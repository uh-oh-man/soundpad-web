using System.Text.Json;
namespace SoundpadWeb;

/// <summary>What the Bridge hands Program.cs when the host confirmed an update (2.44).</summary>
/// <param name="DownloadPath">The verified new EXE; null for a forced (test) state.</param>
public sealed record UpdateExit(string Version, string? DownloadPath, BridgeHandoff Bridge);

// The updater and session continuity across an update (2.44, gate updater). See docs/UPDATES.md. The ops are host
// only. Installing goes: check → download and verify (Updater) → backup checkpoint → EnterUpdateExit (capture stops
// with an "update" gap, the history closes without being deleted, state is flushed, Temp and the journal session stay)
// → UpdateExitRequested (Program.cs writes the hand-over, swaps the EXE and starts the new version) → the new process
// calls ResumeFromHandoff instead of RecoverHistory and StartupCleanup.
public sealed partial class Bridge
{
    private Updater? updater;
    private volatile bool updateExit;
    private readonly List<Func<string?>> updateBlockers = [];
    private int installing;
    /// <summary>True once the companion is leaving for an update: Dispose keeps the recording history then.</summary>
    public bool UpdateExiting => updateExit;
    /// <summary>Program.cs (and the harness with --handoff-out) carries out the exit. Without one, installing stops at "installing".</summary>
    public event Func<UpdateExit, Task>? UpdateExitRequested;
    private static string RunningVersion => typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public Updater Updates
    {
        get
        {
            if (updater != null) return updater;
            lock (updateBlockers) return updater ??= Wire(new Updater(new UpdateOptions { Current = RunningVersion, DataDir = store.DirectoryPath }));
        }
    }
    private Updater Wire(Updater u) { u.Changed += Publish; u.Log += Log; return u; }

    /// <summary>Sets up the updater for this launch (Program.cs from the build and --update-source; tests with fakes).</summary>
    public void ConfigureUpdates(UpdateOptions options)
    {
        lock (updateBlockers) { updater?.Dispose(); updater = Wire(new Updater(options)); }
        Publish();
    }

    /// <summary>
    /// Another Stage H step that must not overlap an update (a whole-library restore, an Organize apply) registers why
    /// it is busy; installing is refused while any answers non-null.
    /// </summary>
    public void BlockUpdatesWhile(Func<string?> reason) { lock (updateBlockers) updateBlockers.Add(reason); }
    /// <summary>Why an update may not start now, or null. Other packages also ask this before their own risky steps.</summary>
    public string? UpdateBusy => Volatile.Read(ref installing) != 0 || updateExit ? "The companion is installing an update." : null;

    private UpdateStatus UpdateSnapshot => Updates.Status;

    private void UpdatesDispose() => updater?.Dispose();

    private static bool IsUpdateOp(string op) => op is "updateStatus" or "updateCheck" or "updateDownload" or "updateInstall" or "updateLater" or "updateSettings";

    private Task<Reply>? ExecuteUpdates(Command c, string actor, Participant? p, string? requester, string? audience)
    {
        if (!IsUpdateOp(c.Op)) return updateExit && actor != "host" ? Task.FromResult(new Reply("ack", c.Id, false, "The host is updating Soundpad Web; try again in a moment.")) : null;
        return Task.FromResult(UpdateOp(c, actor, requester));
    }

