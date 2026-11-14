using System.Collections.Concurrent;
using System.Diagnostics;
namespace SoundpadWeb;

/// <summary>
/// Exact cue starts and regions (2.37). Soundpad can only start an entry from its beginning and then seek. On the
/// owner's Soundpad 4.0.35 that let the beginning through (37 ms once; in 2 of 3 rounds the seek was ignored and the
/// whole first second played), and a region's end was only found by polling. Instead, the wanted range is rendered
/// sample-exact into one of three slot files that are registered once in the app's Temp category, and that slot is
/// played: Soundpad reads a rewritten file again on every play and plays a file's first and last milliseconds
/// exactly (both measured). A slot is never rewritten while it plays (Soundpad locks it then), so the next slice
/// goes into another one.
///
/// Everything else stays as it was. Every start still goes through the newest-intent arbitration: a slice that a
/// newer intent, Stop or Panic overtook while it was rendered never plays. The playback the website sees is about
/// the ORIGINAL sound (its id, its duration, and position = position in the slot + where the slice starts), so
/// seek, pause, statistics and now-playing work unchanged. When a slice cannot be ready in about 150 ms, or the
/// slots are not registered (yet) or cannot be written, the start takes the 2.36 way (play, then seek), which is
/// logged once and never shown to the person as an error. A plain Play is always the sound itself (already exact).
/// </summary>
public sealed partial class Bridge
{
    /// <summary>Longest slice. A cue start whose remainder is longer (or a longer region) plays the old way.</summary>
    internal const long ExactMaxMs = 600000;
    /// <summary>How long a start waits for decoding and rendering before it plays the old way instead, at once.</summary>
    internal const int ExactBudgetMs = 150;
    private PcmCache pcm = new();
    /// <summary>For tests: a cache with a stand-in decoder (a slow or failing decode).</summary>
    internal void UsePcmCache(PcmCache cache) { var old = pcm; pcm = cache; old.Dispose(); }
    /// <summary>One slice is rendered at a time (review, 2.37): a long one holds hundreds of MB while it is made.</summary>
    private readonly SemaphoreSlim renderSlot = new(1, 1);
    private Func<ISliceSource, long, long, CancellationToken, byte[]> renderSlice = (source, start, end, cancel) => SliceRenderer.Render(source, start, end, null, cancel);
    /// <summary>For tests: a stand-in renderer (a slow one, or one that counts how many run at once).</summary>
    internal void UseRenderer(Func<ISliceSource, long, long, CancellationToken, byte[]> render) => renderSlice = render;
    private SlotFiles? slotFiles;
    private SlotFiles Slots
    {
        get
        {
            if (Volatile.Read(ref slotFiles) is { } files) return files;
            Interlocked.CompareExchange(ref slotFiles, new SlotFiles(store.DirectoryPath), null);
            return slotFiles!;
        }
    }
    private volatile bool exactEnabled = true;
    /// <summary>Slot path to its index, from the last library read; only slots listed once, inside the app's Temp category.</summary>
    private volatile IReadOnlyDictionary<string, int> slotIndexes = new Dictionary<string, int>();
    /// <summary>A cue start or region wanted a slice but the slots were not registered: register them at the next quiet moment.</summary>
    private volatile bool slotsWanted;
    private long slotsRetryAt, lastAudio = Environment.TickCount64, exactStarts, exactFallbacks;
    private readonly ConcurrentDictionary<string, byte> exactNotes = new();
    /// <summary>Cue starts and regions that played as exact slices, and those that took the old way (for tests and probes).</summary>
    internal long ExactStarts => Interlocked.Read(ref exactStarts);
    internal long ExactFallbacks => Interlocked.Read(ref exactFallbacks);
    /// <summary>All three slots are registered and listed in Soundpad.</summary>
    internal bool SlotsReady => slotIndexes.Count == SlotFiles.Count;
    internal IReadOnlyList<string> SlotPaths => Slots.Paths;

    /// <summary>
    /// Turns exact slices off or on (on by default). The e2e harness turns them off unless asked, because its
    /// browser tests check the 2.36 play-then-seek commands Soundpad receives.
    /// </summary>
    public void UseExactSlices(bool enabled) => exactEnabled = enabled;

