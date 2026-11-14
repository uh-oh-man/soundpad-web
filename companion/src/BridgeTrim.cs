using NAudio.Wave;
namespace SoundpadWeb;

public sealed partial class Bridge
{
    public const int TrimMinimumMs = 100;
    /// <summary>How much of the finished copy a host preview plays (2.20): enough to judge the cut and effect.</summary>
    public const int TrimPreviewSeconds = 20;
    /// <summary>Longest plain trim named after its own words when transcripts are on (2.24); longer copies keep "(trimmed)".</summary>
    public const int TrimWordsSeconds = 30;
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
    /// Recognised words as a plain-ASCII name (2.29.3): Whisper writes curly quotes and long dashes, which
    /// Soundpad's ANSI code page may not hold; app-made names stay ASCII.
    /// </summary>
    public static string PlainWords(string words) => words
        .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"')
        .Replace('–', '-').Replace('—', '-').Replace("…", "...");

    /// <summary>
    /// Saves part of a Temp source, optionally with a voice effect (<c>action</c>), as a new Temp sound, as
    /// Voice Replay Studio's trim saved a new version: the original stays untouched. Guests need the same
    /// permission as uploading. With <paramref name="preview"/> (op <c>trimPreview</c>, host only, 2.20) it makes
    /// the same copy but returns its first <see cref="TrimPreviewSeconds"/> s as WAV to the host's own browser
    /// tab, like Mimic's Preview, and saves nothing. With <c>autoplay</c> (2.35) the new copy is played when it is
    /// ready, but only if <paramref name="intent"/>, reserved when the command arrived, is still the newest.
    /// </summary>
    private async Task<Reply> TrimAudio(Command c, string actor, Participant? participant, string? requester, string? audience, bool preview = false, Intent? intent = null)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            if (c.AssetId == null || c.PositionMs is not long from || c.EndMs is not long to || from < 0 || to - from < TrimMinimumMs)
                throw new InvalidDataException($"Choose a start and end at least {TrimMinimumMs / 1000.0:0.0} s apart.");
            // Check the guest before any decoding: this work is heavy and must not be free to trigger.
            var owner = requester ?? participant?.Id ?? actor;
            // Preview audio goes only to the host's own tab over loopback, never to guests or Soundpad.
            if (preview && actor != "host") throw new InvalidDataException("Only the host can preview.");
            if (!sessionAccepting) throw new InvalidDataException("Hosting has ended.");
            if (actor != "host" && (!disk.Permissions.GuestUploads || (audioAudience != null && audience != audioAudience) || revokedAudioPeers.ContainsKey(owner)))
                throw new InvalidDataException("The host has not allowed guests to edit audio.");
            if (Interlocked.Increment(ref editing) > 1) { Interlocked.Decrement(ref editing); throw new InvalidDataException("Another copy is being made. Try again in a moment."); }
            try
            {
            string? path; string original;
            await audioGate.EnterAsync(PriorityGate.Lane.Normal);
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
            // "speed" takes any factor in range (2.16): 0.05 is 20 times slower, 20 is 20 times faster.
            double speed = effect == "speed" ? c.Speed is double s && double.IsFinite(s) && s >= AudioEffects.MinSpeed && s <= AudioEffects.MaxSpeed ? s : throw new InvalidDataException($"Choose a speed from {AudioEffects.MinSpeed}x to {AudioEffects.MaxSpeed}x.") : 1;
            float[] cut; int rate, channels;
            using (var audio = new DecodedAudio(path))
            {
                if (from >= audio.Duration.TotalMilliseconds) throw new InvalidDataException("The start is past the end of the sound.");
                rate = audio.Rate; channels = audio.Channels;
                double stretch = AudioEffects.Stretch(effect, speed);
                long end = Math.Min(to, (long)audio.Duration.TotalMilliseconds);
                // A preview needs only the source that makes its first seconds (plus a second for the effect's edges).
                if (preview) end = Math.Min(end, from + (long)Math.Ceiling((TrimPreviewSeconds / stretch + 1) * 1000));
                long skip = from * rate / 1000 * channels, keep = (end - from) * rate / 1000 * channels;
                // Even mono at the lowest quality must fit Temp's 16 MiB per-sound limit; say so before decoding.
                double seconds = keep / (double)channels / rate * stretch + (effect == "echo" ? 0.5 : 0);
                if (!preview && seconds * MinimumCopyRate * 2 + 44 > UploadLimit)
                    throw new InvalidDataException($"That copy is too long for Temp. Choose at most {Math.Floor((UploadLimit - 44) / (2.0 * MinimumCopyRate) / stretch)} s of this sound{(effect == "speed" ? " at this speed, or a faster speed" : "")}.");
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
            bool keepPitch = effect == "speed" && c.KeepPitch;
            var processed = AudioEffects.Apply(cut, channels, rate, effect, speed, keepPitch);
            if (preview)
            {
                // At most 20 s, and at most about 5.5 MB of WAV so the reply stays as small as Mimic's preview
                // (a 96 kHz stereo source previews about 14 s).
                long most = Math.Min((long)TrimPreviewSeconds * rate * channels, 2_750_000 / channels * channels); // whole frames
                if (processed.Length > most) processed = processed[..(int)most];
            }
            else (processed, rate, channels) = FitForTemp(processed, rate, channels);
            byte[] wav;
            using (var output = new MemoryStream())
            {
                using (var writer = new WaveFileWriter(output, new WaveFormat(rate, 16, channels)))
                    foreach (var sample in processed) writer.WriteSample(Math.Clamp(sample, -1f, 1f));
                wav = output.ToArray();
            }
            if (preview) return new("ack", c.Id, true, Data: new { wav = Convert.ToBase64String(wav), seconds = processed.Length / (double)channels / rate });
            var label = effect switch { "none" => "trimmed", "speed" => $"{speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}x speed{(keepPitch ? ", same pitch" : "")}", _ => AudioEffects.Names[effect] };
            string? words = null;
            // A plain trim, unnamed, while transcripts are on: name it after what it says. Effects garble speech
            // for recognition, so they keep their effect name. Recognition is best effort and never blocks the copy.
            if (effect == "none" && string.IsNullOrWhiteSpace(c.Name) && cut.Length <= (long)TrimWordsSeconds * rate * channels)
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    words = await transcriber.Words(cut, rate, channels, limit.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { words = null; }
            }
            var name = !string.IsNullOrWhiteSpace(c.Name) ? c.Name.Trim() : words != null ? $"{PlainWords(words)} ({label})" : $"{original} ({label})";
            // The name becomes a file name first, so a path separator would cut it short ("24/7" -> "7"), as for
            // Voice Replay Studio imports (2.29.3).
            name = name.Replace('/', '-').Replace('\\', '-');
            if (name.Length > 100) name = name[..99].TrimEnd() + "\u2026";
            var saved = (await Upload(wav, name + ".wav", Person(actor, participant).Name, actor == "host", owner, audience, guest: PersonKey(actor, participant, requester))) with { Id = c.Id };
            // Provenance for the voice library (2.41): an edited copy of that source.
            if (saved.Ok && Field(saved, "assetId") is string copyId) NoteProvenance(copyId, new LibrarySource { Kind = "copy", SourceAssetId = c.AssetId });
            return c.Autoplay ? await Autoplay(saved, c, actor, participant, requester, audience, intent) : saved;
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
        ["none"] = "trimmed", ["chipmunk"] = "chipmunk", ["slowmo"] = "slow-mo", ["echo"] = "echo", ["speed"] = "speed",
    };
    /// <summary>Range of the free speed control: 20 times slower to 20 times faster.</summary>
    public const double MinSpeed = 0.05, MaxSpeed = 20;
    /// <summary>How much longer the effect makes the audio (Slow-mo plays at 0.7x; "speed" at the chosen factor).</summary>
    public static double Stretch(string effect, double speed = 1) => effect switch { "slowmo" => 1 / 0.7, "chipmunk" => 1 / 1.5, "speed" => 1 / speed, _ => 1 };
    public static float[] Apply(float[] interleaved, int channels, int rate, string effect, double speed = 1, bool keepPitch = false) => effect switch
    {
        "speed" when keepPitch => TimeStretch(interleaved, channels, rate, speed),
        "chipmunk" => Speed(interleaved, channels, 1.5),
        "slowmo" => Speed(interleaved, channels, 0.7),
        "speed" => Speed(interleaved, channels, speed),
        "echo" => Echo(interleaved, channels, rate),
        _ => interleaved,
    };
    /// <summary>
    /// Plays faster or slower at the same sample rate, so pitch moves with speed (the classic effect). Slower
    /// interpolates between samples; faster averages the samples each output step covers, which keeps high
    /// speeds (up to 20x) from sounding harsh.
    /// </summary>
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
                if (factor > 1)
                {
                    int last = Math.Min((int)(position + factor), frames);
                    float sum = 0; int count = 0;
                    for (int f = index; f < last; f++) { sum += input[f * channels + c]; count++; }
                    output[i * channels + c] = count > 0 ? sum / count : input[Math.Min(index, frames - 1) * channels + c];
                }
                else
                {
                    float a = input[Math.Min(index, frames - 1) * channels + c], b = input[Math.Min(index + 1, frames - 1) * channels + c];
                    output[i * channels + c] = (float)(a + (b - a) * t);
                }
            }
        }
        return output;
    }
    /// <summary>
    /// Changes speed but keeps pitch (2.17), by WSOLA time-stretching: overlapping 40 ms Hann-windowed slices are
    /// laid down at the new pace, each taken from wherever (within ±10 ms) it best continues the previous slice,
    /// so waves line up instead of clicking. Alignment uses a mono mix so stereo channels stay together.
    /// </summary>
    public static float[] TimeStretch(float[] input, int channels, int rate, double speed)
    {
        int frames = input.Length / channels;
        if (frames == 0 || speed == 1) return input;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += input[f * channels + c];
            mono[f] = sum / channels;
        }
        int size = Math.Clamp(rate / 25, 256, 4096) & ~1, hop = size / 2, tolerance = size / 4, overlap = size / 2;
        int outFrames = (int)(frames / speed);
        var output = new float[(outFrames + size) * channels];
        var weight = new float[outFrames + size];
        var window = new float[size];
        for (int i = 0; i < size; i++) window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / size);
        int previous = 0;
        for (int k = 0; (long)k * hop < outFrames; k++)
        {
            int target = (int)Math.Min(frames - 1, Math.Round(k * hop * speed)), best = target;
            if (k > 0)
            {
                // Where the previous slice would naturally carry on; pick the candidate that matches it best.
                int natural = previous + hop; double bestScore = double.NegativeInfinity;
                for (int candidate = Math.Max(0, target - tolerance); candidate <= Math.Min(frames - 1, target + tolerance); candidate += 2)
                {
                    double score = 0;
                    for (int i = 0; i < overlap; i += 4)
                    {
                        if (natural + i >= frames || candidate + i >= frames) break;
                        score += mono[natural + i] * mono[candidate + i];
                    }
                    if (score > bestScore) { bestScore = score; best = candidate; }
                }
            }
            int start = k * hop;
            for (int i = 0; i < size && best + i < frames && start + i < weight.Length; i++)
            {
                float w = window[i]; weight[start + i] += w;
                for (int c = 0; c < channels; c++) output[(start + i) * channels + c] += input[(best + i) * channels + c] * w;
            }
            previous = best;
        }
        var result = new float[outFrames * channels];
        for (int f = 0; f < outFrames; f++)
        {
            float w = weight[f] > 1e-3f ? weight[f] : 1;
            for (int c = 0; c < channels; c++) result[f * channels + c] = output[f * channels + c] / w;
        }
        return result;
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
