namespace SoundpadWeb;

public sealed partial class Bridge
{
    /// <summary>The live view of pre-2.39 websites (the newest 30 s, 300 buckets) and the longest clip.</summary>
    public const int MimicWindowMs = 30000, MimicBuckets = 300, MimicMinimumMs = 150;
    private IMimicCapture mimicCapture = new WasapiMimicCapture();
    private MimicTranscriber transcriber = new(new WhisperEngine(
        Path.Combine(store.DirectoryPath, "models", "ggml-base.en.bin"),
        // Reuse the model Voice Replay Studio already downloaded, if this PC has one.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voice Replay Studio", "Models", "ggml-base.en.bin"),
        Path.Combine(store.DirectoryPath, "native", "whisper-1.9.1")));
    /// <summary>F6 lengths, as Voice Replay Studio offered them; 10 s is the default.</summary>
    public static readonly int[] HotkeyLengths = [3, 5, 10, 15, 30];
    private volatile int hotkeySeconds = 10;
    /// <summary>Keys the host can pick for the hotkey; F6 (Voice Replay Studio's) is the default.</summary>
    public static readonly string[] HotkeyKeys = ["F6", "F7", "F8", "F9", "F10"];
    /// <summary>The real Windows hotkey for one of <see cref="HotkeyKeys"/> (virtual keys F6 = 0x75 … F10 = 0x79).</summary>
    public static IGlobalHotkey WindowsKey(string key) => new WindowsHotkey((uint)(0x75 + Array.IndexOf(HotkeyKeys, key)), key);
    private Func<string, IGlobalHotkey> makeHotkey = WindowsKey;
    private IGlobalHotkey hotkey = new WindowsHotkey();
    private volatile string hotkeyStatus = "Off";
    public const int RecentClips = 5;
    private readonly List<MimicClipInfo> recentClips = [];
    private MimicClipInfo[] RecentSnapshot { get { lock (recentClips) return [.. recentClips]; } }
    private MimicState MimicSnapshot => new(mimicCapture.Running, mimicCapture.SourceName, mimicCapture.Status, MimicWindowMs, transcriber.Enabled, transcriber.ModelReady, transcriber.Status, hotkey.Active, hotkeyStatus, RecentSnapshot, hotkeySeconds, hotkey.Name, HistorySnapshot());

    /// <summary>Replaces the system-wide hotkey; tests use a fixture that can be "pressed" directly.</summary>
    public void UseHotkey(IGlobalHotkey key) { hotkey.Dispose(); hotkey = key; }
    /// <summary>Replaces how hotkeys are made, so tests can pick other keys without touching Windows.</summary>
    public void UseHotkeys(Func<string, IGlobalHotkey> factory) { makeHotkey = factory; UseHotkey(factory(HotkeyKeys[0])); }

    /// <summary>One-line Mimic events for the companion console (its only interface on the host PC).</summary>
    public event Action<string>? MimicLog;
    private void Log(string message) => MimicLog?.Invoke(message);

    /// <summary>
    /// End hosting: capture, transcripts and the hotkey stop, guests and shares are forgotten. The recording history
    /// itself stays until the companion closes (2.39), with a "stopped" gap.
    /// </summary>
    private void StopMimic()
    {
        if (mimicCapture.Running) Log("Hosting ended: stopped listening; the recording history stays until the companion closes");
        StopCapture();
        transcriber.Disable(); hotkey.Unregister(); hotkeyStatus = "Off"; lock (recentClips) recentClips.Clear(); lock (peekGate) lastPeek = null;
        ForgetGuests();
    }
    private void StopCapture()
    {
        mimicCapture.Stop();
        history?.EndRun(HistoryGapReason.Stopped);
        sourceLost = false;
    }