    /// <summary>A slice rendered into a slot and not played yet. Its lease keeps the slot from being handed to anyone else.</summary>
    private sealed class ExactPlan(SlotLease lease, long generation, long offsetMs, long sliceMs, long sourceMs)
    {
        public readonly SlotLease Lease = lease;
        public readonly long Generation = generation, OffsetMs = offsetMs, SliceMs = sliceMs, SourceMs = sourceMs;
        /// <summary>Handed over to the playing slice; whoever made the plan no longer releases it.</summary>
        public bool Taken;
    }
    /// <summary>The slice that is playing. Read and replaced under playbackSync; it keeps its slot's lease until another sound replaces it or it stops.</summary>
    private sealed class ExactSlice(ExactPlan plan, bool region, Intent owner)
    {
        public readonly SlotLease Lease = plan.Lease;
        public readonly long Generation = plan.Generation, OffsetMs = plan.OffsetMs, SliceMs = plan.SliceMs, SourceMs = plan.SourceMs;
        public readonly bool Region = region;
        /// <summary>The intent that started it, so a repeat never outlives a newer one.</summary>
        public readonly Intent Owner = owner;
        /// <summary>When its slot was last told to play (TickCount64); moved on by the time it spent paused.</summary>
        public long StartedTick = Environment.TickCount64;
        /// <summary>Since it was last told to play, a reading saw the slot playing close to its own end (review, 2.37).</summary>
        public bool NearEnd;
        /// <summary>A reading saw the slot playing since it was last told to play.</summary>
        public bool SeenPlaying;
        /// <summary>When it was paused (TickCount64), while it is paused: the end clock stands still meanwhile.</summary>
        public long? PausedTick;
        /// <summary>
        /// How close to its end a reading must see the slot for a later stop to count as its natural end. A stop
        /// seen without such a reading was someone stopping it in Soundpad, and a repeat never plays it again.
        /// </summary>
        public long NearMs => NearWindow(SliceMs);
        public void Pause(long now) { PausedTick ??= now; }
        public void Resume(long now) { if (PausedTick is long paused) { StartedTick += now - paused; PausedTick = null; } }
    }
    /// <summary>See <see cref="ExactSlice.NearMs"/>: two thirds of the slice, at least 60 ms and at most 250 ms.</summary>
    private static long NearWindow(long sliceMs) => Math.Clamp(sliceMs * 2 / 3, 60, 250);
    private ExactSlice? slice;

    /// <summary>The companion just played, stopped or saw audio playing: the Temp category waits a second after this (2.37).</summary>
    private void NoteAudio() => Interlocked.Exchange(ref lastAudio, Environment.TickCount64);
    /// <summary>Logs a reason for taking the old way once per kind, so a busy session does not repeat it on every click.</summary>
    private void Note(string key, string message) { if (exactNotes.TryAdd(key, 0)) Log(message); }
    /// <summary>Called with playbackSync held: the record no longer describes a slice. Returns the lease to release.</summary>
    private SlotLease? DropSliceLocked() { var lease = slice?.Lease; slice = null; return lease; }
    private void Free(SlotLease? lease) { if (lease != null) Slots.Pool.Release(lease); }
    /// <summary>Releases a plan nobody played.</summary>
    private void Discard(ExactPlan? plan) { if (plan is { Taken: false }) Slots.Pool.Release(plan.Lease); }
    /// <summary>The slot index Soundpad has for a plan, while the slot still holds that plan's slice; null otherwise.</summary>
    private int? SlotIndexOf(SlotLease lease, long generation) =>
        slotIndexes.TryGetValue(lease.Path, out int index) && Slots.Pool.Info(lease.Index).Generation == generation ? index : null;

