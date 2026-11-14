using System.Runtime.InteropServices;
using System.Text.Json;
namespace SoundpadWeb;

// The on-disk side of HistoryStore: the session folder, its small index file, and what happens to a
// session after the process that wrote it is gone (recover, continue after an update, or discard).
//
// root/history/<sessionId>/index.json        rate, spans, gaps, segments and pins; replaced atomically
// root/history/<sessionId>/seg-00000012.pcm  raw 16-bit little-endian mono, segment 12 from its first frame
//
// The index is written after the audio it describes, at segment boundaries, on Stop, on Pin and on Flush.
// A crash therefore loses at most the audio since the last of those (under one segment in normal use).

internal sealed record HistoryIndexSpan(long StartFrame, long WallStartUtcMs);
internal sealed record HistoryIndexSegment(long Index, int Frames);
internal sealed record HistoryIndexPin(string Owner, long StartFrame, long EndFrame);
internal sealed record HistoryIndexFile(int Format, string SessionId, int Rate, int SegmentFrames, long Live, long CreatedUtcMs, long UpdatedUtcMs, int WindowSeconds, bool Closed, HistoryIndexSpan[] Spans, HistoryGap[] Gaps, HistoryIndexSegment[] Segments, HistoryIndexPin[] Pins);

public sealed partial class HistoryStore
{
    private const int IndexFormat = 1;
    private const long MaxIndexBytes = 8 * 1024 * 1024;
    private const string IndexName = "index.json", IndexTempName = "index.json.tmp", HistoryFolderName = "history";
    /// <summary>Session folders a store in this process has open, so Recover and Discard leave them alone.</summary>
    private static readonly HashSet<string> InUse = new(StringComparer.OrdinalIgnoreCase);

