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
    private MimicState MimicSnapshot => new(mimicCapture.Running, mimicCapture.SourceName, mimicCapture.Status, MimicWindowMs, transcriber.Enabled, transcriber.ModelReady, transcriber.Status);

    /// <summary>Replaces the speech engine; tests use a deterministic fixture instead of Whisper.</summary>
    public void UseTranscriptEngine(ITranscriptEngine engine) { transcriber.Dispose(); transcriber = new(engine); }

    /// <summary>Replaces the capture backend; tests and the e2e harness use a deterministic fixture source.</summary>
    public void UseMimicCapture(IMimicCapture capture) { mimicCapture.Dispose(); mimicCapture = capture; }

    private static bool IsMimic(string op) => op is "mimicSources" or "mimicStart" or "mimicStop" or "mimicPeek" or "mimicClip" or "mimicTranscribe";

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
                    Publish(); CommandAccepted?.Invoke();
                    return new("ack", c.Id, true);
                case "mimicStop":
                    mimicCapture.Stop(); Publish();
                    return new("ack", c.Id, true);
                case "mimicTranscribe":
                    if (c.Action == "disable") transcriber.Disable();
                    else if (c.Action == "enable") await transcriber.Enable(() => mimicCapture.Ring, false);
                    else if (c.Action == "download") { if (transcriber.ModelReady) await transcriber.Enable(() => mimicCapture.Ring, false); else transcriber.BeginDownload(() => mimicCapture.Ring, Publish); }
                    else throw new InvalidDataException("Unsupported transcript setting.");
                    Publish();
                    return new("ack", c.Id, true);
                case "mimicPeek":
                    {
                        var ring = mimicCapture.Ring ?? throw new InvalidDataException("Mimic is not listening.");
                        long end = ring.Written, frames = (long)ring.Rate * MimicWindowMs / 1000;
                        return new("ack", c.Id, true, Data: new MimicPeek(end, ring.Rate, MimicWindowMs, ring.Levels(end, frames, MimicBuckets), mimicCapture.Running, transcriber.Lines(ring, end - frames, end)));
                    }
                case "mimicClip":
                    {
                        var ring = mimicCapture.Ring ?? throw new InvalidDataException("Mimic is not listening.");
                        if (c.MimicEnd is not long end || c.PositionMs is not long from || c.EndMs is not long to || from < 0 || to > MimicWindowMs || to - from < MimicMinimumMs) throw new InvalidDataException($"Select between {MimicMinimumMs} ms and {MimicWindowMs / 1000} seconds of audio.");
                        long windowStart = end - (long)ring.Rate * MimicWindowMs / 1000;
                        // Time before capture started has no audio; trim it instead of treating it as expired.
                        long start = Math.Max(0, windowStart + from * ring.Rate / 1000), stop = windowStart + to * ring.Rate / 1000;
                        if (stop - start < (long)ring.Rate * MimicMinimumMs / 1000) throw new InvalidDataException("Mimic had not started listening yet at that moment.");
                        var samples = ring.Read(start, stop);
                        if (samples.All(s => Math.Abs(s) < 0.0005f)) throw new InvalidDataException("That selection is silent. Nothing was saved.");
                        var name = string.IsNullOrWhiteSpace(c.Name) ? "Mimic " + DateTime.Now.ToString("HH.mm.ss") : c.Name.Trim();
                        var by = Person(actor, participant).Name;
                        var result = await Upload(MimicRing.Wav(samples, ring.Rate), name[..Math.Min(name.Length, 100)] + ".wav", by, host, requester ?? participant?.Id ?? actor, audience, mimic: true);
                        return result with { Id = c.Id };
                    }
                default: throw new InvalidDataException("Unsupported command.");
            }
        }
        catch (InvalidDataException ex) { Publish(); return new("ack", c.Id, false, ex.Message); }
    }
}