    /// <summary>
    /// Takes the app's slot entries out of the library the website sees (they are never one of the person's sounds:
    /// not in the list, not in any category, not playable by id, no waveform) and notes the index of each usable slot.
    /// Called from RefreshInner with the gate held.
    /// </summary>
    private ParsedLibrary HideSlots(ParsedLibrary next)
    {
        var own = new HashSet<string>(Slots.Owned(), StringComparer.OrdinalIgnoreCase);
        var hidden = next.Paths.Where(p => own.Contains(p.Value)).Select(p => p.Key).ToHashSet();
        if (hidden.Count == 0) { slotIndexes = new Dictionary<string, int>(); return next; }
        var view = managed?.Public;
        Category? Find(IEnumerable<Category> categories) { foreach (var c in categories) { if (c.Id == view?.TempCategoryId?.ToString() && c.Name == view?.TempCategoryName) return c; if (Find(c.Children) is { } found) return found; } return null; }
        var temp = view?.TempCategoryId == null ? null : Find(next.Public.Categories);
        var usable = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Slots.Paths)
        {
            var ids = next.Paths.Where(p => string.Equals(p.Value, path, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray();
            // Listed exactly once, and in the app's own Temp category: anything else is left alone and not played.
            if (ids.Length == 1 && temp != null && temp.SoundIds.Contains(ids[0])) usable[path] = next.Indexes[ids[0]];
        }
        slotIndexes = usable;
        Category Without(Category c) => c with { SoundIds = c.SoundIds.Where(id => !hidden.Contains(id)).ToArray(), Children = c.Children.Select(Without).ToArray() };
        return next with
        {
            Public = next.Public with { Sounds = next.Public.Sounds.Where(s => !hidden.Contains(s.Id)).ToArray(), Categories = next.Public.Categories.Select(Without).ToArray() },
            Indexes = next.Indexes.Where(p => !hidden.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value),
            Paths = next.Paths.Where(p => !hidden.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)
        };
    }

    /// <summary>A cue start or region of this command, prepared for its intent; see <see cref="PrepareExact"/>.</summary>
    private Task<(ExactPlan? Plan, bool Withdrawn)> PrepareFor(Command c, string? path, Intent intent) =>
        c.Op is "startAt" or "region" ? PrepareExact(c.Op, c.PositionMs, c.EndMs, path, () => IsNewest(intent), c.SoundId) : Task.FromResult<(ExactPlan?, bool)>((null, false));

    /// <summary>
    /// Renders [start, end) of the source into a free slot, before the gate is taken, so nothing waits for it.
    /// Plan null and not withdrawn: play the old way (slices off, slots missing, not decoded within the budget, too
    /// long, a slot locked beyond its retries). Withdrawn: <paramref name="wanted"/> turned false meanwhile (a newer
    /// intent, Stop or Panic): nothing may play. The decode is never cancelled, so the next click finds it cached.
    /// </summary>
    private async Task<(ExactPlan? Plan, bool Withdrawn)> PrepareExact(string op, long? positionMs, long? endMs, string? path, Func<bool> wanted, string? soundId = null)
    {
        if (!exactEnabled || path == null || positionMs is not >= 0 || op == "region" && (endMs is null || endMs <= positionMs)) return (null, false);
        // Too long to be a slice by what is already known (the region, or the rest of the sound by the length
        // Soundpad lists): nothing is decoded for it (review, 2.37).
        long? listedMs = soundId == null ? null : parsed.Public.Sounds.FirstOrDefault(s => s.Id == soundId)?.DurationMs;
        if ((op == "region" ? endMs!.Value : listedMs ?? 0) - positionMs.Value > ExactMaxMs) return (null, false);
        var clock = Stopwatch.StartNew();
        Task<ISliceSource> loading;
        try { if (!File.Exists(path)) return (null, false); loading = pcm.Get(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return (null, false); }
        if (!SlotsReady)
        {
            // Registered lazily, at the next quiet moment (never while something plays): this click plays the old
            // way at once, and the decode started above makes the next one exact.
            slotsWanted = true;
            return (null, false);
        }
        var decoded = await Within(loading, clock, wanted);
        if (decoded == null) return (null, true);
        if (decoded == false || !loading.IsCompletedSuccessfully)
        {
            if (!loading.IsCompleted) Note("decode", "Exact cue starts: a sound is still being decoded, so it started the usual way this once.");
            else Note("decode-failed", "Exact cue starts: a sound could not be decoded, so it starts the usual way.");
            return (null, false);
        }
        var source = loading.Result;
        int rate = source.SampleRate;
        long frames = source.FrameCount, start = SliceRenderer.FrameOf(positionMs.Value, rate);
        long end = op == "region" ? Math.Min(SliceRenderer.FrameOf(endMs!.Value, rate), frames) : frames;
        // An empty or reversed range keeps the old handling (it clamps, or refuses as before); so does a very long one.
        if (start >= end || SliceRenderer.MsOf(end - start, rate) > ExactMaxMs || source.Channels > 8) return (null, false);
        // One render at a time, and a render nobody waits for any more is stopped (review, 2.37): five quick clicks on
        // a long sound used to make five full renders at once, none of which was played.
        if (!renderSlot.Wait(0)) { Note("render-busy", "Exact cue starts: another slice was still being made, so a sound started the usual way this once."); return (null, false); }
        var stop = new CancellationTokenSource();
        var render = renderSlice;
        Task<byte[]> rendering;
        try { rendering = Task.Run(() => render(source, start, end, stop.Token)); }
        catch { renderSlot.Release(); stop.Dispose(); throw; }
        _ = rendering.ContinueWith(task => { _ = task.Exception; renderSlot.Release(); stop.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var rendered = await Within(rendering, clock, wanted);
        if (rendered != true) { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        if (rendered == null) return (null, true);
        if (rendered == false || !rendering.IsCompletedSuccessfully) { Note("render", "Exact cue starts: a slice was not ready in time, so it started the usual way this once."); return (null, false); }
        var wav = rendering.Result;
        long sourceMs = (long)Math.Round(SliceRenderer.MsOf(frames, rate)), sliceMs = (long)Math.Round(SliceRenderer.MsOf(end - start, rate));
        var tried = new List<int>();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (!wanted()) return (null, true);
            // Never the slot that is playing (alternating): its lease is held, and it is excluded once more here.
            lock (playbackSync) if (slice != null && !tried.Contains(slice.Lease.Index)) tried.Add(slice.Lease.Index);
            var lease = Slots.Pool.Acquire(tried);
            if (lease == null) { await Task.Delay(10); continue; } // another slice is being written this instant
            SlotWriteResult written;
            try { written = await Slots.Pool.Write(lease, wav); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { written = new(false, 0, Slots.Pool.Info(lease.Index), ex.Message); }
            if (written.Ok)
            {
                if (!wanted()) { Slots.Pool.Release(lease); return (null, true); }
                return (new ExactPlan(lease, written.Slot.Generation, positionMs.Value, sliceMs, sourceMs), false);
            }
            // Still locked by something (Soundpad, a virus scanner): try another slot, then the old way.
            Slots.Pool.Release(lease); tried.Add(lease.Index);
            Note("locked", "Exact cue starts: a slot file was in use, so a sound started the usual way.");
        }
        return (null, false);
    }
    /// <summary>
    /// Waits for work for the rest of the budget, looking every few ms whether the start is still wanted. True when it
    /// finished in time, false when the budget ran out, null when it is no longer wanted. Nothing is registered on an
    /// intent's token, so withdrawing it runs no code on the thread that delivers the newer command.
    /// </summary>
    private static async Task<bool?> Within(Task work, Stopwatch clock, Func<bool> wanted)
    {
        while (!work.IsCompleted)
        {
            if (!wanted()) return null;
            long left = ExactBudgetMs - clock.ElapsedMilliseconds;
            if (left <= 0) return false;
            await Task.WhenAny(work, Task.Delay((int)Math.Min(left, 10)));
        }
        return wanted() ? true : null;
    }

    /// <summary>
    /// After a poll: registers the slots when a cue start wanted them and Soundpad is idle. Never while anything plays
    /// (the person's sound included); the store looks again before each change. Failures (the journal blocked,
    /// Soundpad refusing, a garbled or missing category, a slot listed elsewhere) are logged once and retried later;
    /// meanwhile every start takes the old way.
    /// </summary>
    private async Task RegisterSlotsIfDue()
    {
        if (!exactEnabled || !slotsWanted || SlotsReady || !available || Environment.TickCount64 < Interlocked.Read(ref slotsRetryAt)) return;
        if (playback.Status != "STOPPED" || Environment.TickCount64 - Interlocked.Read(ref lastAudio) < 300) return;
        if (!audioGate.TryEnter()) return;
        try
        {
            Interlocked.Exchange(ref slotsRetryAt, Environment.TickCount64 + 5000);
            var audio = Audio;
            if (audio.Public.Blocked) { Note("blocked", "Exact cue starts are off while the managed-audio journal is blocked; sounds start the usual way."); Interlocked.Exchange(ref slotsRetryAt, Environment.TickCount64 + 60000); return; }
            await Slots.Pool.EnsureFiles();
            bool done = await audio.RegisterSlots(Slots.Paths);
            if (done) Slots.Record(audio.Public.TempCategoryName);
            await gate.EnterAsync(PriorityGate.Lane.Background); try { await RefreshInner(); Publish(); } finally { gate.Release(); }
            if (SlotsReady) { slotsWanted = false; Log("Exact cue starts and regions are ready (three slot files in the Temp category)."); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Note("register", "Exact cue starts are not available yet (" + (ex is InvalidDataException ? ex.Message : "Soundpad or the slot files could not be prepared") + "); sounds start the usual way and it is retried.");
            Interlocked.Exchange(ref slotsRetryAt, Environment.TickCount64 + 30000);
        }
        finally { audioGate.Release(); }
    }

    /// <summary>
    /// For the real-Soundpad acceptance probe: registers the slots now (with the same rules: only when Soundpad is
    /// idle and quiet), decodes a source ahead, and finds a sound's id by its file. True when the slots are ready.
    /// </summary>
    internal async Task<bool> RegisterSlotsNow()
    {
        slotsWanted = true; Interlocked.Exchange(ref slotsRetryAt, 0);
        for (int i = 0; i < 40 && !SlotsReady; i++)
        {
            await Poll(i == 0);
            if (!SlotsReady) await Task.Delay(150);
        }
        return SlotsReady;
    }
    internal Task WarmExact(string path) => pcm.Get(path);
    internal string? SoundIdOf(string path) => parsed.Paths.FirstOrDefault(p => string.Equals(p.Value, path, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>
    /// Wakes the poll loop shortly before a short slice ends (so a reading sees it near its end, see
    /// <see cref="ExactSlice.NearMs"/>) and just after, so a repeat follows at once rather than at the next poll.
    /// <paramref name="playedMs"/> is how much of it has already played (a resume).
    /// </summary>
    private void WakeAfter(long sliceMs, long playedMs = 0)
    {
        if (sliceMs > 60000) return;
        long near = sliceMs - NearWindow(sliceMs) / 3 - playedMs, end = sliceMs + 20 - playedMs;
        if (near > 0) _ = Task.Delay((int)near).ContinueWith(_ => CommandAccepted?.Invoke(), TaskScheduler.Default);
        if (end > 0) _ = Task.Delay((int)end).ContinueWith(_ => CommandAccepted?.Invoke(), TaskScheduler.Default);
    }

    /// <summary>
    /// A repeating region whose slot played to its end: the slot still holds the slice (its lease was kept), so it is
    /// played again, with no seek. The caller holds the gate and sampled <paramref name="seen"/>. Nothing is played
    /// once a Stop or a newer start is under way: the request carries the newest intent's token, which a newer intent
    /// withdraws before its own request can be written.
    /// </summary>
    private async Task ReplaySlice(ExactSlice cut, long seen)
    {
        var latest = newest;
        if (AboutToChange() || !ReferenceEquals(latest, cut.Owner) && latest.Halts) return;
        var list = await Pipe.Request("GetSoundlist()");
        if (list != lastSoundlist) await RefreshInner();
        if (SlotIndexOf(cut.Lease, cut.Generation) is not int index)
        {
            SlotLease? free = null;
            lock (playbackSync) { if (seen == playbackVersion && ReferenceEquals(slice, cut)) { playbackVersion++; playback = new("STOPPED", SampledAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); free = DropSliceLocked(); } }
            Free(free); return;
        }
        string answer;
        try { answer = await Pipe.Request($"DoPlaySound({index})", latest.Withdrawn.Token); }
        catch (OperationCanceledException) when (latest.Withdrawn.IsCancellationRequested) { return; }
        if (!answer.StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("Soundpad rejected this action or its API does not support it.");
        NoteAudio();
        lock (playbackSync)
        {
            if (seen == playbackVersion && ReferenceEquals(slice, cut))
            {
                cut.StartedTick = Environment.TickCount64; cut.NearEnd = false; cut.SeenPlaying = false; cut.PausedTick = null; seekSettlesUntil = Environment.TickCount64 + 1500; sampledTick = Environment.TickCount64;
                playback = playback with { Status = "PLAYING", PositionMs = cut.OffsetMs, PositionRevision = playback.PositionRevision + 1, SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            }
        }
        WakeAfter(cut.SliceMs);
    }

    /// <summary>
    /// A seek while a slice plays (the caller holds the gate): the slot holds only the old range, so the rest of the
    /// sound is reached with a new slice from the seek position (like any seek, it ends the region and its repeat).
    /// Paused stays paused. If no slice is ready in time, the sound itself is played and sought, the 2.36 way.
    /// False when the playback changed meanwhile (the seek is stale).
    /// </summary>
    private async Task<bool> SeekSlice(Command c, ExactSlice cut, long target, bool paused)
    {
        var epoch = c.Epoch;
        bool Current() { lock (playbackSync) return ReferenceEquals(slice, cut) && playback.Epoch == epoch; }
        var path = c.SoundId != null ? parsed.Paths.GetValueOrDefault(c.SoundId) : null;
        var prepared = await PrepareExact("startAt", target, null, path, () => !AboutToChange() && Current(), c.SoundId);
        if (prepared.Withdrawn) return false;
        var plan = prepared.Plan;
        try
        {
            var latest = newest;
            if (AboutToChange() || !Current()) return false;
            // Soundpad's indexes are list positions: a sound added, removed or moved since the last library
            // read shifts them. Never trust a cached index here either (review, 2.37), whether it is the slot's
            // or the sound's own.
            var list = await Pipe.Request("GetSoundlist()");
            if (list != lastSoundlist) await RefreshInner();
            if (AboutToChange() || !Current()) return false;
            int? slotIndex = plan == null ? null : SlotIndexOf(plan.Lease, plan.Generation);
            int index;
            if (slotIndex is int found) index = found;
            else if (c.SoundId == null || !parsed.Indexes.TryGetValue(c.SoundId, out index)) return false;
            string answer;
            try { answer = await Pipe.Request($"DoPlaySound({index})", latest.Withdrawn.Token); }
            catch (OperationCanceledException) when (latest.Withdrawn.IsCancellationRequested) { return false; }
            if (!answer.StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("Soundpad rejected this action or its API does not support it.");
            NoteAudio();
            if (slotIndex == null) await Success($"DoSeekMs({target})");
            // Soundpad starts what it is told to play; a paused sound is paused again at its new position.
            if (paused) await Success("DoTogglePause()");
            SlotLease? free = null;
            lock (playbackSync)
            {
                if (playback.Epoch == epoch)
                {
                    free = DropSliceLocked();
                    if (slotIndex != null) { slice = new ExactSlice(plan!, false, cut.Owner); plan!.Taken = true; if (paused) slice.Pause(Environment.TickCount64); }
                    seekSettlesUntil = Environment.TickCount64 + 1500; sampledTick = Environment.TickCount64; regionStart = null;
                    playback = playback with { Status = paused ? "PAUSED" : "PLAYING", PositionMs = target, PositionRevision = playback.PositionRevision + 1, RegionEndMs = null, Repeat = false, SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                }
            }
            Free(free);
            if (slotIndex != null) { Interlocked.Increment(ref exactStarts); if (!paused) WakeAfter(plan!.SliceMs); }
            return true;
        }
        finally { Discard(plan); }
    }
}