    private string IndexPath => Path.Combine(Folder, IndexName);
    private string SegmentPath(long index) => Path.Combine(Folder, SegmentName(index));
    private static string SegmentName(long index) => $"seg-{index:D8}.pcm";
    private static bool IsSegmentName(string name, out long index)
    {
        index = -1;
        return name.Length == 16 && name.StartsWith("seg-", StringComparison.Ordinal) && name.EndsWith(".pcm", StringComparison.Ordinal)
            && name.AsSpan(4, 8).IndexOfAnyExceptInRange('0', '9') < 0 && long.TryParse(name.AsSpan(4, 8), out index);
    }
    /// <summary>The only file names this module ever creates in a session folder.</summary>
    private static bool IsOwnName(string name) => name is IndexName or IndexTempName || IsSegmentName(name, out _);
    private static bool IsSessionId(string? id) => id is { Length: >= 1 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string HistoryRoot(string root) => Path.GetFullPath(Path.Combine(root, HistoryFolderName));
    private static string SessionFolder(string root, string sessionId)
    {
        // The id becomes a folder name, so it must not be able to leave root/history.
        if (!IsSessionId(sessionId)) throw new ArgumentException("A session id is 1 to 64 letters, digits, '-' or '_'.", nameof(sessionId));
        return Path.Combine(HistoryRoot(root), sessionId);
    }
    private static void Claim(string folder)
    {
        lock (InUse) if (!InUse.Add(folder)) throw new InvalidDataException("That recording session is already open.");
    }
    private static void Release(string folder)
    {
        lock (InUse) InUse.Remove(folder);
    }
    private static HistoryOptions Checked(HistoryOptions options)
    {
        if (options.Rate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(options), "The sample rate must be 8000 to 192000.");
        if (options.WindowSeconds is < MinWindowSeconds or > MaxWindowSeconds) throw new ArgumentOutOfRangeException(nameof(options), $"The recording history can be {MinWindowSeconds} to {MaxWindowSeconds} seconds long.");
        if (options.SegmentSeconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(options), "A segment is 1 to 60 seconds.");
        // A whole segment waits in memory before it is written, so a smaller tail would overrun in normal use.
        if (options.TailSeconds < options.SegmentSeconds * 2 || options.TailSeconds > 600) throw new ArgumentOutOfRangeException(nameof(options), "The in-memory tail must hold at least two segments and at most 600 seconds.");
        if (options.PinBudgetBytes < 0 || options.MaxReadSeconds < 1) throw new ArgumentOutOfRangeException(nameof(options));
        return options;
    }

    /// <summary>
    /// Starts a new, empty session under <paramref name="root"/>/history/. <paramref name="sessionId"/> is
    /// generated when omitted. <paramref name="root"/> is the companion's data folder (or a test's temp folder).
    /// </summary>
    public static HistoryStore Create(string root, HistoryOptions? options = null, string? sessionId = null)
    {
        options = Checked(options ?? new());
        sessionId ??= Guid.NewGuid().ToString("N");
        var folder = SessionFolder(root, sessionId);
        var files = options.Files ?? HistoryDiskFiles.Instance;
        Claim(folder);
        try
        {
            files.CreateDirectory(folder);
            if (files.Entries(folder).Count > 0) throw new InvalidDataException("That recording session already exists.");
            var clock = options.UtcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var store = new HistoryStore(folder, sessionId, options, files, options.Rate, checked(options.Rate * options.SegmentSeconds), clock());
            // Written at once so a crash before the first segment still leaves a session Recover can name.
            files.ReplaceAtomically(store.IndexPath, Serialize(store.SnapshotIndexLocked(0, false))); // no other thread exists yet
            store.writer.Start();
            return store;
        }
        catch { Release(folder); throw; }
    }

    /// <summary>
    /// Continues a session left on disk by a crash or an update. Segment sizes are checked against the index, a
    /// torn newest segment is dropped (the timeline then ends where intact audio ends), peaks are rebuilt from
    /// the audio, pins are restored and a gap with <paramref name="gapReason"/> ("update" or "crash") is opened
    /// at the live frame, unless a gap was already open there (capture had been stopped), which is kept as it is.
    /// The sample rate and segment length come from the index; those two options are ignored.
    /// See <see cref="Recovery"/> for what was found. Throws InvalidDataException when the index is unusable.
    /// </summary>
    public static HistoryStore Open(string root, string sessionId, string gapReason, HistoryOptions? options = null)
    {
        if (!HistoryGapReason.IsKnown(gapReason)) throw new ArgumentException("Unknown gap reason.", nameof(gapReason));
        options ??= new();
        var folder = SessionFolder(root, sessionId);
        var files = options.Files ?? HistoryDiskFiles.Instance;
        Claim(folder);
        try
        {
            var index = LoadIndex(files, folder, sessionId);
            options = Checked(options with { Rate = index.Rate, SegmentSeconds = index.SegmentFrames / index.Rate });
            var store = new HistoryStore(folder, sessionId, options, files, index.Rate, index.SegmentFrames, index.CreatedUtcMs);
            store.Restore(index, gapReason);
            store.writer.Start();
            return store;
        }
        catch { Release(folder); throw; }
    }

    /// <summary>
    /// Lists the sessions left under <paramref name="root"/>/history/, newest first, for "Recover previous
    /// session or Discard". Reads index files only; changes nothing.
    /// </summary>
    public static IReadOnlyList<HistoryRecoverable> Recover(string root, IHistoryFiles? files = null)
    {
        files ??= HistoryDiskFiles.Instance;
        var result = new List<HistoryRecoverable>();
        foreach (var entry in files.Entries(HistoryRoot(root)))
        {
            if (entry.Kind != HistoryEntryKind.Directory || !IsSessionId(entry.Name)) continue;
            var folder = Path.Combine(HistoryRoot(root), entry.Name);
            bool inUse; lock (InUse) inUse = InUse.Contains(folder);
            long bytes = 0;
            try
            {
                foreach (var file in files.Entries(folder)) if (file.Kind == HistoryEntryKind.File && IsSegmentName(file.Name, out _)) bytes += Math.Max(0, files.Length(Path.Combine(folder, file.Name)));
                var index = LoadIndex(files, folder, entry.Name);
                result.Add(new(entry.Name, true, null, index.Rate, index.Live, index.Segments.Sum(s => (long)s.Frames), bytes, index.CreatedUtcMs, index.UpdatedUtcMs, index.Closed, index.Pins.Length, inUse));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                result.Add(new(entry.Name, false, ex.Message, 0, 0, 0, bytes, 0, 0, false, 0, inUse));
            }
        }
        return [.. result.OrderByDescending(r => r.UpdatedUtcMs)];
    }

    /// <summary>
    /// Deletes one session folder. Before anything is removed, every entry in it must be a plain file with a
    /// name this module creates; a folder, a link or any other file stops the whole deletion with
    /// InvalidDataException (it is then not this module's folder to clear). Never recurses. A session that
    /// does not exist is not an error; one that is open in this process is refused.
    /// </summary>
    public static void Discard(string root, string sessionId, IHistoryFiles? files = null)
    {
        files ??= HistoryDiskFiles.Instance;
        var folder = SessionFolder(root, sessionId);
        lock (InUse) if (InUse.Contains(folder)) throw new InvalidDataException("That recording session is in use.");
        var self = files.Entries(HistoryRoot(root)).FirstOrDefault(e => string.Equals(e.Name, sessionId, StringComparison.OrdinalIgnoreCase));
        if (self is null) return;
        if (self.Kind != HistoryEntryKind.Directory) throw new InvalidDataException("That recording session is not an ordinary folder, so nothing was deleted.");
        var entries = files.Entries(folder);
        if (entries.FirstOrDefault(e => e.Kind != HistoryEntryKind.File || !IsOwnName(e.Name)) is { } foreign)
            throw new InvalidDataException($"The recording session folder holds something this app did not put there (\"{foreign.Name}\"), so nothing was deleted.");
        foreach (var e in entries) files.Delete(Path.Combine(folder, e.Name));
        files.RemoveDirectoryIfEmpty(folder);
    }

    /// <summary>Clean close: remove the files this store wrote, then the folder if nothing else is in it.</summary>
    private static bool DeleteOwnFiles(IHistoryFiles files, string folder)
    {
        try
        {
            foreach (var e in files.Entries(folder)) if (e.Kind == HistoryEntryKind.File && IsOwnName(e.Name)) files.Delete(Path.Combine(folder, e.Name));
            return files.RemoveDirectoryIfEmpty(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static byte[] Serialize(HistoryIndexFile index) => JsonSerializer.SerializeToUtf8Bytes(index, Json.Options);

    /// <summary>
    /// The index as it will be true once every frame below <paramref name="durable"/> is on disk. Audio past
    /// that point is not described, so a crash can never leave an index that promises audio the disk lacks.
    /// </summary>
    private HistoryIndexFile SnapshotIndexLocked(long durable, bool cleanlyClosed)
    {
        var spanList = spans.Where(s => s.StartFrame < durable).Select(s => new HistoryIndexSpan(s.StartFrame, s.WallStartUtcMs)).ToArray();
        var gapList = gaps.Where(g => g.AtFrame < durable).ToList();
        // A gap whose following run has nothing durable yet is, as far as the disk knows, still open.
        if (gaps.FirstOrDefault(g => g.AtFrame == durable) is { } pending) gapList.Add(pending with { WallToUtcMs = null });
        else if (openGap is { } open && open.AtFrame == durable) gapList.Add(open);
        var segmentList = new List<HistoryIndexSegment>();
        foreach (var s in segments.Values)
        {
            long frames = Math.Min(s.Filled, durable - s.Start);
            if (frames > 0) segmentList.Add(new(s.Index, (int)frames));
        }
        // A pin reaching into audio that is not durable yet is recorded up to the durable point.
        var pinList = pins.Where(p => p.Value.StartFrame < durable).Select(p => new HistoryIndexPin(p.Key, p.Value.StartFrame, Math.Min(p.Value.EndFrame, durable))).ToArray();
        return new(IndexFormat, SessionId, Rate, segmentFrames, durable, createdUtcMs, clock(), windowSeconds, cleanlyClosed, spanList, [.. gapList], [.. segmentList], pinList);
    }

    private static HistoryIndexFile LoadIndex(IHistoryFiles files, string folder, string sessionId)
    {
        var path = Path.Combine(folder, IndexName);
        long length = files.Length(path);
        if (length < 0) throw new InvalidDataException("That recording session has no index.");
        if (length > MaxIndexBytes) throw new InvalidDataException("That recording session's index is too large.");
        var bytes = new byte[length];
        HistoryIndexFile? index = null;
        try { if (files.Read(path, 0, bytes) == length) index = JsonSerializer.Deserialize<HistoryIndexFile>(bytes, Json.Options); }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException) { }
        if (index is null || !Sound(index, sessionId)) throw new InvalidDataException("That recording session's index is damaged.");
        return index;
    }

    /// <summary>Everything Restore relies on. The index is this module's own file, but a damaged one must not crash or mislead it.</summary>
    private static bool Sound(HistoryIndexFile x, string sessionId)
    {
        if (x.Format != IndexFormat || x.SessionId != sessionId || x.Rate is < 8000 or > 192000 || x.Live < 0) return false;
        if (x.SegmentFrames <= 0 || x.SegmentFrames % x.Rate != 0 || x.SegmentFrames / x.Rate > 60) return false;
        if (x.Spans is null || x.Gaps is null || x.Segments is null || x.Pins is null || x.Pins.Length > MaxPins) return false;
        for (int i = 0; i < x.Spans.Length; i++)
        {
            var s = x.Spans[i];
            if (s is null || s.StartFrame < 0 || s.StartFrame >= x.Live) return false;
            if (i > 0 && (s.StartFrame <= x.Spans[i - 1].StartFrame || s.WallStartUtcMs < x.Spans[i - 1].WallStartUtcMs)) return false;
        }
        for (int i = 0; i < x.Gaps.Length; i++)
        {
            var g = x.Gaps[i];
            if (g is null || !HistoryGapReason.IsKnown(g.Reason) || g.AtFrame < 0 || g.AtFrame > x.Live || g.WallToUtcMs < g.WallFromUtcMs) return false;
            if (i > 0 && g.AtFrame <= x.Gaps[i - 1].AtFrame) return false;
            // Only the gap at the live frame can still be open, and a closed one cannot sit there.
            if ((g.WallToUtcMs is null) != (g.AtFrame == x.Live)) return false;
        }
        long tail = x.Live / x.SegmentFrames;
        for (int i = 0; i < x.Segments.Length; i++)
        {
            var s = x.Segments[i];
            if (s is null || s.Index < 0 || s.Index > tail || (i > 0 && s.Index <= x.Segments[i - 1].Index)) return false;
            if (s.Frames != Math.Min(x.SegmentFrames, x.Live - s.Index * x.SegmentFrames)) return false;
        }
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in x.Pins)
            if (p is null || string.IsNullOrEmpty(p.Owner) || p.Owner.Length > MaxOwnerKeyLength || p.StartFrame < 0 || p.EndFrame <= p.StartFrame || p.EndFrame > x.Live || !owners.Add(p.Owner)) return false;
        return true;
    }

    private Segment? LoadSegment(long index, int frames)
    {
        var path = SegmentPath(index);
        bool partial = frames < segmentFrames;
        // A shorter file than the index promised is a torn write. A longer one only has bytes the index never
        // described (written after its last update); they are ignored.
        if (files.Length(path) < frames * 2L) return null;
        var samples = GC.AllocateUninitializedArray<short>(partial ? segmentFrames : frames);
        try { if (files.Read(path, 0, MemoryMarshal.AsBytes(samples.AsSpan(0, frames))) != frames * 2) return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var segment = new Segment(index, index * segmentFrames, segmentFrames) { Filled = frames, Flushed = frames, Samples = partial ? samples : null };
        // Peaks are not stored; rebuilding them reads each kept segment once (about 100 ms for ten minutes at 48 kHz).
        segment.Peaks.Add(0, samples.AsSpan(0, frames));
        return segment;
    }

    private void Restore(HistoryIndexFile index, string reason)
    {
        var good = new SortedDictionary<long, Segment>();
        var listed = new HashSet<long>();
        int droppedSegments = 0;
        foreach (var s in index.Segments)
        {
            listed.Add(s.Index);
            if (LoadSegment(s.Index, s.Frames) is { } loaded) good[s.Index] = loaded; else droppedSegments++;
        }
        // The newest audio must be intact for the timeline to end there: step back over torn newest segments.
        long recovered = index.Live; bool torn = false;
        while (recovered > 0 && listed.Contains((recovered - 1) / segmentFrames) && !good.ContainsKey((recovered - 1) / segmentFrames))
        {
            recovered = (recovered - 1) / segmentFrames * segmentFrames; torn = true;
        }
        long start = recovered;
        while (start > 0 && good.ContainsKey((start - 1) / segmentFrames)) start = (start - 1) / segmentFrames * segmentFrames;

        var droppedPins = new List<string>();
        foreach (var p in index.Pins)
        {
            long end = Math.Min(p.EndFrame, recovered);
            bool whole = end > p.StartFrame;
            for (long i = p.StartFrame / segmentFrames; whole && i <= (end - 1) / segmentFrames; i++) whole = good.ContainsKey(i);
            if (!whole) { droppedPins.Add(p.Owner); continue; }
            pins[p.Owner] = new(p.StartFrame, end);
            for (long i = p.StartFrame / segmentFrames; i <= (end - 1) / segmentFrames; i++) good[i].PinCount++;
        }
        // Intact audio that is neither in the unbroken newest stretch nor pinned is treated as expired.
        foreach (var (i, s) in good) if (s.Start >= start || s.PinCount > 0) segments[i] = s;
        live = recovered; windowStart = start;

        foreach (var s in index.Spans) if (s.StartFrame < recovered) spans.Add(new(s.StartFrame, s.WallStartUtcMs));
        foreach (var g in index.Gaps)
        {
            if (g.AtFrame < recovered) gaps.Add(g with { WallToUtcMs = g.WallToUtcMs ?? g.WallFromUtcMs });
            else if (g.AtFrame == recovered && openGap is null) openGap = g with { WallToUtcMs = null };
        }
        // Capture that was already stopped keeps its own reason: the update or crash lost no audio then.
        if (openGap is null && spans.Count > 0) openGap = new(live, WallOfLiveLocked(), null, reason);
        // 2.41: audio always lies inside a span, so frames held before the first span (or held with no span at all)
        // are the silenced start of a segment whose earlier audio was discarded (DiscardBefore): they stay gone.
        if (segments.Count > 0 && (spans.Count == 0 || spans[0].StartFrame > segments.First().Value.Start))
            floor = spans.Count == 0 ? live : spans[0].StartFrame;
        ExpireLocked(); PruneTimelineLocked();
        deletes.Clear();
        Recovery = new(recovered, index.Live - recovered, droppedSegments, torn, [.. droppedPins], openGap?.Reason);

        // New index first, then remove what it no longer names: files of dropped segments and a leftover temp file.
        files.ReplaceAtomically(IndexPath, Serialize(SnapshotIndexLocked(live, false)));
        foreach (var e in files.Entries(Folder))
        {
            if (e.Kind != HistoryEntryKind.File) continue;
            if (e.Name == IndexTempName || (IsSegmentName(e.Name, out long i) && !segments.ContainsKey(i))) files.Delete(Path.Combine(Folder, e.Name));
        }
        // Appending continues inside the newest segment's file, so stale bytes after the recovered frames must go.
        if (segments.TryGetValue(live / segmentFrames, out var tail) && files.Length(SegmentPath(tail.Index)) > tail.Filled * 2L) files.Truncate(SegmentPath(tail.Index), tail.Filled * 2L);
    }
}
