using System.Runtime.InteropServices;
namespace SoundpadWeb;

/// <summary>
/// Disk-backed rolling recording history for one companion session (reports/OVERHAUL-PLAN.md, "Rolling
/// recording and clip editing" and "Data lifecycle"). Since 2.39 Mimic records into it (BridgeHistory.cs).
///
/// Audio is mono, stored as 16-bit PCM in fixed-length segment files under root/history/sessionId/. Frames
/// are absolute for the whole session; a Stop, a lost device or an update leaves a zero-width
/// <see cref="HistoryGap"/> instead of resetting the counter. Append only copies into memory and returns, a
/// background thread does every disk write, and readers see audio that has not reached the disk yet.
///
/// Threads: one appender (the capture thread), the background writer, any number of readers.
/// The lock is never held during disk I/O.
/// </summary>
public sealed partial class HistoryStore : IDisposable
{
    public const int DefaultWindowSeconds = 600, MinWindowSeconds = 60, MaxWindowSeconds = 3600;
    /// <summary>Peak answers are exact for the asked range rounded outward to blocks of this many frames (counted from each segment's start).</summary>
    public const int PeakBlockFrames = HistoryPeakPyramid.BlockFrames;
    public const int MaxBins = 4096, MaxPins = 1024, MaxOwnerKeyLength = 128;
    private const long MaxLevelFrames = 1L << 40;

    private sealed class Segment(long index, long start, int frames)
    {
        public readonly long Index = index, Start = start;
        /// <summary>Rebuilt (2.41) when a discard zeroes the start of this segment, so no peak describes removed audio.</summary>
        public HistoryPeakPyramid Peaks = new(frames);
        /// <summary>In memory until the whole segment is on disk; the partial newest segment always is.</summary>
        public short[]? Samples;
        public int Filled, Flushed, PinCount;
        public bool Dropped;
    }
    private readonly record struct SpanStart(long StartFrame, long WallStartUtcMs);
    private readonly record struct Piece(Segment Segment, short[]? Memory, int Offset, int Count);
    private sealed class Work
    {
        public readonly List<(Segment Segment, int From, int To, short[] Data)> Writes = [];
        public long[] Deletes = [];
        /// <summary>2.41: the first frames of these segments' files are overwritten with silence (a discard).</summary>
        public (long Index, int Frames)[] Zeroes = [];
        public byte[] Index = [];
        public long FlushTarget;
        public bool Final, Written;
    }

    private readonly object gate = new();
    private readonly IHistoryFiles files;
    private readonly Func<long> clock;
    private readonly int segmentFrames;
    private readonly long segmentBytes, tailCapFrames, pinBudgetBytes, maxReadFrames, createdUtcMs;
    private readonly SortedDictionary<long, Segment> segments = [];
    private readonly List<SpanStart> spans = [];
    private readonly List<HistoryGap> gaps = [];
    private readonly Dictionary<string, HistoryRange> pins = new(StringComparer.Ordinal);
    private readonly List<long> deletes = [];
    private readonly List<(long Index, int Frames)> zeroes = [];
    private readonly Thread writer;
    private readonly AutoResetEvent wake = new(false);
    /// <summary>The gap at the live frame while no run is open; it closes when audio arrives again.</summary>
    private HistoryGap? openGap;
    private bool running, indexDirty, closed, closing, abandoned, finalWritten;
    private long live, windowStart, windowFrames, unflushed, overruns, droppedFrames, flushRequested, flushCompleted;
    /// <summary>
    /// 2.41: nothing before this frame exists any more (<see cref="DiscardBefore"/>). On disk it is where the first
    /// span starts; the frames of its segment before it are silence, so even a companion that does not know this
    /// field (an older one after a rollback) can only find silence there.
    /// </summary>
    private long floor;
    private int windowSeconds;
    private string? writeError;
    /// <summary>The next segment's buffer, allocated ahead by the writer so the capture thread rarely allocates a megabyte itself.</summary>
    private short[]? spare;

    private HistoryStore(string folder, string sessionId, HistoryOptions options, IHistoryFiles files, int rate, int segmentFrames, long createdUtcMs)
    {
        Folder = folder; SessionId = sessionId; Rate = rate; this.files = files; this.segmentFrames = segmentFrames; this.createdUtcMs = createdUtcMs;
        clock = options.UtcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        segmentBytes = segmentFrames * 2L;
        windowSeconds = options.WindowSeconds; windowFrames = (long)options.WindowSeconds * rate;
        tailCapFrames = (long)options.TailSeconds * rate; pinBudgetBytes = options.PinBudgetBytes; maxReadFrames = (long)options.MaxReadSeconds * rate;
        writer = new Thread(WriterLoop) { IsBackground = true, Name = "History writer" };
    }

    public string SessionId { get; }
    /// <summary>The app-owned folder holding this session's index and segment files.</summary>
    public string Folder { get; }
    public int Rate { get; }
    /// <summary>The next frame to be captured. Record this when a guest is admitted and pass it to <see cref="HistoryAccess.Guest"/>.</summary>
    public long LiveFrame { get { lock (gate) return live; } }
    public int WindowSeconds { get { lock (gate) return windowSeconds; } }
    /// <summary>Frames per segment: pins keep whole segments.</summary>
    public int SegmentFrames => segmentFrames;
    /// <summary>The bytes all pins together may keep (<see cref="HistoryOptions.PinBudgetBytes"/>).</summary>
    public long PinBudgetBytes => pinBudgetBytes;
    /// <summary>Set when this store came from <see cref="Open"/>; null for a new session.</summary>
    public HistoryRecovery? Recovery { get; private set; }

