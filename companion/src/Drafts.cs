using System.Text.Json;
namespace SoundpadWeb;

/// <summary>
/// A draft (2.40): one person's editable selection on the recording history. It lives in companion memory for the
/// launch (a page reload or a reconnect finds it again) and pins its audio in the <see cref="HistoryStore"/> under
/// "draft:&lt;id&gt;", so ring expiry never removes audio it uses. Discard, "Keep in Temp and close draft", a history
/// Discard and quitting remove it. See reports/OVERHAUL-PLAN.md, "Rolling recording and clip editing".
/// </summary>
public sealed class Draft
{
    public required string Id { get; init; }
    /// <summary>"host", or the guest's participant id (their transport peer id when they sent none).</summary>
    public required string Owner { get; init; }
    public required string OwnerName { get; init; }
    public required string SessionId { get; init; }
    public long Start { get; set; }
    public long End { get; set; }
    public string? Name { get; set; }
    /// <summary>
    /// True when the owner typed <see cref="Name"/>. Otherwise it was made from the words of <see cref="NameRange"/>
    /// that the owner may see, and someone who may not see all of that range is shown a name from their own words.
    /// </summary>
    public bool NameTyped { get; set; }
    public HistoryRange NameRange { get; set; }
    public long Created { get; init; }
    public long Updated { get; set; }
    /// <summary>The Temp clip made from this draft, and the range it was made from (Play and Keep reuse it while the range is unchanged).</summary>
    public string? ClipAssetId { get; set; }
    public HistoryRange ClipRange { get; set; }
    public string PinKey => "draft:" + Id;
}

/// <summary>A person's own draft as their page receives it (draftList, draftCreate, draftUpdate).</summary>
public sealed record DraftView(string Id, long StartFrame, long EndFrame, string? Name, long CreatedUtcMs, long UpdatedUtcMs, string SessionId, bool InTemp);
/// <summary>Someone else's draft, as a labelled band, clipped to what the asker may see. <paramref name="Tag"/> picks a colour; it names nobody.</summary>
public sealed record DraftBand(string Id, string OwnerName, string Tag, long StartFrame, long EndFrame, string? Name);

/// <summary>
/// What the companion itself played into the game, as spans of the recording history (2.40). Loopback capture
/// records the game output, so the companion's own replays come back into the history; Latest phrase skips a phrase
/// that lies entirely inside one of them, and keeps anything else (another person talking at the same time is
/// longer than, or outside, the replay). Frames are the live frame when the play was sent plus the sound's length;
/// a tail allows for capture latency.
/// </summary>
public sealed class OwnReplayLog
{
    private sealed record Entry(string SessionId, long Start, long End, string? Label);
    public const int MaxEntries = 256;
    private readonly List<Entry> entries = [];

    /// <summary>A play reached Soundpad at <paramref name="startFrame"/> of the history; it lasts <paramref name="lengthFrames"/> (plus <paramref name="tailFrames"/>).</summary>
    public void Started(string sessionId, long startFrame, long lengthFrames, long tailFrames, string? label)
    {
        lock (entries)
        {
            // Whatever played before was cut off by this play (Soundpad plays one sound at a time).
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].SessionId == sessionId && entries[i].End > startFrame + tailFrames && entries[i].Start <= startFrame)
                    entries[i] = entries[i] with { End = startFrame + tailFrames };
            entries.Add(new(sessionId, startFrame, startFrame + Math.Max(0, lengthFrames) + tailFrames, label));
            if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
        }
    }

    /// <summary>Playback stopped at <paramref name="atFrame"/>: a replay still running ends there (plus the tail).</summary>
    public void Stopped(string sessionId, long atFrame, long tailFrames)
    {
        lock (entries)
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].SessionId == sessionId && entries[i].Start <= atFrame && entries[i].End > atFrame + tailFrames)
                    entries[i] = entries[i] with { End = atFrame + tailFrames };
    }

    /// <summary>The own-replay spans of one history session, oldest first.</summary>
    public HistoryRange[] Ranges(string sessionId)
    {
        lock (entries) return [.. entries.Where(e => e.SessionId == sessionId && e.End > e.Start).Select(e => new HistoryRange(e.Start, e.End))];
    }

    /// <summary>True when [start, end) lies entirely inside one own replay.</summary>
    public bool Covers(string sessionId, long start, long end)
    {
        lock (entries) return entries.Any(e => e.SessionId == sessionId && e.Start <= start && e.End >= end);
    }

    public void Clear(string? sessionId = null) { lock (entries) entries.RemoveAll(e => sessionId == null || e.SessionId == sessionId); }

    /// <summary>2.41: forgets what was played before <paramref name="frame"/> of one history (that audio was discarded).</summary>
    public void DropBefore(string sessionId, long frame)
    {
        lock (entries)
        {
            entries.RemoveAll(e => e.SessionId == sessionId && e.End <= frame);
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].SessionId == sessionId && entries[i].Start < frame) entries[i] = entries[i] with { Start = frame };
        }
    }
}

