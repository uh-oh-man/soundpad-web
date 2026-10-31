using NAudio.Wave;
namespace SoundpadWeb;

public sealed partial class Bridge
{
    public const int TrimMinimumMs = 100;
    // Temp's per-sound limit. It is also recorded in the audio journal, so raising it would make older
    // companions reject that journal and break rolling back; long copies are reduced to fit instead.
    private const int UploadLimit = 16777216, MinimumCopyRate = 16000;

    /// <summary>Keeps a copy within Temp's 16 MiB: mono first, then a lower sample rate (never below 16 kHz).</summary>
    public static (float[] Samples, int Rate, int Channels) FitForTemp(float[] interleaved, int rate, int channels)
    {
        long Bytes(long samples) => samples * 2 + 44;
        if (Bytes(interleaved.Length) <= UploadLimit) return (interleaved, rate, channels);
        var mono = interleaved;
        if (channels > 1)
        {
            mono = new float[interleaved.Length / channels];
            for (int i = 0; i < mono.Length; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
                mono[i] = sum / channels;
            }
            if (Bytes(mono.Length) <= UploadLimit) return (mono, rate, 1);
        }
        int target = (int)Math.Max(MinimumCopyRate, Math.Floor((UploadLimit - 44) / 2.0 / mono.Length * rate));
        var resampled = MimicTranscriber.Resample(mono, rate, target);
        if (Bytes(resampled.Length) > UploadLimit) throw new InvalidDataException("That copy is too long for Temp.");
        return (resampled, target, 1);
    }
    private int editing;