    // ---- Capture side ------------------------------------------------------------------------------------

    /// <summary>
    /// Adds mono samples at the live frame and returns how many were kept. Never waits on the disk. The first
    /// call after a gap (or the first ever) opens a run anchored at <paramref name="wallUtcMs"/> (the wall time
    /// of the first sample; the clock when omitted). When the background writer is so far behind that the
    /// in-memory tail is full, the rest is dropped, an "overrun" gap is recorded and fewer frames are returned.
    /// </summary>
    public int Append(ReadOnlySpan<float> mono, long? wallUtcMs = null) => (int)AppendCore(mono, mono.Length, wallUtcMs);

    /// <summary>
    /// Adds <paramref name="frames"/> of digital silence, exactly like <see cref="Append"/> with zeros but without
    /// a buffer. Loopback capture delivers nothing while its source is quiet, so the capture layer keeps the
    /// timeline moving with this. Call it in small steps (every second or so): one call larger than the
    /// in-memory tail is cut short and recorded as an overrun, like any other audio that arrives too fast.
    /// </summary>
    public long AppendSilence(long frames, long? wallUtcMs = null) => frames <= 0 ? 0 : AppendCore(default, frames, wallUtcMs);

    /// <summary>The one append path. An empty <paramref name="mono"/> means <paramref name="frames"/> of silence.</summary>
    private long AppendCore(ReadOnlySpan<float> mono, long frames, long? wallUtcMs)
    {
        if (frames == 0) return 0;
        bool signal = false; long accepted;
        lock (gate)
        {
            if (closed) return 0;
            accepted = Math.Clamp(tailCapFrames - unflushed, 0, frames);
            if (accepted > 0)
            {
                if (!running) BeginRunLocked(wallUtcMs ?? clock());
                for (long done = 0; done < accepted;)
                {
                    long index = live / segmentFrames;
                    if (!segments.TryGetValue(index, out var segment))
                        segments[index] = segment = new Segment(index, index * segmentFrames, segmentFrames) { Samples = Interlocked.Exchange(ref spare, null) ?? GC.AllocateUninitializedArray<short>(segmentFrames) };
                    int offset = (int)(live - segment.Start), count = (int)Math.Min(accepted - done, segmentFrames - offset);
                    var target = segment.Samples!.AsSpan(offset, count); // the newest segment is always in memory
                    if (mono.IsEmpty) target.Clear(); else Quantise(mono.Slice((int)done, count), target);
                    segment.Peaks.Add(offset, target);
                    segment.Filled = offset + count; live += count; unflushed += count; done += count;
                    if (segment.Filled == segmentFrames) signal = true;
                }
                if (ExpireLocked()) signal = true;
            }
            if (accepted < frames)
            {
                droppedFrames += frames - accepted;
                if (running) { overruns++; EndRunLocked(HistoryGapReason.Overrun); signal = true; }
                // Audio lost inside a gap that began for another reason is still lost audio; say so.
                else if (openGap is { Reason: not HistoryGapReason.Overrun } gap) { overruns++; openGap = gap with { Reason = HistoryGapReason.Overrun }; indexDirty = true; }
            }
        }
        if (signal) wake.Set();
        return accepted;
    }

    /// <summary>
    /// Marks that capture stopped (reason from <see cref="HistoryGapReason"/>). History stays readable; the gap
    /// begins at the wall time of the last captured frame and closes at the next Append. Starts a background
    /// flush so the stop is on disk soon; call <see cref="Flush"/> to wait for it. False when no run was open.
    /// </summary>
    public bool EndRun(string reason)
    {
        if (!HistoryGapReason.IsKnown(reason)) throw new ArgumentException("Unknown gap reason.", nameof(reason));
        lock (gate)
        {
            if (closed || !running) return false;
            EndRunLocked(reason);
        }
        wake.Set();
        return true;
    }