/// <summary>Quick Mimic (2.40): "Latest phrase" and "Last 10 seconds", as pure functions over the transcript.</summary>
public static class QuickMimic
{
    public const int RecentSeconds = 10;
    /// <summary>How far back Latest phrase looks.</summary>
    public const int SearchSeconds = 120;
    /// <summary>Padding applied when a phrase is cut (words are stored unpadded; see TranscriptWord).</summary>
    public const int PadBeforeMs = 20, PadAfterMs = 10;

    /// <summary>
    /// The most recent completed phrase among <paramref name="words"/> (already limited to what the asker may see):
    /// a phrase whose last word is followed by a pause, or that ended at least half a second before live. A phrase that
    /// lies entirely inside the companion's own replay (<paramref name="ownReplay"/>) is skipped. The range is padded
    /// and limited to <paramref name="maxFrames"/> (its newest part). Null when there is none.
    /// </summary>
    public static HistoryRange? LatestPhrase(IEnumerable<TranscriptWord> words, long liveFrame, int rate, Func<long, long, bool> ownReplay, long maxFrames)
    {
        var phrases = words.GroupBy(w => w.PhraseId)
            .Select(g => g.OrderBy(w => w.Start).ToList())
            .Where(l => l.Count > 0)
            .Select(l => (Start: l[0].Start, End: l.Max(w => w.End), Last: l[^1]))
            .Where(p => p.End > p.Start && (p.Last.PauseAfter || p.End <= liveFrame - rate / 2))
            .OrderByDescending(p => p.End);
        foreach (var p in phrases)
        {
            if (ownReplay(p.Start, p.End)) continue;
            long start = Math.Max(0, p.Start - (long)PadBeforeMs * rate / 1000), end = Math.Min(liveFrame, p.End + (long)PadAfterMs * rate / 1000);
            if (end - start > maxFrames) start = end - maxFrames;
            return new(start, end);
        }
        return null;
    }

    /// <summary>The last <see cref="RecentSeconds"/> s that <paramref name="available"/> holds before live, within its newest range.</summary>
    public static HistoryRange? Recent(IReadOnlyList<HistoryRange> available, long liveFrame, int rate)
    {
        var newest = available.Where(r => r.StartFrame < liveFrame).OrderBy(r => r.EndFrame).LastOrDefault();
        if (newest.EndFrame <= newest.StartFrame) return null;
        long end = Math.Min(liveFrame, newest.EndFrame), start = Math.Max(newest.StartFrame, end - (long)RecentSeconds * rate);
        return end > start ? new(start, end) : null;
    }
}

/// <summary>A sliding-window limit per key (guest preview, 2.40). Thread-safe.</summary>
public sealed class RateWindow(int limit, TimeSpan window, Func<long>? clock = null)
{
    private readonly Dictionary<string, Queue<long>> taken = [];
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    public bool TryTake(string key)
    {
        lock (taken)
        {
            long at = now();
            if (!taken.TryGetValue(key, out var times)) { if (taken.Count > 4096) taken.Clear(); taken[key] = times = new(); }
            while (times.Count > 0 && times.Peek() <= at - (long)window.TotalMilliseconds) times.Dequeue();
            if (times.Count >= limit) return false;
            times.Enqueue(at);
            return true;
        }
    }
}

/// <summary>
/// Keep and guest storage settings (2.40), in keep-settings.json next to state.json. Older companions never read it,
/// so state.json and the managed-audio journal stay exactly as they were (rolling back keeps working).
/// </summary>
/// <param name="GuestKeep">Guests with the usual friend permissions may Keep clips (default on).</param>
/// <param name="Destination">The host's permanent folder for Keep; null for the companion's own library. Guests never choose one.</param>
/// <param name="TempMb">Each guest's Temp (this session) allowance.</param>
/// <param name="KeptMb">Each guest's permanent allowance: per session, or everything they ever kept (<paramref name="BudgetMode"/> "total").</param>
public sealed record KeepSettings(int Format = 1, bool GuestKeep = true, string? Destination = null, int? CategoryId = null, string? CategoryName = null, int TempMb = KeepSettings.DefaultTempMb, int KeptMb = KeepSettings.DefaultKeptMb, string BudgetMode = "session")
{
    public const int DefaultTempMb = 200, DefaultKeptMb = 100, MaxTempMb = 4096, MaxKeptMb = 65536;
    public static bool ValidMode(string? mode) => mode is "session" or "total";