    /// <summary>The host pressed the hotkey: save the last few seconds (3–30, host's choice), like Voice Replay Studio's F6.</summary>
    public async Task HotkeyPressed()
    {
        try
        {
            var h = CurrentHistory();
            if (h == null || !mimicCapture.Running) throw new InvalidDataException($"{hotkey.Name} was pressed while Mimic was not listening.");
            int seconds = hotkeySeconds;
            long stop = h.LiveFrame, start = Math.Max(0, stop - (long)h.Rate * seconds);
            var words = string.Join(" ", transcriber.Lines(h, HistoryAccess.Host, start, stop).Select(l => l.Text.Trim())).Trim();
            var reply = await SaveMimic(h, HistoryAccess.Host, start, stop, words.Length > 0 ? words : null, "Host", true, "host", audioAudience);
            hotkeyStatus = reply.Ok ? $"Saved the last {seconds} s at {DateTime.Now:HH:mm:ss}" : reply.Error ?? "Could not save.";
            if (!reply.Ok) Log($"{hotkey.Name}: {hotkeyStatus}");
        }
        catch (InvalidDataException ex) { hotkeyStatus = ex.Message; Log($"{hotkey.Name}: {ex.Message}"); }
        catch (HistoryException ex) { hotkeyStatus = HistoryMessage(ex, true); Log($"{hotkey.Name}: {hotkeyStatus}"); }
        Publish(); CommandAccepted?.Invoke();
    }