    /// <summary>
    /// Waits until everything appended before the call, and the index describing it, is on disk.
    /// False when that did not happen within <paramref name="timeoutMs"/> (a stalled or failing disk).
    /// Never call this from the capture thread.
    /// </summary>
    public bool Flush(int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        lock (gate)
        {
            if (closed) return false;
            long target = ++flushRequested;
            wake.Set();
            while (flushCompleted < target)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0 || closed || !Monitor.Wait(gate, (int)left)) break;
            }
            return flushCompleted >= target;
        }
    }

    /// <summary>Changes the rolling window (60 to 3600 s). Shrinking releases older unpinned audio at once.</summary>
    public void SetWindow(int seconds)
    {
        if (seconds is < MinWindowSeconds or > MaxWindowSeconds) throw new ArgumentOutOfRangeException(nameof(seconds), $"The recording history can be {MinWindowSeconds} to {MaxWindowSeconds} seconds long.");
        lock (gate)
        {
            if (closed) return;
            windowSeconds = seconds; windowFrames = (long)seconds * Rate;
            ExpireLocked(); indexDirty = true;
        }
        wake.Set();
    }

    /// <summary>
    /// Removes everything recorded before <paramref name="frame"/> (2.41: Discard of the history a crash recovered,
    /// while what was captured since stays). Whole segments before it are deleted; the frames of the segment holding
    /// it that lie before it are overwritten with silence, in memory and on disk, and are never readable again. Pins
    /// that reach before it go (their owner keys are returned). Frame numbers, the session id, guests' join frames and
    /// everything from <paramref name="frame"/> on are unchanged. Clamped to the live frame.
    /// </summary>
    public string[] DiscardBefore(long frame)
    {
        var released = new List<string>();
        lock (gate)
        {
            ThrowIfClosedLocked();
            frame = Math.Clamp(frame, 0, live);
            if (frame <= floor) return [];
            floor = frame;
            foreach (var (owner, range) in pins.Where(p => p.Value.StartFrame < frame).ToArray())
            {
                pins.Remove(owner); ReleaseLocked(range); released.Add(owner);
            }
            foreach (var s in segments.Values.ToArray())
            {
                if (s.Start + segmentFrames <= frame) { DropLocked(s); continue; }
                if (s.Start >= frame) continue;
                int cut = (int)Math.Min(frame - s.Start, s.Filled);
                if (s.Samples is { } samples)
                {
                    Array.Clear(samples, 0, cut);
                    s.Peaks = new HistoryPeakPyramid(segmentFrames);
                    s.Peaks.Add(0, samples.AsSpan(0, s.Filled));
                }
                // What is already on disk is overwritten by the writer; the rest is written from the cleared memory.
                if (Math.Min(cut, s.Flushed) is > 0 and int onDisk) zeroes.Add((s.Index, onDisk));
            }
            windowStart = Math.Max(windowStart, frame / segmentFrames * segmentFrames);
            // The timeline starts at the floor: the span holding it begins there (that is what the index records).
            while (spans.Count > 1 && spans[1].StartFrame <= frame) spans.RemoveAt(0);
            if (spans.Count > 0 && spans[0].StartFrame < frame)
            {
                if (frame < live || running) spans[0] = new(frame, spans[0].WallStartUtcMs + (frame - spans[0].StartFrame) * 1000 / Rate);
                else { spans.Clear(); openGap = null; }
            }
            gaps.RemoveAll(g => g.AtFrame <= frame);
            if (openGap is { } open && open.AtFrame < frame) openGap = null;
            indexDirty = true; flushRequested++;
        }
        wake.Set();
        return [.. released];
    }

    // ---- Reading -----------------------------------------------------------------------------------------

    /// <summary>
    /// Samples for [startFrame, startFrame + frameCount) as floats in [-1, 1]. Throws
    /// <see cref="HistoryUnavailableException"/> when any part is outside the caller's access, has expired or
    /// is not recorded yet; the result is never shifted, zero-filled or partial, also when expiry happens
    /// while the read is running.
    /// </summary>
    public float[] Read(HistoryAccess access, long startFrame, int frameCount)
    {
        var pcm = ReadPcm16(access, startFrame, frameCount);
        var result = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++) result[i] = pcm[i] / (float)short.MaxValue;
        return result;
    }

    /// <summary>The same audio as <see cref="Read"/>, as the stored 16-bit samples (what a WAV writer wants).</summary>
    public short[] ReadPcm16(HistoryAccess access, long startFrame, int frameCount)
    {
        if (startFrame < 0 || frameCount <= 0) throw new InvalidDataException("Choose a longer selection.");
        if (frameCount > maxReadFrames) throw new InvalidDataException($"That selection is too long. Choose at most {maxReadFrames / Rate} seconds.");
        var result = new short[frameCount];
        ReadCore(access, startFrame, result);
        return result;
    }

    /// <summary>
    /// Waveform for [startFrame, endFrame) as <paramref name="bins"/> interleaved (min, max) pairs in
    /// [-32767, 32767], the website's waveform shape. Unlike Read this clips instead of refusing: a bin with
    /// no audio the caller may see (expired, not shared, not recorded) is (0, 0); use <see cref="Timeline"/>
    /// to tell that apart from silence. When a bin is at least <see cref="PeakBlockFrames"/> frames wide the
    /// answer comes from the peak pyramid without touching samples and is exact for the bin rounded outward
    /// to 64-frame blocks (rounded inward at the edge of a guest's access, so nothing before it leaks).
    /// Narrower bins (deep zoom, at most 4096 x 64 frames) are computed from the samples and are exact.
    /// </summary>
    public short[] Levels(HistoryAccess access, long startFrame, long endFrame, int bins)
    {
        if (bins is < 1 or > MaxBins) throw new InvalidDataException($"A waveform can have 1 to {MaxBins} bins.");
        if (startFrame < 0 || endFrame <= startFrame || endFrame - startFrame > MaxLevelFrames) throw new InvalidDataException("Choose a longer selection.");
        long frames = endFrame - startFrame;
        var result = new short[bins * 2];
        if (frames < (long)bins * PeakBlockFrames) { LevelsFromSamples(access, startFrame, (int)frames, result); return result; }

        var held = new Dictionary<long, (Segment Segment, int Filled)>();
        long from0;
        lock (gate)
        {
            ThrowIfClosedLocked();
            from0 = floor;
            long first = startFrame / segmentFrames, last = (endFrame - 1) / segmentFrames;
            foreach (var s in segments.Values) if (s.Index >= first && s.Index <= last && s.Filled > 0) held[s.Index] = (s, s.Filled);
        }
        if (held.Count == 0) return result;
        var allowed = PeakRanges(access, from0);
        for (int b = 0; b < bins; b++)
        {
            long from = startFrame + frames * b / bins, to = startFrame + frames * (b + 1) / bins;
            short min = short.MaxValue, max = short.MinValue;
            foreach (var r in allowed)
            {
                long a = Math.Max(from, r.StartFrame), z = Math.Min(to, r.EndFrame);
                if (a >= z) continue;
                for (long index = a / segmentFrames; index <= (z - 1) / segmentFrames; index++)
                {
                    if (!held.TryGetValue(index, out var entry)) continue;
                    long start = entry.Segment.Start;
                    int lo = (int)(Math.Max(a, start) - start), hi = (int)(Math.Min(z, start + entry.Filled) - start);
                    if (lo < hi) entry.Segment.Peaks.Merge(lo, hi, ref min, ref max);
                }
            }
            if (min <= max) { result[b * 2] = Math.Max(min, (short)-short.MaxValue); result[b * 2 + 1] = max; }
        }
        return result;
    }

    /// <summary>Spans, gaps and readable ranges as this access may know them. See <see cref="HistoryTimeline"/>.</summary>
    public HistoryTimeline Timeline(HistoryAccess access)
    {
        lock (gate)
        {
            ThrowIfClosedLocked();
            var hostSpans = new HistorySpan[spans.Count];
            for (int i = 0; i < spans.Count; i++) hostSpans[i] = new(spans[i].StartFrame, i + 1 < spans.Count ? spans[i + 1].StartFrame : live, spans[i].WallStartUtcMs);
            var hostGaps = openGap is null ? gaps : [.. gaps, openGap];
            var available = AvailableLocked();
            if (access.IsHost) return new(Rate, live, spans.Count > 0 ? WallOfLiveLocked() : null, running, windowSeconds, [.. available], hostSpans, [.. hostGaps]);

            var clipped = new List<HistorySpan>();
            foreach (var r in access.Allowed)
                foreach (var s in hostSpans)
                {
                    long a = Math.Max(s.StartFrame, r.StartFrame), z = Math.Min(s.EndFrame, r.EndFrame);
                    if (a < z) clipped.Add(new(a, z, s.WallStartUtcMs + (a - s.StartFrame) * 1000 / Rate));
                }
            var seen = new List<HistoryGap>();
            foreach (var g in hostGaps)
            {
                // A gap on the edge of what a guest may see carries wall times from the hidden side, so only
                // gaps strictly inside are reported as they are.
                if (access.Allowed.Any(r => g.AtFrame > r.StartFrame && g.AtFrame < r.EndFrame)) seen.Add(g);
                // Capture was stopped when they joined: show that from their join time, if the caller told us it.
                else if (g.AtFrame == access.FromFrame && access.FromWallUtcMs is { } joined && (g.WallToUtcMs is null || g.WallToUtcMs > joined) && access.Allowed.Any(r => r.StartFrame == g.AtFrame))
                    seen.Add(g with { WallFromUtcMs = Math.Max(g.WallFromUtcMs, joined) });
            }
            // The wall time of the live edge is the end of the newest run; a guest only learns it when they can see that run.
            long? liveWall = clipped.Count > 0 && clipped[^1].EndFrame == live ? WallOfLiveLocked() : null;
            return new(Rate, live, liveWall, running, windowSeconds, Clip(access, available), [.. clipped], [.. seen]);
        }
    }

    /// <summary>
    /// UTC milliseconds at which <paramref name="frame"/> was heard. A frame where a run starts belongs to that
    /// run; the frame just past visible audio (the live frame, or the end of a shared range) gives the time that
    /// audio ended. "Time behind live" is the clock now minus this. Refused like a read for anything else.
    /// </summary>
    public long WallTimeOf(HistoryAccess access, long frame)
    {
        lock (gate)
        {
            ThrowIfClosedLocked();
            bool Visible(long f) => f >= floor && f < live && access.Allowed.Any(r => f >= r.StartFrame && f < r.EndFrame);
            long probe = Visible(frame) ? frame : Visible(frame - 1) ? frame - 1 : -1;
            if (probe < 0) throw Unavailable(access, access.Allowed.Any(r => frame >= r.StartFrame && frame < r.EndFrame) && frame >= live ? HistoryUnavailableException.NotRecorded : HistoryUnavailableException.NotShared);
            for (int i = spans.Count - 1; i >= 0; i--)
                if (spans[i].StartFrame <= probe) return spans[i].WallStartUtcMs + (frame - spans[i].StartFrame) * 1000 / Rate;
            throw Unavailable(access, HistoryUnavailableException.Expired);
        }
    }

    /// <summary>
    /// The first frame this access may see that was heard at or after <paramref name="utcMs"/>; the live frame
    /// when there is none. A time inside a gap gives the first frame after the gap.
    /// </summary>
    public long FrameAtOrAfterWall(HistoryAccess access, long utcMs)
    {
        lock (gate)
        {
            ThrowIfClosedLocked();
            for (int i = 0; i < spans.Count; i++)
            {
                long start = spans[i].StartFrame, end = i + 1 < spans.Count ? spans[i + 1].StartFrame : live, wall = spans[i].WallStartUtcMs;
                // Smallest frame whose floor-rounded wall time reaches utcMs: ceil((utcMs - wall) * rate / 1000).
                if (utcMs > wall + (end - start) * 1000 / Rate) continue; // after this run; also keeps the product below in range
                long first = utcMs <= wall ? start : start + ((utcMs - wall) * Rate + 999) / 1000;
                foreach (var r in access.Allowed)
                {
                    long candidate = Math.Max(first, r.StartFrame);
                    if (candidate < Math.Min(end, r.EndFrame)) return candidate;
                }
            }
            return live;
        }
    }

    // ---- Pins --------------------------------------------------------------------------------------------

    /// <summary>
    /// Protects [startFrame, endFrame) from expiry for <paramref name="ownerKey"/> (one range per key, e.g. a
    /// draft id), keeping the whole segments that cover it. An existing pin of the same key is replaced in one
    /// step; when this throws, the old pin is untouched. Throws <see cref="HistoryUnavailableException"/> when
    /// the range is not readable by <paramref name="access"/> right now and <see cref="HistoryPinBudgetException"/>
    /// when all pinned segments together would exceed the budget.
    /// </summary>
    public void Pin(HistoryAccess access, string ownerKey, long startFrame, long endFrame) => SetPin(access, ownerKey, startFrame, endFrame, false);

    /// <summary>Like <see cref="Pin"/>, for moving or extending a pin that must already exist (a draft that was discarded meanwhile is not revived).</summary>
    public void ReplacePin(HistoryAccess access, string ownerKey, long startFrame, long endFrame) => SetPin(access, ownerKey, startFrame, endFrame, true);

    /// <summary>Releases a pin; audio it alone kept outside the window expires at once. False when there was none.</summary>
    public bool Unpin(string ownerKey)
    {
        lock (gate)
        {
            if (closed || !pins.Remove(ownerKey, out var old)) return false;
            ReleaseLocked(old); indexDirty = true;
        }
        wake.Set();
        return true;
    }

    /// <summary>Every pin and its range (host only).</summary>
    public IReadOnlyDictionary<string, HistoryRange> Pins(HistoryAccess access)
    {
        RequireHost(access);
        lock (gate) { ThrowIfClosedLocked(); return new Dictionary<string, HistoryRange>(pins, StringComparer.Ordinal); }
    }

    /// <summary>Storage figures (host only; a guest's view of what exists is <see cref="Timeline"/>).</summary>
    public HistoryTotals Totals(HistoryAccess access)
    {
        RequireHost(access);
        lock (gate)
        {
            ThrowIfClosedLocked();
            long pinned = 0, outside = 0;
            foreach (var s in segments.Values)
            {
                if (s.PinCount == 0) continue;
                pinned += segmentBytes;
                if (s.Start < windowStart) outside += s.Filled * 2L;
            }
            return new(live, windowStart, (live - windowStart) * 2, pinned, outside, pinBudgetBytes, windowSeconds, unflushed, overruns, droppedFrames, writeError);
        }
    }

    // ---- Closing -----------------------------------------------------------------------------------------

    /// <summary>
    /// Ends the session. With <paramref name="deleteOnClose"/> (an ordinary quit) the files this store wrote and
    /// its folder are removed; anything else found in the folder is left alone and false is returned. Without
    /// it (an update hand-over) everything is flushed and the index is marked cleanly closed so
    /// <see cref="Open"/> can continue the same timeline. Returns whether that fully succeeded.
    /// </summary>
    public bool Close(bool deleteOnClose)
    {
        lock (gate)
        {
            if (closed) return true;
            closed = true;
            if (deleteOnClose) abandoned = true; else closing = true;
            Monitor.PulseAll(gate);
        }
        wake.Set();
        // A stalled disk must not hang quitting forever.
        bool ok = !writer.IsAlive || writer.Join(deleteOnClose ? 5000 : 15000);
        if (deleteOnClose) ok &= DeleteOwnFiles(files, Folder);
        else lock (gate) ok &= finalWritten;
        Release(Folder);
        return ok;
    }

    /// <summary>Closes without deleting, like an update hand-over. Call <see cref="Close"/> with true for an ordinary quit.</summary>
    public void Dispose() => Close(false);

    /// <summary>Test seam: stops like a killed process. Nothing is flushed and the index is left as it was.</summary>
    internal void Abandon()
    {
        lock (gate) { closed = true; abandoned = true; Monitor.PulseAll(gate); }
        wake.Set();
        if (writer.IsAlive) writer.Join(5000);
        Release(Folder);
    }

    // ---- Internals: timeline -----------------------------------------------------------------------------

    private void BeginRunLocked(long wall)
    {
        // Wall time never runs backwards along the frame axis, whatever the clock did, so searches by time stay valid.
        if (openGap != null) wall = Math.Max(wall, openGap.WallFromUtcMs);
        else if (spans.Count > 0) wall = Math.Max(wall, WallOfLiveLocked());
        if (openGap != null) { gaps.Add(openGap with { WallToUtcMs = wall }); openGap = null; }
        spans.Add(new(live, wall));
        running = true; indexDirty = true;
    }

    private void EndRunLocked(string reason)
    {
        openGap = new(live, WallOfLiveLocked(), null, reason);
        running = false;
        flushRequested++;
    }

    private long WallOfLiveLocked() => spans[^1].WallStartUtcMs + (live - spans[^1].StartFrame) * 1000 / Rate;

    /// <summary>Drops segments wholly older than the window unless pinned. True when files were queued for deletion.</summary>
    private bool ExpireLocked()
    {
        bool any = false;
        for (long cutoff = live - windowFrames; windowStart + segmentFrames <= cutoff; windowStart += segmentFrames)
            if (segments.TryGetValue(windowStart / segmentFrames, out var s) && s.PinCount == 0) { DropLocked(s); any = true; }
        return any;
    }

    private void DropLocked(Segment s)
    {
        segments.Remove(s.Index);
        s.Dropped = true; unflushed -= s.Filled - s.Flushed; s.Samples = null;
        deletes.Add(s.Index);
        PruneTimelineLocked();
    }

    /// <summary>Forgets spans and gaps that only describe audio no longer held, so a long session's index stays small.</summary>
    private void PruneTimelineLocked()
    {
        long oldest = segments.Count > 0 ? segments.First().Key * segmentFrames : live;
        while (spans.Count > 1 && spans[1].StartFrame <= oldest) spans.RemoveAt(0);
        if (spans.Count > 0) gaps.RemoveAll(g => g.AtFrame <= spans[0].StartFrame);
    }

    private List<HistoryRange> AvailableLocked()
    {
        var result = new List<HistoryRange>();
        foreach (var s in segments.Values)
        {
            if (s.Filled == 0 || s.Start + s.Filled <= floor) continue;
            if (result.Count > 0 && result[^1].EndFrame == s.Start) result[^1] = result[^1] with { EndFrame = s.Start + s.Filled };
            else result.Add(new(Math.Max(s.Start, floor), s.Start + s.Filled));
        }
        return result;
    }

    private static HistoryRange[] Clip(HistoryAccess access, List<HistoryRange> ranges)
    {
        if (access.IsHost) return [.. ranges];
        var result = new List<HistoryRange>();
        foreach (var range in ranges)
            foreach (var r in access.Allowed)
            {
                long a = Math.Max(range.StartFrame, r.StartFrame), z = Math.Min(range.EndFrame, r.EndFrame);
                if (a < z) result.Add(new(a, z));
            }
        return [.. result];
    }

    // ---- Internals: access and reads ---------------------------------------------------------------------

    private static void RequireHost(HistoryAccess access)
    {
        if (!access.IsHost) throw new HistoryUnavailableException(HistoryUnavailableException.NotShared, "Only the host can see how recording history is stored.", []);
    }

    private void ThrowIfClosedLocked()
    {
        if (closed) throw new HistoryUnavailableException(HistoryUnavailableException.Closed, "Recording history is closed.", []);
    }

    private HistoryUnavailableException Unavailable(HistoryAccess access, string reason)
    {
        HistoryRange[] available;
        lock (gate) available = closed ? [] : Clip(access, AvailableLocked());
        return new(reason, reason switch
        {
            HistoryUnavailableException.Expired => "That moment has already scrolled out of the recording history. Select it again.",
            HistoryUnavailableException.NotRecorded => "That selection is newer than the recording history.",
            _ => "That part of the recording history has not been shared with you.",
        }, available);
    }

    private List<Piece> LocateLocked(HistoryAccess access, long start, long end)
    {
        // Access first: a guest asking about hidden history gets the same answer whether or not it still exists.
        if (!access.Allows(start, end)) throw Unavailable(access, HistoryUnavailableException.NotShared);
        if (end > live) throw Unavailable(access, HistoryUnavailableException.NotRecorded);
        if (start < floor) throw Unavailable(access, HistoryUnavailableException.Expired);
        var pieces = new List<Piece>();
        for (long frame = start; frame < end;)
        {
            if (!segments.TryGetValue(frame / segmentFrames, out var s)) throw Unavailable(access, HistoryUnavailableException.Expired);
            int offset = (int)(frame - s.Start), count = (int)Math.Min(end - frame, s.Filled - offset);
            pieces.Add(new(s, s.Samples, offset, count));
            frame += count;
        }
        return pieces;
    }

    private void ReadCore(HistoryAccess access, long start, Span<short> destination)
    {
        List<Piece> pieces;
        lock (gate) { ThrowIfClosedLocked(); pieces = LocateLocked(access, start, start + destination.Length); }
        int at = 0;
        foreach (var p in pieces)
        {
            var target = destination.Slice(at, p.Count);
            // A memory piece is the array captured under the lock: the appender only writes past the frames we
            // were given, and dropping the segment later cannot change it.
            if (p.Memory != null) p.Memory.AsSpan(p.Offset, p.Count).CopyTo(target);
            else
            {
                int read;
                try { read = files.Read(SegmentPath(p.Segment.Index), p.Offset * 2L, MemoryMarshal.AsBytes(target)); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { read = -1; }
                if (read != p.Count * 2)
                {
                    // Only retention removes a segment file. Then the honest answer is "expired", never partial audio.
                    bool gone; lock (gate) gone = p.Segment.Dropped || closed;
                    if (gone) throw Unavailable(access, HistoryUnavailableException.Expired);
                    throw new InvalidDataException("A part of the recording history could not be read from disk.");
                }
            }
            at += p.Count;
        }
    }

    /// <summary>What a guest may see, shrunk to whole peak blocks so a block never mixes in hidden frames.</summary>
    /// <summary>
    /// Also shrunk to start at <paramref name="floor"/> rounded up to a whole peak block (2.41), for everyone: a segment
    /// on disk whose start was discarded keeps its old peaks until the next launch rebuilds them from the silence.
    /// </summary>
    private HistoryRange[] PeakRanges(HistoryAccess access, long floor)
    {
        long safe = floor == 0 ? 0 : floor / segmentFrames * segmentFrames + Math.Min(segmentFrames, (floor % segmentFrames + PeakBlockFrames - 1) / PeakBlockFrames * PeakBlockFrames);
        if (access.IsHost) return safe == 0 ? access.Allowed : [.. access.Allowed.Where(r => r.EndFrame > safe).Select(r => new HistoryRange(Math.Max(r.StartFrame, safe), r.EndFrame))];
        var result = new List<HistoryRange>();
        foreach (var r0 in access.Allowed)
        {
            if (r0.EndFrame <= safe) continue;
            var r = r0 with { StartFrame = Math.Max(r0.StartFrame, safe) };
            long segment = r.StartFrame / segmentFrames * segmentFrames, local = r.StartFrame - segment;
            long start = segment + Math.Min(segmentFrames, (local + PeakBlockFrames - 1) / PeakBlockFrames * PeakBlockFrames), end = r.EndFrame;
            if (end != long.MaxValue) { segment = end / segmentFrames * segmentFrames; end = segment + (end - segment) / PeakBlockFrames * PeakBlockFrames; }
            if (start < end) result.Add(new(start, end));
        }
        return [.. result];
    }

    private void LevelsFromSamples(HistoryAccess access, long startFrame, int frames, short[] result)
    {
        HistoryRange[] visible;
        lock (gate) { ThrowIfClosedLocked(); visible = Clip(access, AvailableLocked()); }
        var samples = new short[frames]; var known = new bool[frames];
        foreach (var range in visible)
        {
            long a = Math.Max(range.StartFrame, startFrame), z = Math.Min(range.EndFrame, startFrame + frames);
            if (a >= z) continue;
            // Expiry between the listing and the read just leaves these bins empty, as if asked a moment later.
            try { ReadCore(access, a, samples.AsSpan((int)(a - startFrame), (int)(z - a))); }
            catch (HistoryUnavailableException) { continue; }
            known.AsSpan((int)(a - startFrame), (int)(z - a)).Fill(true);
        }
        int bins = result.Length / 2;
        for (int b = 0; b < bins; b++)
        {
            int from = (int)((long)frames * b / bins), to = Math.Min(frames, Math.Max(from + 1, (int)((long)frames * (b + 1) / bins)));
            short min = short.MaxValue, max = short.MinValue;
            for (int i = from; i < to; i++) if (known[i]) { if (samples[i] < min) min = samples[i]; if (samples[i] > max) max = samples[i]; }
            if (min <= max) { result[b * 2] = Math.Max(min, (short)-short.MaxValue); result[b * 2 + 1] = max; }
        }
    }

    private static void Quantise(ReadOnlySpan<float> from, Span<short> to)
    {
        for (int i = 0; i < from.Length; i++)
        {
            float s = from[i];
            // NaN fails both comparisons and would otherwise convert to an arbitrary value.
            to[i] = s >= 1f ? short.MaxValue : s <= -1f ? (short)-short.MaxValue : float.IsNaN(s) ? (short)0 : (short)MathF.Round(s * short.MaxValue);
        }
    }

    // ---- Internals: pins ---------------------------------------------------------------------------------

    private void SetPin(HistoryAccess access, string ownerKey, long start, long end, bool mustExist)
    {
        if (string.IsNullOrEmpty(ownerKey) || ownerKey.Length > MaxOwnerKeyLength) throw new ArgumentException("A pin needs an owner key of 1 to 128 characters.", nameof(ownerKey));
        if (start < 0 || end <= start) throw new InvalidDataException("Choose a longer selection.");
        lock (gate)
        {
            ThrowIfClosedLocked();
            bool had = pins.TryGetValue(ownerKey, out var old);
            if (mustExist && !had) throw new InvalidDataException("That draft no longer protects any recording history.");
            if (!had && pins.Count >= MaxPins) throw new InvalidDataException("Too many drafts are protecting recording history.");
            if (!access.Allows(start, end)) throw Unavailable(access, HistoryUnavailableException.NotShared);
            if (end > live) throw Unavailable(access, HistoryUnavailableException.NotRecorded);
            if (start < floor) throw Unavailable(access, HistoryUnavailableException.Expired);
            long first = start / segmentFrames, last = (end - 1) / segmentFrames;
            for (long i = first; i <= last; i++) if (!segments.ContainsKey(i)) throw Unavailable(access, HistoryUnavailableException.Expired);
            // The budget counts every pinned segment, also those still inside the window: they will all be
            // outside it eventually, and a pin that succeeded must never lose its audio later.
            long oldFirst = had ? old.StartFrame / segmentFrames : 1, oldLast = had ? (old.EndFrame - 1) / segmentFrames : 0, count = 0;
            foreach (var s in segments.Values)
            {
                int others = s.PinCount - (s.Index >= oldFirst && s.Index <= oldLast ? 1 : 0);
                if (others > 0 || (s.Index >= first && s.Index <= last)) count++;
            }
            if (count * segmentBytes > pinBudgetBytes) throw new HistoryPinBudgetException(count * segmentBytes, pinBudgetBytes);
            for (long i = first; i <= last; i++) segments[i].PinCount++;
            pins[ownerKey] = new(start, end);
            if (had) ReleaseLocked(old);
            // A draft's audio should survive a crash soon after it was made, not only at the next segment boundary.
            flushRequested++;
        }
        wake.Set();
    }

    private void ReleaseLocked(HistoryRange range)
    {
        for (long i = range.StartFrame / segmentFrames; i <= (range.EndFrame - 1) / segmentFrames; i++)
            if (segments.TryGetValue(i, out var s) && --s.PinCount == 0 && s.Start < windowStart) DropLocked(s);
    }

    // ---- Internals: background writer --------------------------------------------------------------------

    private void WriterLoop()
    {
        bool retry = false;
        while (true)
        {
            wake.WaitOne(retry ? 500 : Timeout.Infinite);
            while (true)
            {
                if (Volatile.Read(ref spare) is null) Interlocked.CompareExchange(ref spare, GC.AllocateUninitializedArray<short>(segmentFrames), null);
                Work? work;
                lock (gate)
                {
                    if (abandoned) return;
                    work = CollectLocked(closing);
                }
                if (work is null) break;
                string? error = null;
                try { Execute(work); }
                // Anything escaping here would end the thread (and the process); keep the audio in memory and try again.
                catch (Exception ex) { error = ex.Message; }
                lock (gate)
                {
                    ApplyLocked(work, error);
                    Monitor.PulseAll(gate);
                    if (abandoned) return;
                }
                if (work.Final) return;
                retry = error != null;
                if (retry) break;
            }
        }
    }

    private Work? CollectLocked(bool final)
    {
        bool force = final || flushRequested > flushCompleted;
        var work = new Work { FlushTarget = flushRequested, Final = final };
        // Whole segments are written as they complete. The partial newest one is only written when asked
        // (Stop, Pin, Flush, Close), then topped up in place as it fills.
        foreach (var s in segments.Values)
            if (s.Samples is { } data && s.Filled > s.Flushed && (force || s.Filled == segmentFrames)) work.Writes.Add((s, s.Flushed, s.Filled, data));
        if (work.Writes.Count == 0 && deletes.Count == 0 && zeroes.Count == 0 && !indexDirty && !force) return null;
        work.Deletes = [.. deletes]; deletes.Clear();
        // A segment dropped meanwhile has no file left to clear (and writing would make one again).
        work.Zeroes = [.. zeroes.Where(z => segments.ContainsKey(z.Index))]; zeroes.Clear();
        long durable = live;
        if (!force && segments.TryGetValue(live / segmentFrames, out var tail) && tail.Filled < segmentFrames) durable = tail.Start + tail.Flushed;
        work.Index = Serialize(SnapshotIndexLocked(durable, final));
        indexDirty = false;
        return work;
    }

    /// <summary>Data first, then the index that describes it, then files the index no longer names.</summary>
    private void Execute(Work work)
    {
        foreach (var (segment, from, to, data) in work.Writes)
            files.Write(SegmentPath(segment.Index), from * 2L, MemoryMarshal.AsBytes(data.AsSpan(from, to - from)));
        foreach (var (index, frames) in work.Zeroes) files.Write(SegmentPath(index), 0, new byte[frames * 2L]);
        files.ReplaceAtomically(IndexPath, work.Index);
        work.Written = true;
        foreach (var index in work.Deletes) files.Delete(SegmentPath(index));
    }

    private void ApplyLocked(Work work, string? error)
    {
        if (work.Written)
        {
            foreach (var (segment, from, to, _) in work.Writes)
            {
                if (segment.Dropped) continue;
                unflushed -= to - from; segment.Flushed = to;
                if (to == segmentFrames) segment.Samples = null;
            }
            flushCompleted = Math.Max(flushCompleted, work.FlushTarget);
            if (work.Final) finalWritten = true;
        }
        else indexDirty = true;
        if (error != null) { deletes.AddRange(work.Deletes); zeroes.AddRange(work.Zeroes); }
        writeError = error;
    }
}
