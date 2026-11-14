namespace SoundpadWeb;

/// <summary>
/// Turns a sentence's pieces into audio (2.42) with <see cref="SliceAssembly"/>: recorded ranges of library
/// recordings, pauses, and bleeps for missing words. A later-starting piece cuts off an earlier one still sounding,
/// with a short crossfade. The only sources it reads are the library recordings <c>library</c> resolves by id; the
/// only audio it makes is silence and the censor tone. There is no other path: no speech is ever generated.
/// </summary>
public static class SentenceRender
{
    /// <summary>The longest sentence that is rendered (it goes into one slot file).</summary>
    public const double MaxSentenceMs = 120000;
    /// <summary>One range of one library recording a render read.</summary>
    public sealed record ReadPart(string PieceId, string RecordingId, long StartFrame, long EndFrame, int Rate);
    public sealed record Result(SliceAudio Audio, IReadOnlyList<ReadPart> Parts, IReadOnlyList<SliceItem> Items, IReadOnlyList<string> Pieces);

    /// <summary>A piece whose recording is gone or no longer holds its range: the message names the piece.</summary>
    public sealed class MissingSourceException(string pieceId, string message) : Exception(message)
    {
        public string PieceId { get; } = pieceId;
    }

    public static Result Render(IReadOnlyList<SentencePiece> pieces, SentenceSettings settings, Func<string, ISliceSource> library, bool? cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(pieces); ArgumentNullException.ThrowIfNull(library);
        bool clean = cleanup ?? settings.Cleanup;
        var items = new List<SliceItem>(); var parts = new List<ReadPart>(); var owners = new List<string>();
        foreach (var p in pieces.OrderBy(p => p.OffsetMs).ThenBy(p => p.TokenStart ?? int.MaxValue))
        {
            switch (p.Kind)
            {
                case "pause": items.Add(new SlicePause(p.OffsetMs, p.DurationMs)); owners.Add(p.Id); break;
                case "missing":
                    // An unresolved missing word is never filled with speech: silence (Gap) or the censor tone (Bleep).
                    items.Add(p.Missing == "bleep" ? new SliceBleep(p.OffsetMs, p.DurationMs) : new SlicePause(p.OffsetMs, p.DurationMs)); owners.Add(p.Id);
                    break;
                case "word":
                case "named":
                    items.Add(Piece(p, p.Source!, p.OffsetMs, p.FadeInMs, p.FadeOutMs, clean, library, parts)); owners.Add(p.Id);
                    break;
                case "splice":
                    {
                        double at = p.OffsetMs;
                        for (int i = 0; i < p.Parts!.Count; i++)
                        {
                            var part = p.Parts[i];
                            items.Add(Piece(p, part, at, i == 0 ? p.FadeInMs : 4, i == p.Parts.Count - 1 ? p.FadeOutMs : 4, clean, library, parts)); owners.Add(p.Id);
                            at += (part.EndMs - part.StartMs) / SentenceComposer.SpeedOf(p.Effect);
                        }
                        break;
                    }
                default: throw new InvalidDataException("Unknown sentence piece.");
            }
        }
        if (items.Count == 0) throw new InvalidDataException("There is nothing in this sentence yet.");
        var output = SliceAssembly.RenderSamples(items, new SliceAssemblyOptions { Balance = settings.Balance });
        var audio = output.Audio;
        if (settings.Effect is { IsNone: false } whole) audio = Apply(audio, whole);
        if (audio.DurationMs > MaxSentenceMs) throw new InvalidDataException($"A sentence can be at most {MaxSentenceMs / 1000:0} seconds long.");
        return new(audio, parts, items, owners);
    }

    private static SlicePiece Piece(SentencePiece p, SentenceSource s, double offset, double fadeIn, double fadeOut, bool clean, Func<string, ISliceSource> library, List<ReadPart> parts)
    {
        ISliceSource source;
        try { source = library(s.RecordingId); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            throw new MissingSourceException(p.Id, $"The recording of “{p.Text}” is missing from the voice library.");
        }
        int rate = source.SampleRate;
        long start = SliceRenderer.FrameOf(s.StartMs, rate), end = Math.Min(SliceRenderer.FrameOf(s.EndMs, rate), source.FrameCount);
        if (start >= end) throw new MissingSourceException(p.Id, $"The recording of “{p.Text}” no longer holds that part.");
        parts.Add(new(p.Id, s.RecordingId, start, end, rate));
        var effect = p.Effect;
        bool process = clean || effect != null && (effect.Speed != 1 || effect.Echo);
        if (!process)
            return new SlicePiece(source, start, end, offset) { FadeInMs = fadeIn, FadeOutMs = fadeOut, Reverse = effect?.Reverse == true, Gain = p.Gain ?? 1, Balance = p.Gain != null ? false : null };
        var samples = SliceRenderer.RenderSamples(source, start, end, new SliceOptions { FadeInMs = fadeIn, FadeOutMs = fadeOut, Reverse = effect?.Reverse == true }).Samples;
        int channels = source.Channels;
        if (clean) samples = Cleanup(samples, channels, rate);
        if (effect != null && effect.Speed != 1) samples = AudioEffects.Apply(samples, channels, rate, "speed", effect.Speed, effect.KeepPitch);
        if (effect?.Echo == true) samples = AudioEffects.Echo(samples, channels, rate);
        if (samples.Length < channels) throw new MissingSourceException(p.Id, $"“{p.Text}” is too short to play.");
        var processed = new PcmSliceSource(samples.AsMemory(0, samples.Length / channels * channels), rate, channels);
        return new SlicePiece(processed, 0, processed.FrameCount, offset) { Gain = p.Gain ?? 1, Balance = p.Gain != null ? false : null };
    }