    /// <summary>Saves [start, stop) of the history, as <paramref name="access"/> may read it, as a Temp sound. Pins nothing.</summary>
    private async Task<Reply> SaveMimic(HistoryStore h, HistoryAccess access, long start, long stop, string? name, string by, bool host, string owner, string? audience, string? guest = null)
    {
        if (stop - start < (long)h.Rate * MimicMinimumMs / 1000) throw new InvalidDataException("Mimic had not started listening yet at that moment.");
        var samples = h.Read(access, start, (int)(stop - start));
        if (samples.All(s => Math.Abs(s) < 0.0005f)) throw new InvalidDataException("That selection is silent. Nothing was saved.");
        name = string.IsNullOrWhiteSpace(name) ? "Mimic " + DateTime.Now.ToString("HH.mm.ss") : name.Trim();
        if (name.Length > 100) name = name[..99].TrimEnd() + "…";
        var reply = await Upload(MimicRing.Wav(samples, h.Rate), name + ".wav", by, host, owner, audience, mimic: true, guest: guest);
        if (reply.Ok && reply.Data?.GetType().GetProperty("assetId")?.GetValue(reply.Data) is string assetId)
        {
            lock (recentClips)
            {
                recentClips.RemoveAll(c => c.AssetId == assetId);
                recentClips.Insert(0, new(assetId, name, by, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Log($"{by} mimicked \"{name}\" ({samples.Length / (double)h.Rate:0.0} s), now in Temp");
                if (recentClips.Count > RecentClips) recentClips.RemoveRange(RecentClips, recentClips.Count - RecentClips);
            }
            Publish();
        }
        return reply;
    }

    /// <summary>Replaces the speech engine; tests use a deterministic fixture instead of Whisper.</summary>
    public void UseTranscriptEngine(ITranscriptEngine engine)
    {
        transcriber.Dispose(); transcriber = new(engine);
        transcriber.UseMode(SubtitleModes.Parse(Settings.Subtitles) ?? SubtitleMode.Gaming);
        var h = history;
        TranscriptWords? words = null;
        if (h != null) { words = TranscriptWords.Open(HistoryRoot, h.SessionId); words.CutAt(h.LiveFrame); } // as UseStoreLocked does
        transcriber.Attach(h, words);
    }

    /// <summary>Replaces the capture backend; tests and the e2e harness use a deterministic fixture source.</summary>
    public void UseMimicCapture(IMimicCapture capture) { mimicCapture.Dispose(); mimicCapture = capture; }

    private readonly object peekGate = new();
    private (string SessionId, long End, string Access, int[] Levels)? lastPeek;

    /// <summary>
    /// The newest 30 s as pre-2.39 websites show it. The end is shared by every viewer for 100 ms, so selections made
    /// by different people line up; levels come from the history's peak pyramid (no sample scan) and are clipped to
    /// what each person may see.
    /// </summary>
    private (long End, int[] Levels) PeekLevels(HistoryStore h, HistoryAccess access, long frames)
    {
        string key = access.IsHost ? "host" : access.FromFrame + ":" + string.Join(",", access.SharedRanges.Select(r => r.StartFrame + "-" + r.EndFrame));
        lock (peekGate)
        {
            long now = h.LiveFrame;
            long end = lastPeek is { } cached && cached.SessionId == h.SessionId && now - cached.End < h.Rate / 10 && now >= cached.End ? cached.End : now;
            if (lastPeek is { } same && same.SessionId == h.SessionId && same.End == end && same.Access == key) return (end, same.Levels);
            var levels = LegacyLevels(h, access, end, frames, MimicBuckets);
            lastPeek = (h.SessionId, end, key, levels);
            return (end, levels);
        }
    }

    /// <summary>Peak 0–1000 per bucket for the window ending at <paramref name="end"/>; time before frame 0 or not visible is 0.</summary>
    private static int[] LegacyLevels(HistoryStore h, HistoryAccess access, long end, long frames, int buckets)
    {
        var result = new int[buckets];
        long start = end - frames;
        int first = 0;
        while (first < buckets && start + frames * first / buckets < 0) first++;
        if (first >= buckets) return result;
        long from = start + frames * first / buckets;
        if (end <= from) return result;
        var pairs = h.Levels(access, from, end, buckets - first);
        for (int b = first; b < buckets; b++)
        {
            int i = b - first, peak = Math.Max(Math.Abs((int)pairs[2 * i]), Math.Abs((int)pairs[2 * i + 1]));
            result[b] = (int)Math.Round(Math.Min(1000, peak * 1000.0 / short.MaxValue));
        }
        return result;
    }

    /// <summary>
    /// The history frames a person selected (see docs/MIMIC.md, Protocol): either explicit frames (2.39) or, as older
    /// websites send it, a range of the 30 s peek they saw. Up to 30 s from anywhere in the history.
    /// </summary>
    private (HistoryStore History, long Start, long Stop) Selection(Command c)
    {
        var h = CurrentHistory() ?? throw new InvalidDataException("Mimic is not listening.");
        if (c.SourceId != null && c.SourceId != h.SessionId) throw new InvalidDataException("Mimic restarted since you selected that. Select it again.");
        if (c.StartFrame is long a && c.EndFrame is long b)
        {
            if (a < 0 || (b - a) * 1000 < (long)h.Rate * MimicMinimumMs || (b - a) * 1000 > (long)h.Rate * MimicWindowMs) throw new InvalidDataException($"Select between {MimicMinimumMs} ms and {MimicWindowMs / 1000} seconds of audio.");
            return (h, a, b);
        }
        if (c.MimicEnd is not long end || c.PositionMs is not long from || c.EndMs is not long to || from < 0 || to > MimicWindowMs || to - from < MimicMinimumMs) throw new InvalidDataException($"Select between {MimicMinimumMs} ms and {MimicWindowMs / 1000} seconds of audio.");
        long windowStart = end - (long)h.Rate * MimicWindowMs / 1000;
        // Time before capture started has no audio; trim it instead of treating it as expired.
        return (h, Math.Max(0, windowStart + from * h.Rate / 1000), windowStart + to * h.Rate / 1000);
    }

    private static bool IsMimic(string op) => op is "mimicSources" or "mimicStart" or "mimicStop" or "mimicPeek" or "mimicClip" or "mimicTranscribe" or "mimicHotkey" or "mimicPreview" or "mimicLibrary" or "mimicLibraryImport"
        or "historyTimeline" or "historyLevels" or "historyTranscript" or "historyShare" or "historyUnshare" or "historySettings" or "historyDiscard" or "historyCorrect"
        || IsDraftOp(op);
    /// <summary>What a guest with Guest Mimic may ask of Mimic; everything else is the host's.</summary>
    /// <summary>Since 2.40 also drafts, Quick Mimic and private preview (scoped by what they may see, rate limited).</summary>
    private static bool GuestMimicOp(string op) => op is "mimicPeek" or "mimicClip" or "mimicPreview" or "historyTimeline" or "historyLevels" or "historyTranscript" || IsDraftOp(op);

    private VoiceReplayLibrary voiceReplay = new();
    /// <summary>Points at another Voice Replay Studio data folder; tests use a temporary one.</summary>
    public void UseVoiceReplayLibrary(VoiceReplayLibrary library) => voiceReplay = library;

    private bool GuestMimicAllowed(string? requester, string? audience) =>
        disk.Permissions.GuestMimic && (audioAudience == null || sessionAccepting && audience == audioAudience) && (requester == null || !revokedAudioPeers.ContainsKey(requester));

    private static string StableSourceName(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"\s*\[\d+\]$", "");

    private async Task<Reply> ExecuteMimic(Command c, string actor, Participant? participant, string? requester, string? audience, Intent? intent = null)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && !GuestMimicOp(c.Op)) throw new InvalidDataException("Only the host can choose what Mimic listens to.");
            if (!host && !GuestMimicAllowed(requester, audience)) throw new InvalidDataException("The host has not enabled Mimic for guests.");
            if (ExecuteHistory(c, actor, participant, requester) is { } answered) return answered;
            if (await ExecuteDrafts(c, actor, participant, requester, audience, intent) is { } drafted) return drafted;
            switch (c.Op)
            {
                case "mimicSources":
                    {
                        var sources = mimicCapture.Sources();
                        // The source chosen last time (2.39): by id, or for an app by its name without the process id.
                        var remembered = sources.FirstOrDefault(s => s.Id == Settings.SourceId)
                            ?? (Settings.SourceName is { } named ? sources.FirstOrDefault(s => s.Kind == "app" && StableSourceName(s.Name) == StableSourceName(named)) : null);
                        return new("ack", c.Id, true, Data: new { sources, remembered = remembered?.Id });
                    }
                case "mimicStart":
                    {
                        // Only a listed source: outputs are render endpoints and apps are windows, never an input.
                        var source = mimicCapture.Sources().FirstOrDefault(s => s.Id == c.SourceId) ?? throw new InvalidDataException("That source is no longer available. Refresh the list.");
                        var h = EnsureHistory(mimicCapture.Rate);
                        // Changing source while recording leaves an honest gap between the two.
                        if (mimicCapture.Running) h.EndRun(HistoryGapReason.Stopped);
                        await mimicCapture.Start(source, new HistorySink(this));
                        sourceLost = false;
                        SaveSettings(Settings with { SourceId = source.Id, SourceName = source.Name });
                        Log($"Listening to {source.Name} (the last {Settings.HistorySeconds / 60.0:0.#} minutes stay on this PC until the companion closes)");
                        Publish(); CommandAccepted?.Invoke();
                        return new("ack", c.Id, true);
                    }
                case "mimicHotkey":
                    if (c.PositionMs is long lengthMs)
                    {
                        if (!HotkeyLengths.Contains((int)(lengthMs / 1000)) || lengthMs % 1000 != 0) throw new InvalidDataException($"Choose 3, 5, 10, 15 or 30 seconds for {hotkey.Name}.");
                        hotkeySeconds = (int)(lengthMs / 1000);
                        if (hotkey.Active) hotkeyStatus = $"Press {hotkey.Name} in any app to save the last {hotkeySeconds} s";
                    }
                    if (c.Action == "length") { Log($"{hotkey.Name} saves the last {hotkeySeconds} s"); }
                    else if (c.Action == "key")
                    {
                        // Register the new key before releasing the old one, so a key another app owns
                        // leaves the working hotkey in place.
                        if (c.Name is not { } key || !HotkeyKeys.Contains(key)) throw new InvalidDataException("Choose F6, F7, F8, F9 or F10 for the hotkey.");
                        if (key != hotkey.Name)
                        {
                            var next = makeHotkey(key);
                            // While off, register and release at once: a key another app owns is reported
                            // now, not later when the host turns the hotkey on.
                            try { next.Register(() => _ = HotkeyPressed()); }
                            catch { next.Dispose(); throw; }
                            if (hotkey.Active) hotkeyStatus = $"Press {key} in any app to save the last {hotkeySeconds} s";
                            else next.Unregister();
                            hotkey.Dispose(); hotkey = next;
                            Log($"Hotkey is now {key}");
                        }
                    }
                    else if (c.Action == "disable") { hotkey.Unregister(); hotkeyStatus = "Off"; Log($"{hotkey.Name} hotkey off"); }
                    else if (c.Action == "enable") { hotkey.Register(() => _ = HotkeyPressed()); hotkeyStatus = $"Press {hotkey.Name} in any app to save the last {hotkeySeconds} s"; Log($"{hotkey.Name} hotkey on: press it in any app to save the last {hotkeySeconds} s"); }
                    else throw new InvalidDataException("Unsupported hotkey setting.");
                    Publish();
                    return new("ack", c.Id, true);
                case "mimicStop":
                    StopCapture(); lock (peekGate) lastPeek = null; Log("Stopped listening; the recording history is kept until the companion closes"); Publish();
                    return new("ack", c.Id, true);
                case "mimicTranscribe":
                    transcriber.UseMode(SubtitleModes.Parse(Settings.Subtitles) ?? SubtitleMode.Gaming);
                    if (c.Action == "disable") { transcriber.Disable(); Log("Transcripts off"); }
                    else if (c.Action == "enable") { await transcriber.Enable(CurrentHistory, false); Log("Transcripts on (speech is recognized on this PC)"); }
                    else if (c.Action == "download") { if (transcriber.ModelReady) await transcriber.Enable(CurrentHistory, false); else transcriber.BeginDownload(CurrentHistory, Publish); }
                    else throw new InvalidDataException("Unsupported transcript setting.");
                    Publish();
                    return new("ack", c.Id, true);
                case "mimicPeek":
                    {
                        var h = CurrentHistory() ?? throw new InvalidDataException("Mimic is not listening.");
                        var access = AccessFor(actor, participant, requester, h);
                        long frames = (long)h.Rate * MimicWindowMs / 1000;
                        var (end, levels) = PeekLevels(h, access, frames);
                        return new("ack", c.Id, true, Data: new MimicPeek(end, h.Rate, MimicWindowMs, levels, mimicCapture.Running, transcriber.Lines(h, access, end - frames, end), h.SessionId));
                    }
                case "mimicClip":
                    {
                        var (h, start, stop) = Selection(c);
                        var result = (await SaveMimic(h, AccessFor(actor, participant, requester, h), start, stop, c.Name, Person(actor, participant).Name, host, requester ?? participant?.Id ?? actor, audience, PersonKey(actor, participant, requester))) with { Id = c.Id };
                        // "Mimic & play" as one command (2.35): plays only if nothing newer was asked for meanwhile.
                        return c.Autoplay ? await Autoplay(result, c, actor, participant, requester, audience, intent) : result;
                    }
                case "mimicPreview":
                    // The audio goes only to the person who asked (the host's tab over loopback; a guest, since 2.40,
                    // scoped to what they may see). It never reaches Soundpad or anyone else.
                    return Preview(c, actor, participant, requester);
                case "mimicLibrary":
                    // Host only (checked above): clips saved in Voice Replay Studio on this PC, read without changing them.
                    return new("ack", c.Id, true, Data: new { available = voiceReplay.Exists, clips = voiceReplay.List() });
                case "mimicLibraryImport":
                    {
                        var (wav, title) = voiceReplay.Read(c.SourceId ?? "");
                        // The name becomes a file name first: keep path separators from cutting the title short ("AC/DC").
                        var reply = await Upload(wav, title.Replace('/', '-').Replace('\\', '-') + ".wav", "Host", true, "host", audioAudience);
                        if (reply.Ok) Log($"Imported \"{title}\" from Voice Replay Studio into Temp");
                        return c.Autoplay ? await Autoplay(reply with { Id = c.Id }, c, actor, participant, requester, audience, intent) : reply with { Id = c.Id };
                    }
                default: throw new InvalidDataException("Unsupported command.");
            }
        }
        catch (InvalidDataException ex) { Publish(); return new("ack", c.Id, false, ex.Message); }
        // HistoryException does not derive from InvalidDataException (it is sealed): an expired moment is an answer, never a dropped socket.
        catch (HistoryException ex) { return new("ack", c.Id, false, HistoryMessage(ex, actor == "host")); }
    }
}