    /// <summary>Reads the file, field by field: anything out of range falls back to its default.</summary>
    public static KeepSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return new();
            var read = JsonSerializer.Deserialize<KeepSettings>(File.ReadAllText(path), Json.Options);
            if (read == null) return new();
            return new(1, read.GuestKeep,
                read.Destination is { Length: > 0 and <= 1024 } d && Path.IsPathFullyQualified(d) && !d.StartsWith(@"\\", StringComparison.Ordinal) ? d : null,
                read.CategoryId is >= 0 and <= 100000 ? read.CategoryId : null,
                read.CategoryId is >= 0 and <= 100000 && read.CategoryName is { Length: <= 256 } n ? n : null,
                read.TempMb is >= 1 and <= MaxTempMb ? read.TempMb : DefaultTempMb,
                read.KeptMb is >= 1 and <= MaxKeptMb ? read.KeptMb : DefaultKeptMb,
                ValidMode(read.BudgetMode) ? read.BudgetMode : "session");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Json.Write(this));
        File.Move(temporary, path, true);
    }
}

/// <summary>A guest's storage as the host's guest list shows it (budgetStatus, 2.40).</summary>
public sealed record GuestUsageView(string Id, string Name, long TempBytes, long TempLimitBytes, long KeptBytes, long KeptLimitBytes, bool Paused, string? Waiting);
/// <summary>A guest waiting for the host to allow more (same list style as the lobby).</summary>
public sealed record BudgetRequestView(string GuestId, string Name, string Kind, long NeededBytes, long At);

/// <summary>
/// Separate per-guest budgets for Temp (this session) and permanent (kept) audio (2.40). Pure bookkeeping; the
/// Bridge asks before every guest clip, copy, upload and Keep. When a guest reaches a budget, their new clips and
/// copies pause ("Waiting for the host to allow more") and the host gets a request; an allowance the host grants
/// lasts until the companion closes. Running out never deletes anything: it only stops future writes.
/// Totals kept across sessions (mode "total") are kept in their own small file.
/// </summary>
public sealed class GuestBudgets(string? totalsPath = null, Func<long>? clock = null)
{
    public const long MiB = 1024 * 1024;
    public const int MaxGuests = 512;
    public const string Waiting = "Waiting for the host to allow more.";
    private sealed class Guest
    {
        public string Name = "Guest";
        public long SessionKept, ExtraTemp, ExtraKept;
        public BudgetRequestView? Request;
    }
    private readonly Dictionary<string, Guest> guests = [];
    private readonly Dictionary<string, (string Guest, long Bytes)> tempAssets = [];
    private Dictionary<string, long>? totals;
    private readonly Func<long> now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    private void Changed() => Interlocked.Increment(ref revision);

    private Guest For(string id, string? name)
    {
        if (!guests.TryGetValue(id, out var g))
        {
            if (guests.Count >= MaxGuests) guests.Remove(guests.First(p => p.Value.Request == null).Key);
            guests[id] = g = new();
        }
        if (!string.IsNullOrWhiteSpace(name)) g.Name = name.Length > 40 ? name[..40] : name;
        return g;
    }

