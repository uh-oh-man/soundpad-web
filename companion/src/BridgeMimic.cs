namespace SoundpadWeb;

public sealed partial class Bridge
{
    public const int MimicWindowMs = 30000, MimicBuckets = 300, MimicMinimumMs = 150;
    /// <summary>Audio kept beyond the visible window, so a selection survives the round trip from a guest.</summary>
    public const int MimicGraceSeconds = 15;
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
    private MimicState MimicSnapshot => new(mimicCapture.Running, mimicCapture.SourceName, mimicCapture.Status, MimicWindowMs, transcriber.Enabled, transcriber.ModelReady, transcriber.Status, hotkey.Active, hotkeyStatus, RecentSnapshot, hotkeySeconds, hotkey.Name);

    /// <summary>Replaces the system-wide hotkey; tests use a fixture that can be "pressed" directly.</summary>
    public void UseHotkey(IGlobalHotkey key) { hotkey.Dispose(); hotkey = key; }
    /// <summary>Replaces how hotkeys are made, so tests can pick other keys without touching Windows.</summary>
    public void UseHotkeys(Func<string, IGlobalHotkey> factory) { makeHotkey = factory; UseHotkey(factory(HotkeyKeys[0])); }

    /// <summary>One-line Mimic events for the companion console (its only interface on the host PC).</summary>
    public event Action<string>? MimicLog;
    private void Log(string message) => MimicLog?.Invoke(message);

    private void StopMimic() { if (mimicCapture.Running) Log("Hosting ended: stopped listening and discarded the buffer"); mimicCapture.Stop(); transcriber.Disable(); hotkey.Unregister(); hotkeyStatus = "Off"; lock (recentClips) recentClips.Clear(); lock (peekGate) lastPeek = null; }

    /// <summary>The host pressed the hotkey: save the last few seconds (3–30, host's choice), like Voice Replay Studio's F6.</summary>
    public async Task HotkeyPressed()
    {
        try
        {
            var ring = mimicCapture.Ring ?? throw new InvalidDataException($"{hotkey.Name} was pressed while Mimic was not listening.");
            int seconds = hotkeySeconds;
            long stop = ring.Written, start = Math.Max(0, stop - (long)ring.Rate * seconds);
            var words = string.Join(" ", transcriber.Lines(ring, start, stop).Select(l => l.Text.Trim())).Trim();
            var reply = await SaveMimic(ring, start, stop, words.Length > 0 ? words : null, "Host", true, "host", audioAudience);
            hotkeyStatus = reply.Ok ? $"Saved the last {seconds} s at {DateTime.Now:HH:mm:ss}" : reply.Error ?? "Could not save.";
            if (!reply.Ok) Log($"{hotkey.Name}: {hotkeyStatus}");
        }
        catch (InvalidDataException ex) { hotkeyStatus = ex.Message; Log($"{hotkey.Name}: {ex.Message}"); }
        Publish(); CommandAccepted?.Invoke();
    }

