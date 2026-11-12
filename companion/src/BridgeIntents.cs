using System.Collections.Concurrent;
namespace SoundpadWeb;

/// <summary>
/// One arbitration path for everything that changes what plays in the game (2.35).
/// A "playback intent" is a Play, cue start, region, Temp or kept play, reverse, an "... and play" copy or clip,
/// Stop or Panic. Intents are numbered in the order they ARRIVE at the companion (never by client clocks), and
/// only the newest one may start audio. An older one is answered quietly as superseded: it is never played
/// later, counts no statistics and is not an error. Seek and Pause are not intents; they belong to one playback
/// (its epoch) and are answered quietly as stale once that playback is gone.
/// </summary>
public sealed partial class Bridge
{
    private sealed class Intent(long revision, long at, bool halts)
    {
        public readonly long Revision = revision, At = at;
        /// <summary>A Stop or Panic (or a stop the companion makes itself): an older start must do nothing more.</summary>
        public readonly bool Halts = halts;
        /// <summary>
        /// Set when a newer intent arrives. The pipe looks at it immediately before it writes a play, so a play
        /// that was replaced while it waited for the pipe never reaches Soundpad. Nothing is registered on the
        /// token, so setting it runs no code on the thread that delivers the newer command.
        /// </summary>
        public readonly CancellationTokenSource Withdrawn = new();
        /// <summary>For a Stop or Panic: its request to Soundpad has been answered (or failed), so nothing is "about to stop" any more.</summary>
        public volatile bool Settled;
        /// <summary>The gate this intent is queued on, so a newer intent can take it out of the queue at once.</summary>
        public volatile PendingWait? Waiting;
        /// <summary>The answer, for a command id that is sent again.</summary>
        public readonly TaskCompletionSource<Reply> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record PendingWait(PriorityGate Gate, PriorityGate.Waiter Waiter);
    private readonly record struct Reservation(Intent? Intent, bool Repeated, Reply? Quiet);
    private enum Lookup { Found, Missing, Withdrawn }

    private readonly ConcurrentDictionary<string, Intent> playbackIntents = new();
    private readonly object intentSync = new();
    private volatile Intent newest = new(0, 0, false);
    private long playRevision, intentsSwept;
    private volatile bool playbackSuspended;
    // The playback record is also written by Stop and Panic, which do not hold the gate. Every write goes through
    // this lock; playbackOwner is the revision of the intent whose start or stop the record describes (a higher
    // one always wins, as its request also reached Soundpad later), and playbackVersion changes whenever the
    // record is replaced, so a sample that was on its way meanwhile is dropped.
    private readonly object playbackSync = new();
    private long playbackOwner, playbackVersion;
    private int stateDirty, disposed;

    private static bool StartOperation(string op) => op is "play" or "startAt" or "region" or "audioPlay" or "reverse";
    private static bool AutoplayOperation(Command c) => c.Autoplay && c.Op is "trimAudio" or "mimicClip" or "mimicLibraryImport";
    /// <summary>The quiet answer to playback input that no longer applies: never an error, nothing was played.</summary>
    private static Reply Superseded(Command c, string reason) => new("ack", c.Id, true, Data: new { superseded = true, reason, played = false });
    private bool IsNewest(Intent intent) => ReferenceEquals(newest, intent);