    private Reply UpdateOp(Command c, string actor, string? requester)
    {
        try
        {
            // Host only, and never relayed for a guest: an update interrupts everyone's session.
            if (actor != "host" || requester != null) throw new InvalidDataException("Only the host can update Soundpad Web.");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            var body = Body(c);
            var u = Updates;
            switch (c.Op)
            {
                case "updateStatus":
                    Only(body);
                    return new("ack", c.Id, true, Data: new { update = u.Status, checkAtLaunch = u.CheckAtLaunch });
                case "updateCheck":
                    Only(body);
                    if (u.Status.State == "unsupported" && u.Status.Reason is UpdateReasons.NoRepository or UpdateReasons.NoExe) throw new InvalidDataException("This build doesn't update itself.");
                    _ = Task.Run(u.Check);
                    return new("ack", c.Id, true, Data: new { started = true });
                case "updateDownload":
                    {
                        var version = Version(body, false);
                        _ = Task.Run(() => u.Download(version));
                        return new("ack", c.Id, true, Data: new { started = true });
                    }
                case "updateInstall":
                    {
                        var version = Version(body, true);
                        if (UpdateBusy is { } running) throw new InvalidDataException(running);
                        if (Blocked() is { } blocked) throw new InvalidDataException(blocked);
                        if (u.Status.Latest != version || u.Status.State is not ("available" or "ready" or "failed")) throw new InvalidDataException("That update is not available any more. Check again.");
                        if (Interlocked.Exchange(ref installing, 1) != 0) throw new InvalidDataException("The update is already being installed.");
                        _ = Task.Run(() => Install(version));
                        return new("ack", c.Id, true, Data: new { started = true });
                    }
                case "updateLater":
                    Only(body);
                    u.Later();
                    return new("ack", c.Id, true);
                case "updateSettings":
                    {
                        Only(body, "checkAtLaunch");
                        if (body is not { } b || !b.TryGetProperty("checkAtLaunch", out var check) || check.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Say whether to check for updates at launch.");
                        u.SetCheckAtLaunch(check.GetBoolean());
                        Log(check.GetBoolean() ? "Updates are checked when the companion starts" : "Updates are no longer checked when the companion starts");
                        return new("ack", c.Id, true, Data: new { checkAtLaunch = check.GetBoolean() });
                    }
            }
            throw new InvalidDataException("Unsupported command.");
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
    }

    private static JsonElement? Body(Command c) => c.Body is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } b ? b : null;
    private static void Only(JsonElement? body, params string[] allowed)
    {
        if (body is not { } b) return;
        if (b.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid update request.");
        foreach (var property in b.EnumerateObject()) if (!allowed.Contains(property.Name)) throw new InvalidDataException("Invalid update request.");
    }
    private static string Version(JsonElement? body, bool install)
    {
        if (install) Only(body, "version", "confirm"); else Only(body, "version");
        if (body is not { } b || !b.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String || UpdateVersion.ParsePlain(v.GetString()) == null) throw new InvalidDataException("Choose the version to install.");
        if (install && (!b.TryGetProperty("confirm", out var ok) || ok.ValueKind != JsonValueKind.True)) throw new InvalidDataException("Confirm the update first.");
        return v.GetString()!;
    }
    private string? Blocked() { lock (updateBlockers) return updateBlockers.Select(b => { try { return b(); } catch (Exception ex) when (ex is not OutOfMemoryException) { return null; } }).FirstOrDefault(r => r != null); }

    private async Task Install(string version)
    {
        bool handedOver = false;
        try
        {
            var u = Updates;
            if (!await u.Download(version)) return; // the status says why
            if (Blocked() is { } blocked) { u.Refused(UpdateReasons.Busy, blocked); return; }
            // A backup first when the host has a backup folder (2.44, WP1). Skipped or failed: said, and the update goes on.
            string note;
            try
            {
                var checkpoint = await CheckpointH("pre-update", CancellationToken.None);
                note = checkpoint is { Skipped: false } ? "A backup was made first." : "No backup was made: " + (checkpoint?.Reason ?? "Backups are not set up.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { note = "No backup was made: " + ex.Message; }
            Log(note);
            u.Installing(note);
            var exit = UpdateExitRequested;
            if (exit == null) { Log("Nothing here can install updates (a test build)."); return; }
            await Task.Delay(200); // the "installing" state reaches the pages first
            var part = await EnterUpdateExit();
            handedOver = true;
            Log($"Installing version {version}: the companion restarts and continues this session.");
            await exit(new(version, u.Forced ? null : u.DownloadPath, part));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log("The update could not start: " + ex.Message); if (!handedOver) Updates.Refused(UpdateReasons.SwapFailed, "The update could not start: " + ex.Message); }
        finally { if (!handedOver) Interlocked.Exchange(ref installing, 0); }
    }

    /// <summary>
    /// The update path out (2.44): capture stops with an "update" gap, the recording history is flushed and closed
    /// without being deleted, the F6 hotkey is released, deferred state is written. Drafts, Temp, the journal's open
    /// session and every guest's join time stay: they are returned to hand to the next process. Nothing here ends the
    /// session (no ForgetDrafts, CloseHistory, CleanupSession or RetireSession).
    /// </summary>
    public async Task<BridgeHandoff> EnterUpdateExit()
    {
        updateExit = true;
        BridgeHandoff part;
        lock (historyGate)
        {
            var h = history;
            bool running = mimicCapture.Running, transcribing = transcriber.Enabled;
            var source = Settings;
            try { mimicCapture.Pump(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (running) { try { h?.EndRun(HistoryGapReason.Update); } catch (Exception ex) when (ex is HistoryException or InvalidOperationException) { } }
            try { mimicCapture.Stop(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            part = ExportHandoff(h, new(running, running ? source.SourceId : null, running ? source.SourceName : null, transcribing));
            try { transcriber.Disable(); transcriber.RetrySaves(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            UseStoreLocked(null);
            if (h != null) { try { h.Flush(10000); h.Close(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log("The recording history could not be closed cleanly: " + ex.Message); } }
        }
        try { hotkey.Unregister(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        // Nothing new arrives in Temp while the next process starts.
        sessionAccepting = false;
        await FlushState();
        Publish();
        return part;
    }

    private BridgeHandoff ExportHandoff(HistoryStore? h, CaptureHandoff capture)
    {
        DraftHandoff[] kept; string[] here; Dictionary<string, long> away;
        lock (drafts)
        {
            kept = [.. drafts.Values.Select(d => new DraftHandoff(d.Id, d.Owner, d.OwnerName, d.SessionId, d.Start, d.End, d.Name, d.NameTyped, d.NameRange.StartFrame, d.NameRange.EndFrame, d.Created, d.Updated, d.ClipAssetId, d.ClipRange.StartFrame, d.ClipRange.EndFrame))];
            here = [.. guestsHere]; away = new(guestsAway);
        }
        GuestJoinHandoff[] joins; Dictionary<string, long> forgotten;
        lock (guestJoins) { joins = [.. guestJoins.Select(p => new GuestJoinHandoff(p.Key, p.Value.Wall, p.Value.Frame, p.Value.SessionId))]; forgotten = new(forgottenGuests); }
        HistoryRange[] shared; lock (sharedRanges) shared = [.. sharedRanges];
        return new()
        {
            ExitedUtcMs = utcNow(), LaunchedUtcMs = launchedUtcMs, HistorySessionId = h?.SessionId, Capture = capture,
            Hotkey = new(hotkey.Name, hotkeySeconds, hotkey.Active),
            Drafts = kept, GuestJoins = joins, ForgottenGuests = forgotten, SharedRanges = shared, GuestsHere = here, GuestsAway = away,
            OwnReplays = ownReplays.Export(), Budgets = budgets.Export(), Provenance = new(provenance), RecentClips = RecentSnapshot,
            CounterSession = counterSession, PlaybackSuspended = playbackSuspended, SessionAccepting = sessionAccepting, AudioAudience = audioAudience,
            RevokedPeers = [.. revokedAudioPeers.Keys],
        };
    }

    /// <summary>
    /// Continues the session of the companion that updated (2.44), in place of RecoverHistory and StartupCleanup: the
    /// same recording history (with an "update" gap), drafts and their pins, guests' join times and shares (not clamped
    /// to this launch), Temp with its open journal session (no Recover or Discard question), budgets, Panic and the
    /// session's audience. Capture starts again on the same output only when it was running, and only if that output is
    /// still listed; the F6 hotkey comes back through its normal registration (a key another app took is reported).
    /// </summary>
    public async Task ResumeFromHandoff(BridgeHandoff h)
    {
        recoveryPending = false; tempRecovery = 0;
        launchedUtcMs = h.LaunchedUtcMs > 0 && h.LaunchedUtcMs <= utcNow() ? h.LaunchedUtcMs : launchedUtcMs;
        HistoryStore? resumed = null;
        lock (historyGate)
        {
            if (history == null && h.HistorySessionId is { } id)
            {
                try
                {
                    resumed = HistoryStore.Open(HistoryRoot, id, HistoryGapReason.Update, historyOptions with { WindowSeconds = Settings.HistorySeconds });
                    try { resumed.SetWindow(Settings.HistorySeconds); } catch (ArgumentOutOfRangeException) { }
                    UseStoreLocked(resumed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HistoryException or InvalidOperationException or ArgumentException)
                {
                    Log("The recording history could not be continued after the update: " + ex.Message);
                }
            }
            // Any other history left in the folder is not this session's (as RecoverHistory removes it).
            try
            {
                foreach (var r in HistoryStore.Recover(HistoryRoot).Where(r => r.SessionId != resumed?.SessionId && !r.InUse))
                {
                    try { HistoryStore.Discard(HistoryRoot, r.SessionId); TranscriptWords.Delete(HistoryRoot, r.SessionId); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Drafts keep their pins (the index kept them); a pin that did not survive is made again, or the draft goes.
        int dropped = 0;
        var pins = resumed?.Pins(HistoryAccess.Host) ?? new Dictionary<string, HistoryRange>();
        lock (drafts)
        {
            foreach (var d in h.Drafts.Take(4096))
            {
                if (resumed == null || d.SessionId != resumed.SessionId || d.End <= d.Start) { dropped++; continue; }
                var draft = new Draft
                {
                    Id = d.Id, Owner = d.Owner, OwnerName = d.OwnerName, SessionId = d.SessionId, Start = d.Start, End = d.End, Name = d.Name, NameTyped = d.NameTyped,
                    NameRange = new(d.NameStart, d.NameEnd), Created = d.Created, Updated = d.Updated, ClipAssetId = d.ClipAssetId, ClipRange = new(d.ClipStart, d.ClipEnd),
                };
                if (!pins.ContainsKey(draft.PinKey))
                {
                    try { resumed.Pin(HistoryAccess.Host, draft.PinKey, draft.Start, draft.End); }
                    catch (Exception ex) when (ex is HistoryException or InvalidOperationException or ArgumentException) { dropped++; continue; }
                }
                drafts[draft.Id] = draft;
            }
            guestsHere = new(h.GuestsHere.Take(256), StringComparer.Ordinal);
            guestsAway.Clear(); foreach (var (k, v) in h.GuestsAway.Take(MaxGuestJoins)) guestsAway[k] = v;
        }
        if (dropped > 0) Log($"{dropped} draft(s) could not be kept through the update");
        // Drafts' pins that belong to no draft any more are let go.
        if (resumed != null) foreach (var key in pins.Keys.Where(k => k.StartsWith("draft:", StringComparison.Ordinal)))
                { bool owned; lock (drafts) owned = drafts.ContainsKey(key[6..]); if (!owned) { try { resumed.Unpin(key); } catch (Exception ex) when (ex is HistoryException or InvalidOperationException) { } } }
        lock (guestJoins)
        {
            guestJoins.Clear(); forgottenGuests.Clear();
            foreach (var j in h.GuestJoins.Take(MaxGuestJoins)) if (j.Key is { Length: > 0 and <= 128 }) guestJoins[j.Key] = new(j.Wall, j.Frame, j.SessionId);
            foreach (var (k, v) in h.ForgottenGuests.Take(MaxGuestJoins)) forgottenGuests[k] = v;
        }
        lock (sharedRanges) { sharedRanges.Clear(); if (resumed != null) sharedRanges.AddRange(h.SharedRanges.Where(r => r.EndFrame > r.StartFrame).Take(1024)); }
        ownReplays.Import(h.OwnReplays);
        budgets.Import(h.Budgets);
        foreach (var (k, v) in h.Provenance.Take(8192)) if (k.Length <= 64 && LibrarySource.Kinds.Contains(v.Kind)) provenance[k] = v;
        lock (recentClips) { recentClips.Clear(); recentClips.AddRange(h.RecentClips.Take(RecentClips)); }
        counterSession = Math.Max(0, h.CounterSession);
        playbackSuspended = h.PlaybackSuspended;
        sessionAccepting = h.SessionAccepting;
        audioAudience = h.AudioAudience is { Length: <= 64 } a ? a : null;
        foreach (var peer in h.RevokedPeers.Take(4096)) if (peer.Length <= 128) revokedAudioPeers[peer] = 1;
        if (HotkeyLengths.Contains(h.Hotkey.Seconds)) hotkeySeconds = h.Hotkey.Seconds;
        Interlocked.Increment(ref draftsRevision);
        // Temp and its open journal session continue: imports are checked again, nothing is retired or asked about.
        _ = Task.Run(ResumeAudio);
        await ResumeCapture(h, resumed);
        ResumeHotkey(h.Hotkey);
        StageHStarted();
        Publish();
    }

    private async Task ResumeAudio()
    {
        try
        {
            await audioGate.EnterAsync(PriorityGate.Lane.Background);
            try { if (!Audio.Public.Blocked && available) await Audio.Recover(false); }
            finally { audioGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* RecoverAudioIfDue and Retry in Temp still apply */ }
    }

    private async Task ResumeCapture(BridgeHandoff h, HistoryStore? resumed)
    {
        if (!h.Capture.Running || h.Capture.SourceId is not { } id) return;
        long gap = Math.Max(0, (utcNow() - h.ExitedUtcMs) / 1000);
        try
        {
            // Only a source the list offers now (outputs are render endpoints, apps are running windows); Start checks
            // once more that an output is an output (RequireOutput). The narrow exception to "capture never starts by
            // itself": a verified update hand-over that says it was running.
            var source = mimicCapture.Sources().FirstOrDefault(s => s.Id == id);
            if (source == null)
            {
                sourceLost = true;
                Log($"After the update the output Mimic was recording ({h.Capture.SourceName ?? id}) is not available. Choose another output.");
                return;
            }
            var store = EnsureHistory(mimicCapture.Rate);
            await mimicCapture.Start(source, new HistorySink(this));
            sourceLost = false;
            if (h.Capture.Transcribing && transcriber.ModelReady) { try { await transcriber.Enable(CurrentHistory, false); } catch (Exception ex) when (ex is not OutOfMemoryException) { } }
            Log($"Mimic resumed after the update (gap {gap} s)" + (resumed != null && store != resumed ? "; the recording history starts again" : ""));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sourceLost = true;
            Log("Mimic could not resume listening after the update: " + ex.Message);
        }
    }

    private void ResumeHotkey(HotkeyHandoff h)
    {
        try
        {
            if (HotkeyKeys.Contains(h.Key) && h.Key != hotkey.Name) { var next = makeHotkey(h.Key); hotkey.Dispose(); hotkey = next; }
            if (!h.Active) return;
            hotkey.Register(() => _ = HotkeyPressed());
            hotkeyStatus = $"Press {hotkey.Name} in any app to save the last {hotkeySeconds} s";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { hotkeyStatus = ex.Message; Log($"{h.Key}: {ex.Message}"); }
    }
}
