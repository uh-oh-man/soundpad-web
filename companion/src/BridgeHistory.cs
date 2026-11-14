using System.Text.Json;
namespace SoundpadWeb;

/// <summary>The host's Mimic settings (2.39), in their own small file so older companions never see them.</summary>
public sealed record MimicSettings(int Format = 1, int HistorySeconds = HistoryStore.DefaultWindowSeconds, string Subtitles = "gaming", string? SourceId = null, string? SourceName = null);

/// <summary>The history timeline as one person may see it (reply of historyTimeline, 2.39).</summary>
/// <param name="FromFrame">A guest's join frame (what they can see from); null for the host.</param>
/// <param name="Shared">Earlier ranges the host shared with guests.</param>
/// <param name="Replays">2.40: spans where the companion itself played into the game (its own replay), clipped to what the asker may see.</param>
public sealed record HistoryTimelineReply(string? SessionId, int Rate, long LiveFrame, long? LiveWallUtcMs, long NowUtcMs, bool Recording, int WindowSeconds, HistoryRange[] Available, HistorySpan[] Spans, HistoryGap[] Gaps, long? FromFrame, HistoryRange[] Shared, bool Transcribing, HistoryRange[]? Replays = null);
/// <summary>A word as the website receives it (historyTranscript, 2.39). Frames are on the history timeline.</summary>
public sealed record HistoryWordReply(long Id, long Start, long End, string Text, float Probability, int StretchMs, bool PauseBefore, bool PauseAfter, string Status, long PhraseId, int Revision, bool Corrected);

// Rolling recording history (2.39): Mimic records into a HistoryStore under <data>\history\ for the whole companion
// launch, instead of a 45-second ring in memory. See reports/OVERHAUL-PLAN.md, "Rolling recording and clip editing".
public sealed partial class Bridge
{
    public const int MaxHistoryBins = 2048;
    /// <summary>Longest stretch one transcript query covers.</summary>
    public const int MaxTranscriptQuerySeconds = 600;
    private volatile HistoryStore? history;
    private readonly object historyGate = new();
    private HistoryOptions historyOptions = new();
    private MimicSettings? mimicSettings;
    private volatile bool sourceLost;
    private long recoveredMs;
    private readonly long launchedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>
    /// When a guest was admitted: the wall time (UTC ms) and, as a floor that clock changes and late run anchors
    /// cannot move, the live frame of <paramref name="SessionId"/>'s history at that moment.
    /// </summary>
    private readonly record struct GuestJoin(long Wall, long Frame, string? SessionId);
    /// <summary>When each guest (by participant id) was first admitted in this hosting session. Never lowered.</summary>
    private readonly Dictionary<string, GuestJoin> guestJoins = [];
    /// <summary>
    /// Guests the host disconnected or turned away, and when (UTC ms). Their old join time is gone; only a new
    /// admission (a join time after this) gives them one again. Guarded by <see cref="guestJoins"/>.
    /// </summary>
    private readonly Dictionary<string, long> forgottenGuests = [];
    private const int MaxGuestJoins = 4096;
    /// <summary>Turns word and phrase ids into ids that tell a guest nothing about how much was said before they joined.</summary>
    private readonly byte[] guestIdKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    /// <summary>Earlier ranges the host shared with guests, for the current history session.</summary>
    private readonly List<HistoryRange> sharedRanges = [];
    private string HistoryRoot => store.DirectoryPath;
    private string SettingsPath => Path.Combine(store.DirectoryPath, "mimic-settings.json");

    /// <summary>Test seam: history options (an in-memory file system, a test clock, a longer in-memory tail).</summary>
    public void UseHistoryOptions(HistoryOptions options) => historyOptions = options;
    private Func<long> utcNow = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>
    /// Test seam: the wall clock for the history and guest join times. Fixture capture runs faster than real time
    /// when a test advances it, so the test advances this clock with it.
    /// </summary>
    public void UseClock(Func<long> clock) { utcNow = clock; historyOptions = historyOptions with { UtcNowMs = clock }; }
    /// <summary>The current history session, if anything was recorded (tests and the harness).</summary>
    public HistoryStore? History => history;