    private async Task<Reply> SaveMimic(MimicRing ring, long start, long stop, string? name, string by, bool host, string owner, string? audience)
    {
        if (stop - start < (long)ring.Rate * MimicMinimumMs / 1000) throw new InvalidDataException("Mimic had not started listening yet at that moment.");
        var samples = ring.Read(start, stop);
        if (samples.All(s => Math.Abs(s) < 0.0005f)) throw new InvalidDataException("That selection is silent. Nothing was saved.");
        name = string.IsNullOrWhiteSpace(name) ? "Mimic " + DateTime.Now.ToString("HH.mm.ss") : name.Trim();
        if (name.Length > 100) name = name[..99].TrimEnd() + "…";
        var reply = await Upload(MimicRing.Wav(samples, ring.Rate), name + ".wav", by, host, owner, audience, mimic: true);
        if (reply.Ok && reply.Data?.GetType().GetProperty("assetId")?.GetValue(reply.Data) is string assetId)
        {
            lock (recentClips)
            {
                recentClips.RemoveAll(c => c.AssetId == assetId);
                recentClips.Insert(0, new(assetId, name, by, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Log($"{by} mimicked \"{name}\" ({samples.Length / (double)ring.Rate:0.0} s), now in Temp");
                if (recentClips.Count > RecentClips) recentClips.RemoveRange(RecentClips, recentClips.Count - RecentClips);
            }
            Publish();
        }
        return reply;
    }

    /// <summary>Replaces the speech engine; tests use a deterministic fixture instead of Whisper.</summary>
    public void UseTranscriptEngine(ITranscriptEngine engine) { transcriber.Dispose(); transcriber = new(engine); }

    /// <summary>Replaces the capture backend; tests and the e2e harness use a deterministic fixture source.</summary>
    public void UseMimicCapture(IMimicCapture capture) { mimicCapture.Dispose(); mimicCapture = capture; }

    private readonly object peekGate = new();
    private (MimicRing Ring, long End, int[] Levels)? lastPeek;

    /// <summary>
    /// Waveform levels for the window ending now, computed at most every 100 ms and shared by every viewer.
    /// One computation scans the whole 30 s window (~3 ms) under the buffer lock; twelve guests polling
    /// at the session rate limit would otherwise cost ~40% of a core and stall capture writes.
    /// </summary>
    private (long End, int[] Levels) PeekLevels(MimicRing ring, long frames)
    {
        lock (peekGate)
        {
            long now = ring.Written;
            if (lastPeek is { } cached && ReferenceEquals(cached.Ring, ring) && now - cached.End < ring.Rate / 10)
                return (cached.End, cached.Levels);
            var levels = ring.Levels(now, frames, MimicBuckets);
            lastPeek = (ring, now, levels);
            return (now, levels);
        }
    }

    /// <summary>The buffer frames a person selected in a peek they saw (see docs/MIMIC.md, Protocol).</summary>
    private (MimicRing Ring, long Start, long Stop) Selection(Command c)
    {
        var ring = mimicCapture.Ring ?? throw new InvalidDataException("Mimic is not listening.");
        if (c.SourceId != null && c.SourceId != ring.Id) throw new InvalidDataException("Mimic restarted since you selected that. Select it again.");
        if (c.MimicEnd is not long end || c.PositionMs is not long from || c.EndMs is not long to || from < 0 || to > MimicWindowMs || to - from < MimicMinimumMs) throw new InvalidDataException($"Select between {MimicMinimumMs} ms and {MimicWindowMs / 1000} seconds of audio.");
        long windowStart = end - (long)ring.Rate * MimicWindowMs / 1000;
        // Time before capture started has no audio; trim it instead of treating it as expired.
        return (ring, Math.Max(0, windowStart + from * ring.Rate / 1000), windowStart + to * ring.Rate / 1000);
    }

    private static bool IsMimic(string op) => op is "mimicSources" or "mimicStart" or "mimicStop" or "mimicPeek" or "mimicClip" or "mimicTranscribe" or "mimicHotkey" or "mimicPreview";

    private bool GuestMimicAllowed(string? requester, string? audience) =>
        disk.Permissions.GuestMimic && (audioAudience == null || sessionAccepting && audience == audioAudience) && (requester == null || !revokedAudioPeers.ContainsKey(requester));

    private async Task<Reply> ExecuteMimic(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && c.Op is not ("mimicPeek" or "mimicClip")) throw new InvalidDataException("Only the host can choose what Mimic listens to.");
            if (!host && !GuestMimicAllowed(requester, audience)) throw new InvalidDataException("The host has not enabled Mimic for guests.");
            switch (c.Op)
            {
                case "mimicSources":
                    return new("ack", c.Id, true, Data: new { sources = mimicCapture.Sources() });
                case "mimicStart":
                    var source = mimicCapture.Sources().FirstOrDefault(s => s.Id == c.SourceId) ?? throw new InvalidDataException("That source is no longer available. Refresh the list.");
                    await mimicCapture.Start(source, MimicWindowMs / 1000 + MimicGraceSeconds);
                    Log($"Listening to {source.Name} (last 30 s kept in memory only)");
                    Publish(); CommandAccepted?.Invoke();
                    return new("ack", c.Id, true);
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
                            if (hotkey.Active)
                            {
                                try { next.Register(() => _ = HotkeyPressed()); }
                                catch { next.Dispose(); throw; }
                                hotkeyStatus = $"Press {key} in any app to save the last {hotkeySeconds} s";
                            }
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
                    mimicCapture.Stop(); lock (peekGate) lastPeek = null; Log("Stopped listening; the buffer was discarded"); Publish();
                    return new("ack", c.Id, true);
                case "mimicTranscribe":
                    if (c.Action == "disable") { transcriber.Disable(); Log("Transcripts off"); }
                    else if (c.Action == "enable") { await transcriber.Enable(() => mimicCapture.Ring, false); Log("Transcripts on (speech is recognized on this PC)"); }
                    else if (c.Action == "download") { if (transcriber.ModelReady) await transcriber.Enable(() => mimicCapture.Ring, false); else transcriber.BeginDownload(() => mimicCapture.Ring, Publish); }
                    else throw new InvalidDataException("Unsupported transcript setting.");
                    Publish();
                    return new("ack", c.Id, true);
                case "mimicPeek":
                    {
                        var ring = mimicCapture.Ring ?? throw new InvalidDataException("Mimic is not listening.");
                        long frames = (long)ring.Rate * MimicWindowMs / 1000;
                        var (end, levels) = PeekLevels(ring, frames);
                        return new("ack", c.Id, true, Data: new MimicPeek(end, ring.Rate, MimicWindowMs, levels, mimicCapture.Running, transcriber.Lines(ring, end - frames, end), ring.Id));
                    }
                case "mimicClip":
                    {
                        var (ring, start, stop) = Selection(c);
                        var result = await SaveMimic(ring, start, stop, c.Name, Person(actor, participant).Name, host, requester ?? participant?.Id ?? actor, audience);
                        return result with { Id = c.Id };
                    }
                case "mimicPreview":
                    {
                        // Host only (checked above): the audio goes to the host's own browser tab over loopback, to be
                        // heard on that PC. It never reaches guests or Soundpad.
                        var (ring, start, stop) = Selection(c);
                        if (stop - start < (long)ring.Rate * MimicMinimumMs / 1000) throw new InvalidDataException("Mimic had not started listening yet at that moment.");
                        return new("ack", c.Id, true, Data: new { wav = Convert.ToBase64String(MimicRing.Wav(ring.Read(start, stop), ring.Rate)) });
                    }
                default: throw new InvalidDataException("Unsupported command.");
            }
        }
        catch (InvalidDataException ex) { Publish(); return new("ack", c.Id, false, ex.Message); }
    }
}