    /// <summary>
    /// Saves part of a Temp source, optionally with a voice effect (<c>action</c>), as a new Temp sound, as
    /// Voice Replay Studio's trim saved a new version: the original stays untouched. Guests need the same
    /// permission as uploading.
    /// </summary>
    private async Task<Reply> TrimAudio(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            if (c.AssetId == null || c.PositionMs is not long from || c.EndMs is not long to || from < 0 || to - from < TrimMinimumMs)
                throw new InvalidDataException($"Choose a start and end at least {TrimMinimumMs / 1000.0:0.0} s apart.");
            // Check the guest before any decoding: this work is heavy and must not be free to trigger.
            var owner = requester ?? participant?.Id ?? actor;
            if (!sessionAccepting) throw new InvalidDataException("Hosting has ended.");
            if (actor != "host" && (!disk.Permissions.GuestUploads || (audioAudience != null && audience != audioAudience) || revokedAudioPeers.ContainsKey(owner)))
                throw new InvalidDataException("The host has not allowed guests to edit audio.");
            if (Interlocked.Increment(ref editing) > 1) { Interlocked.Decrement(ref editing); throw new InvalidDataException("Another copy is being made. Try again in a moment."); }
            try
            {
            string? path; string original;
            await audioGate.WaitAsync();
            try
            {
                var asset = Audio.AssetById(c.AssetId);
                original = asset.Name;
                path = asset.Variants.FirstOrDefault(v => v.Orientation == "normal" && v.Current && !v.Retired && File.Exists(v.Path))?.Path ?? asset.SourcePath;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { throw new InvalidDataException("Unknown source."); }
            finally { audioGate.Release(); }
            if (path == null || !File.Exists(path)) throw new InvalidDataException("The original audio for that source is no longer available.");
            var effect = c.Action ?? "none";
            if (!AudioEffects.Names.ContainsKey(effect)) throw new InvalidDataException("Unknown effect.");
            float[] cut; int rate, channels;
            using (var audio = new DecodedAudio(path))
            {
                if (from >= audio.Duration.TotalMilliseconds) throw new InvalidDataException("The start is past the end of the sound.");
                rate = audio.Rate; channels = audio.Channels;
                long skip = from * rate / 1000 * channels, keep = (Math.Min(to, (long)audio.Duration.TotalMilliseconds) - from) * rate / 1000 * channels;
                // Even mono at the lowest quality must fit Temp's 16 MiB per-sound limit; say so before decoding.
                double seconds = keep / (double)channels / rate * AudioEffects.Stretch(effect) + (effect == "echo" ? 0.5 : 0);
                if (seconds * MinimumCopyRate * 2 + 44 > UploadLimit)
                    throw new InvalidDataException($"That copy is too long for Temp. Choose at most {Math.Floor((UploadLimit - 44) / (2.0 * MinimumCopyRate) / AudioEffects.Stretch(effect))} s of this sound.");
                var samples = new List<float>((int)keep);
                var buffer = new float[channels * 4096];
                int read;
                while (keep > 0 && (read = audio.Read(buffer)) > 0)
                {
                    int offset = (int)Math.Min(skip, read);
                    skip -= offset;
                    int count = (int)Math.Min(read - offset, keep);
                    samples.AddRange(buffer.AsSpan(offset, count));
                    keep -= count;
                }
                cut = samples.ToArray();
            }
            var processed = AudioEffects.Apply(cut, channels, rate, effect);
            (processed, rate, channels) = FitForTemp(processed, rate, channels);
            byte[] wav;
            using (var output = new MemoryStream())
            {
                using (var writer = new WaveFileWriter(output, new WaveFormat(rate, 16, channels)))
                    foreach (var sample in processed) writer.WriteSample(Math.Clamp(sample, -1f, 1f));
                wav = output.ToArray();
            }
            var name = string.IsNullOrWhiteSpace(c.Name) ? $"{original} ({(effect == "none" ? "trimmed" : AudioEffects.Names[effect])})" : c.Name.Trim();
            if (name.Length > 100) name = name[..99].TrimEnd() + "\u2026";
            return (await Upload(wav, name + ".wav", Person(actor, participant).Name, actor == "host", owner, audience)) with { Id = c.Id };
            }
            finally { Interlocked.Decrement(ref editing); }
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
    }
}

/// <summary>Voice effects for edited copies. Pure functions over interleaved samples so they are easy to test.</summary>
public static class AudioEffects
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["none"] = "trimmed", ["chipmunk"] = "chipmunk", ["slowmo"] = "slow-mo", ["echo"] = "echo",
    };
    /// <summary>How much longer the effect makes the audio (Slow-mo plays at 0.7x).</summary>
    public static double Stretch(string effect) => effect switch { "slowmo" => 1 / 0.7, "chipmunk" => 1 / 1.5, _ => 1 };
    public static float[] Apply(float[] interleaved, int channels, int rate, string effect) => effect switch
    {
        "chipmunk" => Speed(interleaved, channels, 1.5),
        "slowmo" => Speed(interleaved, channels, 0.7),
        "echo" => Echo(interleaved, channels, rate),
        _ => interleaved,
    };
    /// <summary>Plays faster or slower at the same sample rate, so pitch moves with speed (the classic effect).</summary>
    public static float[] Speed(float[] input, int channels, double factor)
    {
        int frames = input.Length / channels, outFrames = (int)(frames / factor);
        var output = new float[outFrames * channels];
        for (int i = 0; i < outFrames; i++)
        {
            double position = i * factor;
            int index = (int)position; double t = position - index;
            for (int c = 0; c < channels; c++)
            {
                float a = input[Math.Min(index, frames - 1) * channels + c], b = input[Math.Min(index + 1, frames - 1) * channels + c];
                output[i * channels + c] = (float)(a + (b - a) * t);
            }
        }
        return output;
    }
    /// <summary>Two taps (250 ms at 45 %, 500 ms at 20 %) with the tail kept so the last word echoes too.</summary>
    public static float[] Echo(float[] input, int channels, int rate)
    {
        int first = rate / 4 * channels, second = rate / 2 * channels;
        var output = new float[input.Length + second];
        for (int i = 0; i < input.Length; i++)
        {
            output[i] += input[i] * 0.8f;
            output[i + first] += input[i] * 0.45f * 0.8f;
            output[i + second] += input[i] * 0.2f * 0.8f;
        }
        return output;
    }
}