    private Dictionary<string, long> Totals
    {
        get
        {
            if (totals != null) return totals;
            totals = [];
            try
            {
                if (totalsPath != null && File.Exists(totalsPath) && new FileInfo(totalsPath).Length < 256 * 1024 && JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(totalsPath), Json.Options) is { } read)
                    foreach (var (key, bytes) in read.Take(MaxGuests)) if (key.Length is > 0 and <= 128 && bytes >= 0) totals[key] = bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return totals;
        }
    }
    private void SaveTotals()
    {
        if (totalsPath == null) return;
        try { var temporary = totalsPath + ".tmp"; File.WriteAllText(temporary, Json.Write(Totals)); File.Move(temporary, totalsPath, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the session figures still apply */ }
    }

    private long TempUsed(string guest, Func<string, bool> stillTemporary) => tempAssets.Where(p => p.Value.Guest == guest && stillTemporary(p.Key)).Sum(p => p.Value.Bytes);
    private long KeptUsed(string guest, Guest g, KeepSettings settings) => settings.BudgetMode == "total" ? Totals.GetValueOrDefault(guest) : g.SessionKept;
    private static long TempLimit(Guest g, KeepSettings settings) => settings.TempMb * MiB + g.ExtraTemp;
    private static long KeptLimit(Guest g, KeepSettings settings) => settings.KeptMb * MiB + g.ExtraKept;

    private string Ask(string guest, Guest g, string kind, long needed, string message)
    {
        if (g.Request is not { } open || open.Kind != kind || open.NeededBytes != needed) { g.Request = new(guest, g.Name, kind, needed, now()); Changed(); }
        return message;
    }

    /// <summary>
    /// Before a guest's new clip, copy or upload of <paramref name="bytes"/>: null when it fits, else the refusal
    /// (and the host gets a request). A guest at their permanent budget is paused for new clips too.
    /// </summary>
    public string? CheckCreate(string guest, string? name, long bytes, KeepSettings settings, Func<string, bool> stillTemporary)
    {
        lock (guests)
        {
            var g = For(guest, name);
            long kept = KeptUsed(guest, g, settings), keptLimit = KeptLimit(g, settings);
            // At the permanent budget, or waiting for the host to allow more of it: new clips and copies pause too.
            if (kept >= keptLimit || g.Request is { Kind: "kept" }) return Ask(guest, g, "kept", Math.Max(g.Request is { Kind: "kept" } open ? open.NeededBytes : 0, kept - keptLimit + 1), Waiting + " You have used your permanent allowance.");
            long used = TempUsed(guest, stillTemporary), limit = TempLimit(g, settings);
            if (used + bytes > limit) return Ask(guest, g, "temp", used + bytes - limit, Waiting + " Your Temp space is full; you can also remove some of your own clips.");
            // It fits again (the guest removed clips): a Temp request still open is no longer needed.
            if (g.Request is { Kind: "temp" }) { g.Request = null; Changed(); }
            return null;
        }
    }

    /// <summary>Before a guest keeps <paramref name="bytes"/> permanently: null when it fits, else the refusal (and a request).</summary>
    public string? CheckKeep(string guest, string? name, long bytes, KeepSettings settings)
    {
        lock (guests)
        {
            var g = For(guest, name);
            long kept = KeptUsed(guest, g, settings), limit = KeptLimit(g, settings);
            if (kept + bytes > limit) return Ask(guest, g, "kept", kept + bytes - limit, Waiting + " Keeping this would pass your permanent allowance.");
            return null;
        }
    }

    public void NoteTemp(string guest, string? name, string assetId, long bytes)
    {
        lock (guests)
        {
            For(guest, name);
            if (tempAssets.Count > 8192) tempAssets.Remove(tempAssets.Keys.First());
            tempAssets[assetId] = (guest, bytes); Changed();
        }
    }

    public void NoteKept(string guest, string? name, long bytes)
    {
        lock (guests)
        {
            var g = For(guest, name);
            g.SessionKept += bytes;
            if (Totals.Count < MaxGuests || Totals.ContainsKey(guest)) { Totals[guest] = Totals.GetValueOrDefault(guest) + bytes; SaveTotals(); }
            Changed();
        }
    }

    /// <summary>The guest who made a Temp asset in this launch, if a guest did.</summary>
    public string? OwnerOf(string assetId) { lock (guests) return tempAssets.TryGetValue(assetId, out var entry) ? entry.Guest : null; }

    /// <summary>The host allows more: the guest's open request is met, for as long as the companion runs.</summary>
    public bool Grant(string guest, KeepSettings settings)
    {
        lock (guests)
        {
            if (!guests.TryGetValue(guest, out var g) || g.Request is not { } request) return false;
            if (request.Kind == "temp") g.ExtraTemp += Math.Max(settings.TempMb * MiB, request.NeededBytes);
            else g.ExtraKept += Math.Max(settings.KeptMb * MiB, request.NeededBytes);
            g.Request = null; Changed();
            return true;
        }
    }

    /// <summary>The host said not now: the request goes, the guest stays paused until a later one is granted.</summary>
    public bool Deny(string guest)
    {
        lock (guests)
        {
            if (!guests.TryGetValue(guest, out var g) || g.Request == null) return false;
            g.Request = null; Changed();
            return true;
        }
    }

    public BudgetRequestView[] Requests() { lock (guests) return [.. guests.Values.Select(g => g.Request).OfType<BudgetRequestView>().OrderBy(r => r.At)]; }

    public GuestUsageView Usage(string guest, KeepSettings settings, Func<string, bool> stillTemporary)
    {
        lock (guests)
        {
            var g = guests.GetValueOrDefault(guest) ?? new Guest();
            long kept = KeptUsed(guest, g, settings), keptLimit = KeptLimit(g, settings);
            return new(guest, g.Name, TempUsed(guest, stillTemporary), TempLimit(g, settings), kept, keptLimit, kept >= keptLimit || g.Request != null, g.Request?.Kind);
        }
    }

    public GuestUsageView[] All(KeepSettings settings, Func<string, bool> stillTemporary)
    {
        string[] ids; lock (guests) ids = [.. guests.Keys];
        return [.. ids.Select(id => Usage(id, settings, stillTemporary))];
    }

    /// <summary>The settings changed (new budgets): the next check decides again.</summary>
    public void Touch() => Changed();
}