    /// <summary>An effect on the whole rendered sentence.</summary>
    public static SliceAudio Apply(SliceAudio audio, PieceEffect effect)
    {
        var samples = audio.Samples; int channels = audio.Channels;
        if (effect.Reverse)
        {
            long frames = samples.Length / channels; var reversed = new float[samples.Length];
            for (long f = 0; f < frames; f++) Array.Copy(samples, (frames - 1 - f) * channels, reversed, f * channels, channels);
            samples = reversed;
        }
        if (effect.Speed != 1) samples = AudioEffects.Apply(samples, channels, audio.Rate, "speed", effect.Speed, effect.KeepPitch);
        if (effect.Echo) samples = AudioEffects.Echo(samples, channels, audio.Rate);
        return new SliceAudio(samples, audio.Rate, channels);
    }

    /// <summary>
    /// Gentle cleanup of a piece (a copy; the recording is untouched): a soft downward expander that lowers what
    /// lies well under the piece's speech (room noise and hum between syllables, at most 9 dB), and a short
    /// de-click ramp at both edges.
    /// </summary>
    public static float[] Cleanup(float[] interleaved, int channels, int rate)
    {
        var output = (float[])interleaved.Clone();
        int frames = output.Length / channels, window = Math.Max(1, rate / 100);
        if (frames == 0) return output;
        int windows = (frames + window - 1) / window;
        var level = new double[windows];
        for (int w = 0; w < windows; w++)
        {
            double sum = 0; int count = 0;
            for (int f = w * window; f < Math.Min(frames, (w + 1) * window); f++) for (int c = 0; c < channels; c++) { double v = output[f * channels + c]; sum += v * v; count++; }
            level[w] = Math.Sqrt(sum / Math.Max(1, count));
        }
        double peak = level.Max();
        if (peak <= 1e-6) return output;
        double threshold = peak * 0.08; // about 22 dB under the loudest 10 ms of the piece
        double gain = 1, floor = 0.355; // -9 dB at most
        for (int w = 0; w < windows; w++)
        {
            double target = level[w] >= threshold ? 1 : Math.Max(floor, level[w] / threshold);
            for (int f = w * window; f < Math.Min(frames, (w + 1) * window); f++)
            {
                gain += (target - gain) * (target < gain ? 0.002 : 0.02); // slow to close, quick to open
                for (int c = 0; c < channels; c++) output[f * channels + c] = (float)(output[f * channels + c] * gain);
            }
        }
        int ramp = Math.Min(frames / 2, Math.Max(1, rate * 6 / 1000));
        for (int f = 0; f < ramp; f++)
        {
            float g = (f + 0.5f) / ramp;
            for (int c = 0; c < channels; c++) { output[f * channels + c] *= g; output[(frames - 1 - f) * channels + c] *= g; }
        }
        return output;
    }

    /// <summary>The rendered sentence as a mono WAV for a private preview, at most <paramref name="maxMs"/> and resampled for guests.</summary>
    public static byte[] PreviewWav(SliceAudio audio, int? rate, int maxMs)
    {
        int channels = audio.Channels; long frames = Math.Min(audio.Frames, (long)audio.Rate * maxMs / 1000);
        var mono = new float[frames];
        for (long f = 0; f < frames; f++) { float sum = 0; for (int c = 0; c < channels; c++) sum += audio.Samples[f * channels + c]; mono[f] = sum / channels; }
        int outRate = audio.Rate;
        if (rate is int wanted && wanted < audio.Rate) { mono = MimicTranscriber.Resample(mono, audio.Rate, wanted); outRate = wanted; }
        return MimicRing.Wav(mono, outRate);
    }
}
