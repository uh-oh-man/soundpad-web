using NAudio.Wave;
namespace SoundpadWeb;

public sealed partial class Bridge
{
    public const int TrimMinimumMs = 100;

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
            if (to - from > 10 * 60 * 1000) throw new InvalidDataException("Edit at most ten minutes at a time.");
            float[] cut; int rate, channels;
            using (var audio = new DecodedAudio(path))
            {
                if (from >= audio.Duration.TotalMilliseconds) throw new InvalidDataException("The start is past the end of the sound.");
                rate = audio.Rate; channels = audio.Channels;
                long skip = from * rate / 1000 * channels, keep = (Math.Min(to, (long)audio.Duration.TotalMilliseconds) - from) * rate / 1000 * channels;
                var samples = new List<float>((int)Math.Min(keep, int.MaxValue / 2));
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
            byte[] wav;
            using (var output = new MemoryStream())
            {
                using (var writer = new WaveFileWriter(output, new WaveFormat(rate, 16, channels)))
                    foreach (var sample in processed) writer.WriteSample(Math.Clamp(sample, -1f, 1f));
                wav = output.ToArray();
            }
            var name = string.IsNullOrWhiteSpace(c.Name) ? $"{original} ({(effect == "none" ? "trimmed" : AudioEffects.Names[effect])})" : c.Name.Trim();
            if (name.Length > 100) name = name[..99].TrimEnd() + "\u2026";
            return (await Upload(wav, name + ".wav", Person(actor, participant).Name, actor == "host", requester ?? participant?.Id ?? actor, audience)) with { Id = c.Id };
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