    private MimicSettings Settings
    {
        get
        {
            if (mimicSettings != null) return mimicSettings;
            MimicSettings loaded = new();
            try
            {
                if (File.Exists(SettingsPath) && new FileInfo(SettingsPath).Length < 64 * 1024 && JsonSerializer.Deserialize<MimicSettings>(File.ReadAllText(SettingsPath), Json.Options) is { } read)
                    // Anything out of range is ignored field by field, never trusted.
                    loaded = new(1, read.HistorySeconds is >= HistoryStore.MinWindowSeconds and <= HistoryStore.MaxWindowSeconds ? read.HistorySeconds : HistoryStore.DefaultWindowSeconds,
                        SubtitleModes.Parse(read.Subtitles) is { } mode ? SubtitleModes.Name(mode) : "gaming",
                        // Only ever used to preselect a source that the current list offers; never started from here.
                        read.SourceId is { Length: <= 512 } id ? id : null,
                        read.SourceName is { Length: <= 600 } name ? name : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            return mimicSettings = loaded;
        }
    }
    private void SaveSettings(MimicSettings next)
    {
        mimicSettings = next;
        try { var temporary = SettingsPath + ".tmp"; File.WriteAllText(temporary, Json.Write(next)); File.Move(temporary, SettingsPath, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Mimic settings could not be saved: " + ex.Message); }
    }

    private MimicHistoryState HistorySnapshot()
    {
        var h = history; string? problem = null;
        if (h != null) { try { var totals = h.Totals(HistoryAccess.Host); problem = totals.WriteError != null ? "The recording history cannot be written to disk: " + totals.WriteError : totals.Overruns > 0 ? "The disk fell behind and some audio was lost; the gaps are marked." : null; } catch (HistoryException) { } }
        return new(h?.SessionId, Settings.HistorySeconds, Settings.Subtitles, sourceLost, Interlocked.Read(ref recoveredMs), problem);
    }

    /// <summary>The history with the capture brought up to now (silence padded). Null before anything was recorded.</summary>
    private HistoryStore? CurrentHistory()
    {
        try { mimicCapture.Pump(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        var h = history;
        // Words go with their audio also while transcripts are off (the live loop is the other place that prunes).
        transcriber.PruneExpired(h);
        return h;
    }

    /// <summary>The capture writes here; it follows <see cref="history"/>, so Discard can swap the store under a running capture.</summary>
    private sealed class HistorySink(Bridge bridge) : IMimicSink
    {
        public void Write(ReadOnlySpan<float> mono) => bridge.history?.Append(mono, bridge.utcNow() - (long)mono.Length * 1000 / Math.Max(1, bridge.history?.Rate ?? 1));
        // Dated by its first frame, so a run that silence opens starts when that silence began, not when it was padded.
        public void Silence(long frames, double endBehindSeconds)
        {
            if (bridge.history is not { } h) return;
            h.AppendSilence(frames, bridge.utcNow() - (long)Math.Round(endBehindSeconds * 1000) - frames * 1000 / Math.Max(1, h.Rate));
        }
        // The capture lost track of time (the PC slept, or nothing pumped it for a long while): end the run here, so
        // the next audio opens a new run at the real clock instead of minutes of invented silence.
        public void Paused() => bridge.history?.EndRun(HistoryGapReason.Stopped);
        public void Lost(string status) => bridge.SourceLost(status);
        public void Drain() => bridge.history?.Flush(10000);
    }

    /// <summary>The output being recorded went away: keep the history, mark the gap, and ask the host for another output.</summary>
    private void SourceLost(string status)
    {
        history?.EndRun(HistoryGapReason.DeviceLost);
        sourceLost = true;
        Log(status);
        ThreadPool.QueueUserWorkItem(_ => Publish());
    }

    /// <summary>This launch's history at the capture's rate, created at the first Start.</summary>
    private HistoryStore EnsureHistory(int rate)
    {
        lock (historyGate)
        {
            if (history is { } existing)
            {
                if (existing.Rate == rate) return existing;
                CloseHistoryLocked(); // a recovered session from another capture rate (only fixtures differ)
            }
            var created = HistoryStore.Create(HistoryRoot, historyOptions with { Rate = rate, WindowSeconds = Settings.HistorySeconds });
            UseStoreLocked(created);
            return created;
        }
    }
    private void UseStoreLocked(HistoryStore? next)
    {
        history = next;
        lock (sharedRanges) sharedRanges.Clear();
        TranscriptWords? words = null;
        if (next != null)
        {
            words = TranscriptWords.Open(HistoryRoot, next.SessionId);
            // After a crash the history resumes at its last durable frame, and new audio reuses the frames after it:
            // words recognised past that point describe audio that is gone, so they go before anyone can see them.
            words.CutAt(next.LiveFrame);
        }
        transcriber.Attach(next, words);
        lock (peekGate) lastPeek = null;
    }
    private void CloseHistoryLocked()
    {
        var old = history;
        // Drafts belong to the history they pin (2.40); its own-replay marks go with it.
        ForgetDrafts(old);
        if (old != null) ownReplays.Clear(old.SessionId);
        UseStoreLocked(null);
        if (old == null) return;
        old.Close(true);
        TranscriptWords.Delete(HistoryRoot, old.SessionId);
    }

    /// <summary>
    /// Startup (2.39): a history left by a launch that did not close normally (a crash, power loss) is recovered into
    /// the same session and the host is told; any other leftover history is removed. Returns the recovered frames.
    /// </summary>
    public long RecoverHistory()
    {
        lock (historyGate)
        {
            if (history != null) return 0;
            IReadOnlyList<HistoryRecoverable> found;
            try { found = HistoryStore.Recover(HistoryRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
            var crashed = found.Where(r => r.Valid && !r.InUse && !r.CleanlyClosed && r.RetainedFrames > 0).OrderByDescending(r => r.UpdatedUtcMs).FirstOrDefault();
            foreach (var r in found.Where(r => r != crashed && !r.InUse))
            {
                try { HistoryStore.Discard(HistoryRoot, r.SessionId); TranscriptWords.Delete(HistoryRoot, r.SessionId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException) { Log($"Old recording history {r.SessionId} could not be removed: {ex.Message}"); }
            }
            // Transcripts whose history is gone.
            try
            {
                var transcripts = Path.Combine(HistoryRoot, "transcripts");
                if (Directory.Exists(transcripts))
                    foreach (var folder in Directory.GetDirectories(transcripts))
                        if (Path.GetFileName(folder) != crashed?.SessionId) TranscriptWords.Delete(HistoryRoot, Path.GetFileName(folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (crashed == null) return 0;
            try
            {
                var recovered = HistoryStore.Open(HistoryRoot, crashed.SessionId, HistoryGapReason.Crash, historyOptions with { WindowSeconds = Settings.HistorySeconds });
                try { recovered.SetWindow(Settings.HistorySeconds); } catch (ArgumentOutOfRangeException) { }
                UseStoreLocked(recovered);
                long frames = recovered.Timeline(HistoryAccess.Host).Available.Sum(r => r.Frames);
                Interlocked.Exchange(ref recoveredMs, frames * 1000 / recovered.Rate);
                Log($"Recovered {frames / (double)recovered.Rate / 60:0.#} minutes of recording history from the last session");
                return frames;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HistoryException or InvalidOperationException)
            {
                Log("The recording history from the last session could not be recovered: " + ex.Message);
                return 0;
            }
        }
    }

    /// <summary>Ordinary quit (2.39): the session's history and transcript are deleted.</summary>
    public void CloseHistory()
    {
        lock (historyGate)
        {
            try { mimicCapture.Stop(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            CloseHistoryLocked();
        }
    }

    /// <summary>
    /// Records when a guest was admitted (the owner page passes it with every relayed command, 2.39). The first time
    /// this hosting session sees a participant wins; it can never be lowered later. Clamped to this launch and to now.
    /// A guest the host disconnected or turned away (<see cref="ForgetGuest"/>) counts again only from a new
    /// admission: a join time sent after that.
    /// </summary>
    public void NoteGuest(Participant? participant, string? requester, long? joinedAtUtcMs)
    {
        var key = participant?.Id ?? requester;
        if (key == null || key.Length > 128) return;
        lock (guestJoins)
        {
            if (guestJoins.ContainsKey(key)) return;
            if (forgottenGuests.TryGetValue(key, out long forgotten))
            {
                if (joinedAtUtcMs is not long joined || joined < forgotten) return;
                forgottenGuests.Remove(key);
            }
        }
        long now = utcNow();
        // Never throws, also when the clock was set back past this launch's start.
        long wall = Math.Max(launchedUtcMs, Math.Min(joinedAtUtcMs ?? now, now));
        var join = JoinAt(wall, now);
        lock (guestJoins) if (!guestJoins.ContainsKey(key) && guestJoins.Count < MaxGuestJoins) guestJoins[key] = join;
    }

    /// <summary>
    /// A join at <paramref name="wall"/> on the current history. Its frame floor comes from the live frame and the time
    /// elapsed since, on one clock: a run whose wall times were anchored late, or a clock that was set back, can make
    /// the history's own wall times point earlier than the join, and must never show a guest audio from before it.
    /// </summary>
    private GuestJoin JoinAt(long wall, long now)
    {
        var h = CurrentHistory();
        if (h == null) return new(wall, 0, null);
        try
        {
            long live = h.LiveFrame;
            long byElapsed = Math.Max(0, live - Math.Max(0, now - wall) * h.Rate / 1000);
            return new(wall, Math.Max(byElapsed, h.FrameAtOrAfterWall(HistoryAccess.Host, wall)), h.SessionId);
        }
        catch (HistoryException) { return new(wall, 0, null); }
    }

    /// <summary>
    /// The host disconnected or turned away this guest (revokePeer with a guestId, 2.39): their join time ends, so
    /// letting them back in records a new one and they never see what was recorded while they were out.
    /// </summary>
    public void ForgetGuest(string participantId)
    {
        if (participantId.Length is 0 or > 128) return;
        long now = utcNow();
        lock (guestJoins)
        {
            guestJoins.Remove(participantId);
            if (forgottenGuests.Count >= MaxGuestJoins && !forgottenGuests.ContainsKey(participantId))
                forgottenGuests.Remove(forgottenGuests.MinBy(p => p.Value).Key);
            forgottenGuests[participantId] = now;
        }
    }
    /// <summary>A new invitation or ended hosting session starts with nobody admitted and nothing shared.</summary>
    private void ForgetGuests()
    {
        lock (guestJoins) { guestJoins.Clear(); forgottenGuests.Clear(); }
        lock (sharedRanges) sharedRanges.Clear();
    }

    /// <summary>
    /// What a requester may see of <paramref name="h"/>: the host everything; a guest from the moment they were first
    /// admitted (as a frame of this history) plus what the host shared. Built for every query, never cached.
    /// </summary>
    private HistoryAccess AccessFor(string actor, Participant? participant, string? requester, HistoryStore h)
    {
        if (actor == "host") return HistoryAccess.Host;
        var key = participant?.Id ?? requester ?? "guest";
        GuestJoin join;
        lock (guestJoins)
        {
            if (!guestJoins.TryGetValue(key, out join))
            {
                // Never admitted through the owner page (or disconnected since): from now. A disconnected guest's
                // stray query must not become their join time, or a later admission would count from it.
                join = new(utcNow(), h.LiveFrame, h.SessionId);
                if (!forgottenGuests.ContainsKey(key) && guestJoins.Count < MaxGuestJoins) guestJoins[key] = join;
            }
        }
        long from = h.FrameAtOrAfterWall(HistoryAccess.Host, join.Wall);
        // The frame floor belongs to the history it was taken on (Discard starts a new one).
        if (join.SessionId == h.SessionId) from = Math.Max(from, Math.Min(join.Frame, h.LiveFrame));
        HistoryRange[] shared;
        lock (sharedRanges) shared = [.. sharedRanges];
        return HistoryAccess.Guest(from, shared, join.Wall);
    }

    /// <summary>A word or phrase id as a guest sees it: stable for this launch, but not a count of what came before.</summary>
    private long GuestId(long value, byte kind)
    {
        Span<byte> input = stackalloc byte[9];
        input[0] = kind;
        BitConverter.TryWriteBytes(input[1..], value);
        var mac = System.Security.Cryptography.HMACSHA256.HashData(guestIdKey, input);
        return (long)(BitConverter.ToUInt64(mac, 0) & ((1UL << 52) - 1)) + 1;
    }

    /// <summary>A typed history refusal as the person sees it.</summary>
    private static string HistoryMessage(HistoryException ex, bool host) => ex switch
    {
        HistoryUnavailableException { Reason: HistoryUnavailableException.NotShared } when !host => "That moment is from before you joined. Ask the host to share it.",
        HistoryUnavailableException { Reason: HistoryUnavailableException.NotRecorded } => "That moment has not been recorded yet.",
        HistoryUnavailableException => "That moment is no longer in the history. Select it again.",
        _ => ex.Message,
    };

    private static HistoryRange RangeOf(Command c, long maxFrames, string what)
    {
        if (c.StartFrame is not long start || c.EndFrame is not long end || start < 0 || end <= start) throw new InvalidDataException($"Choose a part of the history to {what}.");
        if (end - start > maxFrames) throw new InvalidDataException("That part of the history is too long.");
        return new(start, end);
    }

    /// <summary>The 2.39 history commands. Null when the op is not one of them.</summary>
    private Reply? ExecuteHistory(Command c, string actor, Participant? participant, string? requester)
    {
        bool host = actor == "host";
        switch (c.Op)
        {
            case "historyTimeline":
                {
                    var h = CurrentHistory();
                    if (h == null) return new("ack", c.Id, true, Data: new HistoryTimelineReply(null, mimicCapture.Rate, 0, null, utcNow(), false, Settings.HistorySeconds, [], [], [], null, [], transcriber.Enabled));
                    var access = AccessFor(actor, participant, requester, h);
                    var t = h.Timeline(access);
                    HistoryRange[] shared; lock (sharedRanges) shared = [.. sharedRanges];
                    var replays = ownReplays.Ranges(h.SessionId).Where(r => Math.Min(r.EndFrame, t.LiveFrame) > r.StartFrame).Select(r => Visible(access, r.StartFrame, Math.Min(r.EndFrame, t.LiveFrame))).OfType<HistoryRange>().ToArray();
                    return new("ack", c.Id, true, Data: new HistoryTimelineReply(h.SessionId, t.Rate, t.LiveFrame, t.LiveWallUtcMs, utcNow(), t.Recording, t.WindowSeconds, t.Available, t.Spans, t.Gaps, host ? null : access.FromFrame, host ? shared : [.. access.SharedRanges], transcriber.Enabled, replays));
                }
            case "historyLevels":
                {
                    var h = CurrentHistory() ?? throw new InvalidDataException("Mimic has not recorded anything yet.");
                    if (c.SourceId != null && c.SourceId != h.SessionId) throw new InvalidDataException("Mimic restarted since you looked. Look again.");
                    var range = RangeOf(c, (long)(HistoryStore.MaxWindowSeconds + 60) * h.Rate * 4, "show");
                    int bins = c.Bins is int b and >= 1 and <= MaxHistoryBins ? b : throw new InvalidDataException($"A waveform can have 1 to {MaxHistoryBins} bins.");
                    var levels = h.Levels(AccessFor(actor, participant, requester, h), range.StartFrame, range.EndFrame, bins);
                    return new("ack", c.Id, true, Data: new { start = range.StartFrame, end = range.EndFrame, bins, levels });
                }
            case "historyTranscript":
                {
                    var h = CurrentHistory() ?? throw new InvalidDataException("Mimic has not recorded anything yet.");
                    if (c.SourceId != null && c.SourceId != h.SessionId) throw new InvalidDataException("Mimic restarted since you looked. Look again.");
                    var range = RangeOf(c, (long)MaxTranscriptQuerySeconds * h.Rate, "read");
                    var access = AccessFor(actor, participant, requester, h);
                    var visible = h.Timeline(access).Available;
                    var words = transcriber.WordsBetween(h, access, range.StartFrame, range.EndFrame)
                        // Ids count every word and phrase since the history began; a guest gets ids that do not say how many came before they joined.
                        .Select(w => new HistoryWordReply(host ? w.Word.Id : GuestId(w.Word.Id, 1), w.Word.Start, w.Word.End, w.Correction?.Text ?? w.Word.Text, w.Word.Probability, w.Word.StretchMs, w.Word.PauseBefore, w.Word.PauseAfter, w.Word.Status, host ? w.Word.PhraseId : GuestId(w.Word.PhraseId, 2), w.Word.Revision, w.Correction != null)).ToArray();
                    var pending = transcriber.PendingBetween(h, visible, range.StartFrame, range.EndFrame);
                    // Viewing an older stretch asks for it to be recognised (only what this person may see).
                    foreach (var p in pending) transcriber.Request(p);
                    return new("ack", c.Id, true, Data: new { words, pending, transcribing = transcriber.Enabled });
                }
            case "historyShare":
            case "historyUnshare":
                {
                    if (!host) throw new InvalidDataException("Only the host can share recording history.");
                    var h = CurrentHistory() ?? throw new InvalidDataException("Mimic has not recorded anything yet.");
                    lock (sharedRanges)
                    {
                        if (c.Op == "historyUnshare" && c.StartFrame == null) sharedRanges.Clear();
                        else
                        {
                            var range = RangeOf(c, long.MaxValue, "share");
                            var next = new List<HistoryRange>();
                            foreach (var r in sharedRanges)
                            {
                                // Unshare subtracts; share adds (touching ranges merge below).
                                if (c.Op == "historyUnshare" && r.StartFrame < range.EndFrame && r.EndFrame > range.StartFrame)
                                {
                                    if (r.StartFrame < range.StartFrame) next.Add(new(r.StartFrame, range.StartFrame));
                                    if (r.EndFrame > range.EndFrame) next.Add(new(range.EndFrame, r.EndFrame));
                                }
                                else next.Add(r);
                            }
                            if (c.Op == "historyShare") next.Add(range);
                            next.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
                            var merged = new List<HistoryRange>();
                            foreach (var r in next)
                                if (merged.Count > 0 && r.StartFrame <= merged[^1].EndFrame) merged[^1] = new(merged[^1].StartFrame, Math.Max(merged[^1].EndFrame, r.EndFrame));
                                else merged.Add(r);
                            if (merged.Count > HistoryAccess.MaxSharedRanges) throw new InvalidDataException($"Share at most {HistoryAccess.MaxSharedRanges} separate parts of the history.");
                            sharedRanges.Clear(); sharedRanges.AddRange(merged);
                        }
                        Log(c.Op == "historyShare" ? "Shared part of the recording history with guests" : "Stopped sharing part of the recording history");
                        return new("ack", c.Id, true, Data: new { shared = sharedRanges.ToArray(), sessionId = h.SessionId });
                    }
                }
            case "historySettings":
                {
                    if (!host) throw new InvalidDataException("Only the host can change the recording history.");
                    var next = Settings;
                    if (c.Seconds is int seconds)
                    {
                        if (seconds is < HistoryStore.MinWindowSeconds or > HistoryStore.MaxWindowSeconds) throw new InvalidDataException($"Keep {HistoryStore.MinWindowSeconds / 60} to {HistoryStore.MaxWindowSeconds / 60} minutes of history.");
                        history?.SetWindow(seconds);
                        // A shorter window drops audio at once; its words go with it.
                        transcriber.PruneExpired(history, force: true);
                        next = next with { HistorySeconds = seconds };
                        Log($"Recording history keeps the last {seconds / 60.0:0.#} minutes");
                    }
                    if (c.Subtitles != null)
                    {
                        var mode = SubtitleModes.Parse(c.Subtitles) ?? throw new InvalidDataException("Choose Gaming, Balanced or Fast subtitles.");
                        transcriber.UseMode(mode);
                        next = next with { Subtitles = SubtitleModes.Name(mode) };
                    }
                    SaveSettings(next);
                    Publish();
                    return new("ack", c.Id, true);
                }
            case "historyDiscard":
                {
                    // The host's Discard on the "recovered history" notice: everything recorded so far goes; a running capture continues into a new history.
                    if (!host) throw new InvalidDataException("Only the host can discard recording history.");
                    lock (historyGate)
                    {
                        bool running = mimicCapture.Running;
                        int rate = history?.Rate ?? mimicCapture.Rate;
                        CloseHistoryLocked();
                        if (running) UseStoreLocked(HistoryStore.Create(HistoryRoot, historyOptions with { Rate = rate, WindowSeconds = Settings.HistorySeconds }));
                    }
                    Interlocked.Exchange(ref recoveredMs, 0);
                    Log("Discarded the recording history");
                    Publish();
                    return new("ack", c.Id, true);
                }
            case "historyCorrect":
                {
                    if (!host) throw new InvalidDataException("Only the host can correct the transcript.");
                    var h = CurrentHistory() ?? throw new InvalidDataException("Mimic has not recorded anything yet.");
                    if (c.WordId is not long id || c.Text is not { } text || text.Trim().Length is 0 or > 100) throw new InvalidDataException("Type the corrected word (up to 100 characters).");
                    var fix = transcriber.Correct(h, id, text.Trim(), "Host");
                    return new("ack", c.Id, true, Data: fix);
                }
            default: return null;
        }
    }
}