    /// <summary>
    /// The first thing <see cref="Execute"/> does, synchronously. For a playback intent from someone allowed to
    /// make it, this takes the next revision and at once releases the previous newest intent if it is still
    /// waiting to start. Expired playback input, and guest playback while the host has suspended it, get their
    /// quiet answer here and reserve nothing. Anything else (other commands, invalid ones, guests without
    /// permission) reserves nothing and is handled, or refused, further on as before.
    /// </summary>
    private Reservation ReserveIntent(Command c, string actor, string? requester, string? audience)
    {
        bool autoplay = AutoplayOperation(c), control = c.Op is "seek" or "pause", panic = c.Op == "panic";
        bool halts = c.Op == "stop" || panic && c.Action == "enable";
        if (!StartOperation(c.Op) && !autoplay && !control && !halts && !panic && c.Op != "endSession") return default;
        if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _)) return default;
        bool host = actor == "host";
        if ((panic || c.Op == "endSession") && !host) return default;
        // A guest who may not play (or seek) reserves nothing: the refusal must not cancel anyone's pending start.
        if (!host && (!GuestStillAllowed(actor, requester, audience) || (c.Op is "seek" or "pause" or "startAt" or "region") && !disk.Permissions.GuestSeeking)) return default;
        if (playbackIntents.TryGetValue(c.Id, out var known)) return new(known, true, null);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (c.Op != "endSession")
        {
            // Old clicks are dropped quietly: 5 s for anything that starts or moves audio, 30 s for Stop, Pause and Panic.
            long ttl = c.Op is "stop" or "pause" or "panic" ? 30000 : 5000;
            if (c.At < now - ttl || c.At > now + ttl) return autoplay ? default : new(null, false, Superseded(c, "expired"));
            if (!host && playbackSuspended) return autoplay ? default : new(null, false, Superseded(c, "suspended"));
        }
        if (control) return default;
        if (panic)
        {
            // Set here, in arrival order: a guest command that arrives after Panic is refused even while the stop
            // itself is still on its way to Soundpad. Resuming is not an intent and cancels nothing.
            if (c.Action == "enable") playbackSuspended = true;
            else { if (c.Action == "disable") playbackSuspended = false; return default; }
        }
        // Command ids are remembered for two minutes (a repeat gets the first answer); swept at most every 5 s.
        if (playbackIntents.Count > 64 && now - Interlocked.Read(ref intentsSwept) > 5000)
        {
            Interlocked.Exchange(ref intentsSwept, now);
            foreach (var entry in playbackIntents.Where(e => now - e.Value.At > 120000)) playbackIntents.TryRemove(entry.Key, out _);
        }
        Intent intent, previous;
        lock (intentSync)
        {
            intent = new(playRevision + 1, now, halts);
            if (!playbackIntents.TryAdd(c.Id, intent)) return new(playbackIntents[c.Id], true, null);
            playRevision++; previous = newest; newest = intent;
        }
        Release(previous);
        return new(intent, false, null);
    }
    /// <summary>An intent the companion makes itself: session clean-up, and the stops of Undo, Clear Temp and closing.</summary>
    private Intent ReserveInternal(bool halts)
    {
        Intent intent, previous;
        lock (intentSync) { intent = new(++playRevision, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), halts); previous = newest; newest = intent; }
        Release(previous);
        return intent;
    }
    /// <summary>A newer intent arrived: the previous one must not start, and stops waiting to.</summary>
    private static void Release(Intent previous)
    {
        previous.Withdrawn.Cancel();
        if (previous.Waiting is { } wait) wait.Gate.Cancel(wait.Waiter);
    }
    /// <summary>
    /// Waits for a gate in the urgent lane on behalf of an intent. False when a newer intent arrived while this one
    /// was still waiting: it left the queue at once and does not hold the gate. Pending starts therefore never
    /// form a backlog; at most the newest one waits.
    /// </summary>
    private async Task<bool> EnterFor(PriorityGate target, Intent intent)
    {
        var waiter = target.Enter(PriorityGate.Lane.Urgent);
        if (!waiter.Entered.IsCompleted)
        {
            intent.Waiting = new(target, waiter);
            if (!IsNewest(intent)) target.Cancel(waiter); // the newer intent arrived before it could see this wait
        }
        try { return await waiter.Entered; }
        finally { intent.Waiting = null; }
    }
    // Seek and Pause waiting for the gate (2.35). They are cheap one by one but each costs several pipe requests,
    // so a flood of them (a guest's modified page can send 100 a second) must not build a queue that plays and
    // stops wait behind. A newer seek replaces a seek still waiting (only the latest position matters); a pause
    // is a toggle, so while one waits a second one is answered as stale instead of queueing.
    private readonly object controlSync = new();
    private PriorityGate.Waiter? seekWaiting, pauseWaiting;
    /// <summary>
    /// Enters the gate for a Seek or Pause. False (answered quietly as stale) when it aims at a playback that is
    /// no longer current, or when a newer seek replaced it while it waited, or a pause is already waiting.
    /// </summary>
    private async Task<bool> EnterControl(Command c)
    {
        // Costs no pipe request: a control for another sound or an old epoch is stale already.
        var current = playback;
        if (current.SoundId == null || c.SoundId != current.SoundId || c.Epoch != current.Epoch) return false;
        var waiter = gate.Enter(PriorityGate.Lane.Urgent);
        if (waiter.Entered.IsCompleted) return await waiter.Entered;
        bool pause = c.Op == "pause";
        lock (controlSync)
        {
            if (pause)
            {
                // If it was admitted meanwhile it holds the gate and simply goes ahead.
                if (pauseWaiting != null) return !gate.Cancel(waiter);
                pauseWaiting = waiter;
            }
            else
            {
                if (seekWaiting != null) gate.Cancel(seekWaiting);
                seekWaiting = waiter;
            }
        }
        try { return await waiter.Entered; }
        finally
        {
            lock (controlSync)
            {
                if (ReferenceEquals(seekWaiting, waiter)) seekWaiting = null;
                if (ReferenceEquals(pauseWaiting, waiter)) pauseWaiting = null;
            }
        }
    }

    /// <summary>
    /// True when a start that is already under way should do no more Soundpad work: a Stop or Panic came after
    /// it, or a newer start is waiting for the gate this one holds. (A newer intent that is still being prepared,
    /// a reverse or a copy, is no reason: this sound keeps playing properly until that one is ready.)
    /// </summary>
    private bool ShouldYield(Intent intent)
    {
        if (Volatile.Read(ref playbackOwner) > intent.Revision) return true;
        var latest = newest;
        return !ReferenceEquals(latest, intent) && (latest.Halts && !latest.Settled || latest.Waiting is { } wait && ReferenceEquals(wait.Gate, gate));
    }
    /// <summary>True when the playback a Seek or Pause aims at is about to be replaced or stopped by a newer intent.</summary>
    private bool AboutToChange()
    {
        var latest = newest;
        return latest.Revision > Volatile.Read(ref playbackOwner) && (latest.Halts && !latest.Settled || latest.Waiting is { } wait && ReferenceEquals(wait.Gate, gate));
    }
    /// <summary>
    /// The caller holds the gate. Re-reads the small sound list (the full library only if it changed) and returns
    /// the sound's current index. Withdrawn when a newer intent arrived first; nothing was asked of Soundpad then.
    /// </summary>
    private async Task<(Lookup Result, int Index)> FreshIndex(string soundId, Intent intent)
    {
        if (!IsNewest(intent)) return (Lookup.Withdrawn, 0);
        if (!available) throw new InvalidDataException("Soundpad is unavailable. Open it normally; reconnection is automatic.");
        // Re-read the global mapping immediately before play; never trust a cached index after reordering.
        // Only the small sound list is needed (about 10 ms). Sound ids come from file paths, so if the list
        // is unchanged the cached index is exact. The full library (categories and icons, over 1 MB on big
        // libraries) is re-read only when the list changed. A change elsewhere in the library (a category,
        // an icon) no longer refuses the play: that made presses fail while the library kept changing (2.15.9).
        string soundlist;
        try { soundlist = await pipe.Request("GetSoundlist()", intent.Withdrawn.Token); }
        catch (OperationCanceledException) when (intent.Withdrawn.IsCancellationRequested) { return (Lookup.Withdrawn, 0); }
        if (soundlist != lastSoundlist) await RefreshInner();
        return available && parsed.Indexes.TryGetValue(soundId, out int index) ? (Lookup.Found, index) : (Lookup.Missing, 0);
    }

    /// <summary>
    /// Stops Soundpad without taking the gate, so a Stop waits only for the one pipe request already in flight,
    /// never for a library refresh or a managed-audio batch. The record is set to stopped unless an even newer
    /// intent has already written it. False when Soundpad refused.
    /// </summary>
    private async Task<bool> StopNow(Intent intent)
    {
        try
        {
            if (!await SendStop()) return false;
            NoteAudio(); SlotLease? free = null;
            lock (playbackSync)
            {
                if (playbackOwner < intent.Revision)
                {
                    playbackOwner = intent.Revision; playbackVersion++;
                    playback = new("STOPPED", SampledAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); regionStart = null; seekSettlesUntil = 0;
                    free = DropSliceLocked(); // the slot is free at once after Stop (measured 2.36)
                }
            }
            Free(free);
            return true;
        }
        finally { intent.Settled = true; }
    }
    // Stops share one request to Soundpad (2.35). Any number of Stops arriving together wait for the next
    // DoStopSound that has not been written yet, so at most one is in flight and one more batch waits behind it.
    // A batch is written only after all its stops arrived, so nothing asked for before any of them starts after.
    // Without this, a flood of guest Stops queued on the pipe and made the host's own Stop wait up to 4 s.
    private readonly object stopSync = new();
    private TaskCompletionSource<bool>? stopWaiting;
    private bool stopPump;
    private Task<bool> SendStop()
    {
        Task<bool> mine; bool begin;
        lock (stopSync)
        {
            mine = (stopWaiting ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            begin = !stopPump; stopPump = true;
        }
        if (begin) _ = PumpStops();
        return mine;
    }
    private async Task PumpStops()
    {
        while (true)
        {
            TaskCompletionSource<bool> batch;
            lock (stopSync) { if (stopWaiting == null) { stopPump = false; return; } batch = stopWaiting; stopWaiting = null; }
            try { batch.TrySetResult((await pipe.Request("DoStopSound()")).StartsWith("R-200", StringComparison.Ordinal)); }
            catch (Exception ex) { batch.TrySetException(ex); }
        }
    }
    private async Task<Reply> ExecuteStop(Command c, string actor, string? requester, string? audience, Intent? intent)
    {
        try
        {
            RequirePlainPermission(c, actor, requester, audience);
            // Its intent was reserved on arrival, so no start asked for before this Stop can begin after it.
            if (!await StopNow(intent ?? ReserveInternal(true))) throw new InvalidDataException("Soundpad rejected Stop.");
            Publish(); CommandAccepted?.Invoke();
            return new("ack", c.Id, true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            Publish();
            return new("ack", c.Id, false, ex is InvalidDataException ? ex.Message : "Companion operation failed. Check Soundpad and local storage.");
        }
    }
    /// <summary>
    /// Panic (2.35, host only). "enable" stops Soundpad at once, cancels every start still pending and suspends
    /// guest playback; "disable" lets guests play again. The session, Mimic capture and Temp are untouched, and
    /// the host can still play while guests are suspended. The flag itself is set on arrival in ReserveIntent.
    /// </summary>
    private async Task<Reply> ExecutePanic(Command c, string actor, Intent? intent)
    {
        if (actor != "host") return new("ack", c.Id, false, "Host permission required.");
        if (c.Action == "disable") { Publish(); CommandAccepted?.Invoke(); return new("ack", c.Id, true, Data: new { suspended = false }); }
        if (c.Action != "enable" || intent == null) return new("ack", c.Id, false, "Unsupported Panic action.");
        bool stopped;
        try { stopped = !available || await StopNow(intent); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { stopped = false; }
        finally { intent.Settled = true; }
        Publish(); CommandAccepted?.Invoke();
        return stopped ? new("ack", c.Id, true, Data: new { suspended = true }) : new("ack", c.Id, false, "Guest playback is suspended, but Soundpad did not confirm the stop.");
    }
    /// <summary>
    /// Stops playback when what plays is this app's temporary audio, as clean-up needs (Clear Temp, closing the
    /// window). It goes through the same arbitration as Stop (2.35): nothing asked for earlier starts after it.
    /// </summary>
    public async Task StopTemporaryPlayback()
    {
        var current = playback;
        // An exact slice plays from the app's slot file, which lives in the Temp category too (2.37).
        bool sliced; lock (playbackSync) sliced = slice != null;
        if (current.Status is "PLAYING" or "PAUSED" or "SEEKING" && (sliced || parsed.Public.Sounds.Any(s => s.Id == current.SoundId && s.Temporary))) await StopNow(ReserveInternal(true));
    }

    /// <summary>
    /// Playback never waits for the disk (2.35): receipts, statistics and activity of plays are kept in memory and
    /// written together a moment later, off the gate. One write is pending at most, however many plays there were.
    /// </summary>
    private void SaveLater()
    {
        if (Interlocked.Exchange(ref stateDirty, 1) == 0) _ = Task.Run(async () => { await Task.Delay(300); await FlushState(); });
    }
    /// <summary>
    /// Writes deferred playback state now. Also runs before a clean exit (CleanupSession, Dispose). The copy is
    /// taken under the gate; the write happens after it is released, and the store drops it if a later durable
    /// save already put newer state on disk.
    /// </summary>
    internal async Task FlushState()
    {
        if (Volatile.Read(ref stateDirty) == 0) return;
        StateStore.StateImage image;
        await gate.EnterAsync(PriorityGate.Lane.Background);
        try { if (Interlocked.Exchange(ref stateDirty, 0) == 0) return; image = store.Capture(disk); }
        finally { gate.Release(); }
        try { store.Commit(image, pruneIcons: false, createFolder: false); }
        // Still in memory: try again shortly, and the next durable save writes it too.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (Volatile.Read(ref disposed) == 0) SaveLater(); }
    }
}
