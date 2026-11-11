namespace SoundpadWeb;

/// <summary>Something placed on an assembled timeline. <paramref name="OffsetMs"/> is where it starts in the output.</summary>
public abstract record SliceItem(double OffsetMs);

/// <summary>The frames [StartFrame, EndFrame) of a recording, placed at <c>OffsetMs</c>.</summary>
public sealed record SlicePiece(ISliceSource Source, long StartFrame, long EndFrame, double OffsetMs) : SliceItem(OffsetMs)
{
    /// <summary>Manual gain. It multiplies the balancing gain, so it works as a trim on top of Balance volumes.</summary>
    public double Gain { get; init; } = 1;
    public double FadeInMs { get; init; }
    public double FadeOutMs { get; init; }
    public bool Reverse { get; init; }
    /// <summary>Overrides <see cref="SliceAssemblyOptions.Balance"/> for this piece; null follows it.</summary>
    public bool? Balance { get; init; }
    /// <summary>A loudness already known for this range (dBFS, from <see cref="SliceLoudness.EstimateDb"/>), to skip measuring it again.</summary>
    public double? LoudnessDb { get; init; }
}

/// <summary>Silence. It takes up time, and like any later-starting item it cuts off a piece still sounding.</summary>
public sealed record SlicePause(double OffsetMs, double DurationMs) : SliceItem(OffsetMs);

/// <summary>A censor tone for a missing word: a sine with short ramps so it does not click.</summary>
public sealed record SliceBleep(double OffsetMs, double DurationMs) : SliceItem(OffsetMs)
{
    public double FrequencyHz { get; init; } = 1000;
    /// <summary>Peak amplitude, 0 to 1 of full scale. The default, 0.2, sits near balanced speech (about -17 dBFS RMS).</summary>
    public double Level { get; init; } = 0.2;
    public double RampMs { get; init; } = 5;
}

public sealed record SliceAssemblyOptions
{
    /// <summary>Output rate. Null uses the highest rate among the pieces (48000 when there are none).</summary>
    public int? Rate { get; init; }
    /// <summary>Output channels. Null uses the most channels among the pieces (mono when there are none).</summary>
    public int? Channels { get; init; }
    /// <summary>Length of the crossfade where a later-starting item cuts an earlier piece off. 0 is a hard cut.</summary>
    public double CrossfadeMs { get; init; } = 4;
    /// <summary>Balance volumes: bring each piece's voiced loudness to <see cref="TargetDb"/>.</summary>
    public bool Balance { get; init; }
    /// <summary>Loudness balanced pieces aim for, in dBFS RMS over voiced frames.</summary>
    public double TargetDb { get; init; } = -20;
    /// <summary>The most balancing may raise a piece, so a nearly silent one is not turned into loud noise.</summary>
    public double MaxBoostDb { get; init; } = 20;
    /// <summary>The output never exceeds this (1 = full scale); peaks above it are turned down smoothly.</summary>
    public double PeakCeiling { get; init; } = 1;
    /// <summary>How far ahead the limiter looks, which is also how quickly it lets go.</summary>
    public double LimiterMs { get; init; } = 5;
}

/// <summary>Where one item ended up, in output frames. <c>EndFrame</c> is where it stops being the sounding item.</summary>
public sealed record SlicePlacement(int Index, long StartFrame, long EndFrame, bool Cut, double Gain, double? LoudnessDb);

public sealed record SliceAssemblyOutput(SliceAudio Audio, IReadOnlyList<SlicePlacement> Placements, bool Limited);

/// <summary>
/// Renders a sentence timeline (pieces of recordings, pauses and bleeps) into one piece of audio.
///
/// Items are never mixed. When a later-starting item begins while an earlier one is still sounding, the earlier
/// one is cut off there (items that start together: the later one in the list wins). A short crossfade at the cut
/// keeps it from clicking: the earlier piece fades out over the first milliseconds of the later one, which fades
/// in over the same span. Items that merely touch are joined sample for sample with no fade.
/// </summary>
public static class SliceAssembly
{
    public const int MaxItems = 4096;
    private sealed class Placed { public long Start, Length, End; public float[]? Audio; public bool Cut; public double Gain = 1; public double? Loudness; }

    /// <summary>The timeline as a 16-bit PCM WAV.</summary>
    public static byte[] Render(IReadOnlyList<SliceItem> items, SliceAssemblyOptions? options = null) => RenderSamples(items, options).Audio.ToWav();

    /// <summary>The timeline as float samples, with where each item landed (index order matches <paramref name="items"/>).</summary>
    public static SliceAssemblyOutput RenderSamples(IReadOnlyList<SliceItem> items, SliceAssemblyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(items); options ??= new();
        if (items.Count is 0 or > MaxItems) throw new InvalidDataException(items.Count == 0 ? "There is nothing to render." : "That sentence has too many pieces.");
        if (items.Any(item => item == null)) throw new ArgumentException("A timeline item is missing.", nameof(items));
        var pieces = items.OfType<SlicePiece>().ToArray();
        if (pieces.Any(piece => piece.Source == null)) throw new ArgumentException("A piece has no source.", nameof(items));
        int rate = options.Rate ?? (pieces.Length > 0 ? pieces.Max(p => p.Source.SampleRate) : 48000);
        int channels = options.Channels ?? (pieces.Length > 0 ? pieces.Max(p => p.Source.Channels) : 1);
        if (channels is < 1 or > 8 || rate is < 8000 or > 384000) throw new InvalidDataException("Audio must have one to eight channels at 8 to 384 kHz.");
        if (!Valid(options.CrossfadeMs) || !Valid(options.LimiterMs) || !double.IsFinite(options.TargetDb) || !Valid(options.MaxBoostDb) || !(options.PeakCeiling > 0))
            throw new InvalidDataException("Invalid assembly settings.");
        long limit = (ManagedAudioCodec.MaxVariantBytes - 128) / 2 / channels; // frames one WAV may hold

        var placed = new Placed[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!Valid(item.OffsetMs)) throw new InvalidDataException("A piece has an invalid position.");
            var place = placed[i] = new Placed { Start = SliceRenderer.FrameOf(item.OffsetMs, rate) };
            switch (item)
            {
                case SlicePiece piece:
                    if (!double.IsFinite(piece.Gain)) throw new InvalidDataException("Invalid gain or fade.");
                    place.Gain = piece.Gain;
                    if (piece.Balance ?? options.Balance)
                    {
                        place.Loudness = piece.LoudnessDb ?? SliceLoudness.EstimateDb(piece.Source, piece.StartFrame, piece.EndFrame);
                        // Nothing voiced to measure: leave the piece alone rather than amplify its noise floor.
                        if (place.Loudness is double loudness) place.Gain *= Math.Pow(10, Math.Min(options.TargetDb - loudness, options.MaxBoostDb) / 20);
                    }
                    place.Audio = SliceRenderer.RenderSamples(piece.Source, piece.StartFrame, piece.EndFrame, new SliceOptions
                    {
                        Gain = place.Gain, FadeInMs = piece.FadeInMs, FadeOutMs = piece.FadeOutMs, Reverse = piece.Reverse, TargetRate = rate, TargetChannels = channels,
                    }).Samples;
                    place.Length = place.Audio.Length / channels;
                    break;
                case SlicePause pause:
                    if (!Valid(pause.DurationMs)) throw new InvalidDataException("A pause has an invalid length.");
                    place.Length = SliceRenderer.FrameOf(pause.DurationMs, rate);
                    break;
                case SliceBleep bleep:
                    place.Audio = Bleep(bleep, rate, channels, limit); place.Length = place.Audio.Length / channels;
                    break;
                default: throw new ArgumentException("Unknown timeline item.", nameof(items));
            }
            if (place.Start + place.Length > limit) throw new InvalidDataException("That sentence is too long to prepare.");
        }

        // Start order; a stable sort keeps list order for items that start together, so the later one wins.
        var order = Enumerable.Range(0, placed.Length).OrderBy(i => placed[i].Start).ToArray();
        long total = 0;
        for (int k = 0; k < order.Length; k++)
        {
            var place = placed[order[k]]; place.End = place.Start + place.Length;
            // Only the next start can be the first one inside this item: every other later item starts at or after it.
            if (k + 1 < order.Length && placed[order[k + 1]].Start < place.End) { place.End = placed[order[k + 1]].Start; place.Cut = true; }
            total = Math.Max(total, place.End);
        }
        if (total == 0) throw new InvalidDataException("There is nothing to render.");

        var output = new float[total * channels];
        long crossfade = SliceRenderer.FrameOf(options.CrossfadeMs, rate), fadeIn = 0;
        for (int k = 0; k < order.Length; k++)
        {
            var place = placed[order[k]];
            long kept = place.End - place.Start, tail = 0;
            if (place.Cut && kept > 0 && place.Audio != null)
            {
                var next = placed[order[k + 1]];
                // The cut piece carries on under the start of the next item, fading out; never past that item's own end.
                tail = Math.Min(crossfade, Math.Min(next.End - next.Start, place.Length - kept));
            }
            if (place.Audio != null)
            {
                for (long f = 0; f < kept + tail; f++)
                {
                    double g = 1;
                    if (f < fadeIn) g = (f + 0.5) / fadeIn;
                    else if (f >= kept) g = 1 - (f - kept + 0.5) / tail;
                    long from = f * channels, to = (place.Start + f) * channels;
                    if (g == 1) for (int c = 0; c < channels; c++) output[to + c] += place.Audio[from + c];
                    else for (int c = 0; c < channels; c++) output[to + c] += (float)(place.Audio[from + c] * g);
                }
            }
            fadeIn = tail;
        }

        bool limited = Limit(output, channels, rate, options.PeakCeiling, options.LimiterMs);
        var placements = placed.Select((p, i) => new SlicePlacement(i, p.Start, p.End, p.Cut, p.Gain, p.Loudness)).ToArray();
        return new(new SliceAudio(output, rate, channels), placements, limited);
    }

    /// <summary>
    /// Look-ahead peak limiter, applied in place. Audio that stays under <paramref name="ceiling"/> is not touched
    /// at all (returns false). Where a peak would go over, the gain eases down ahead of it and back up after it,
    /// so the peak lands on the ceiling without the flat top of clipping.
    /// </summary>
    public static bool Limit(float[] interleaved, int channels, int rate, double ceiling = 1, double lookaheadMs = 5)
    {
        int frames = interleaved.Length / channels;
        var need = new float[frames]; bool over = false;
        for (int f = 0; f < frames; f++)
        {
            float peak = 0;
            for (int c = 0; c < channels; c++) peak = Math.Max(peak, Math.Abs(interleaved[f * channels + c]));
            need[f] = peak > ceiling ? (float)(ceiling / peak) : 1;
            over |= peak > ceiling;
        }
        if (!over) return false;
        int window = (int)Math.Max(1, SliceRenderer.FrameOf(lookaheadMs, rate));
        // ahead[f] = lowest gain needed in f .. f + window; averaging the last window + 1 of those gives a smooth
        // curve that is never above need[f], because every averaged value already covers frame f.
        var ahead = new float[frames]; var deque = new int[frames]; int head = 0, count = 0, next = 0;
        for (int f = 0; f < frames; f++)
        {
            for (; next < frames && next <= f + window; next++)
            {
                while (count > 0 && need[deque[head + count - 1]] >= need[next]) count--;
                deque[head + count++] = next;
            }
            while (deque[head] < f) { head++; count--; }
            ahead[f] = need[deque[head]];
        }
        double sum = (double)ahead[0] * (window + 1); // before the start, hold the first value
        float top = (float)ceiling;
        for (int f = 0; f < frames; f++)
        {
            if (f > 0) sum += ahead[f] - ahead[Math.Max(0, f - window - 1)];
            float gain = (float)Math.Min(need[f], sum / (window + 1)); // the min only guards rounding in the running sum
            // The clamp only catches the last bit of float rounding, so "never above the ceiling" holds exactly.
            if (gain < 1) for (int c = 0; c < channels; c++) interleaved[f * channels + c] = Math.Clamp(interleaved[f * channels + c] * gain, -top, top);
        }
        return true;
    }

    private static bool Valid(double value) => double.IsFinite(value) && value >= 0;

    private static float[] Bleep(SliceBleep bleep, int rate, int channels, long limit)
    {
        if (!Valid(bleep.DurationMs) || !Valid(bleep.RampMs) || !(bleep.FrequencyHz > 0) || bleep.FrequencyHz >= rate / 2.0 || !(bleep.Level is >= 0 and <= 1))
            throw new InvalidDataException("Invalid bleep settings.");
        long frames = SliceRenderer.FrameOf(bleep.DurationMs, rate);
        if (frames > limit) throw new InvalidDataException("That sentence is too long to prepare.");
        long ramp = Math.Min(SliceRenderer.FrameOf(bleep.RampMs, rate), frames / 2);
        var samples = new float[frames * channels];
        for (long f = 0; f < frames; f++)
        {
            double g = bleep.Level;
            if (f < ramp) g *= (f + 0.5) / ramp;
            if (frames - 1 - f < ramp) g *= (frames - 1 - f + 0.5) / ramp;
            float value = (float)(g * Math.Sin(2 * Math.PI * bleep.FrequencyHz * f / rate));
            for (int c = 0; c < channels; c++) samples[f * channels + c] = value;
        }
        return samples;
    }
}

/// <summary>How loud a piece of speech sounds, for balancing pieces from different recordings.</summary>
public static class SliceLoudness
{
    /// <summary>
    /// RMS level in dBFS over the voiced part of [startFrame, endFrame): 20 ms windows, ignoring those under
    /// -50 dBFS and those more than 10 dB under the average of the rest (pauses and breaths between words would
    /// otherwise make a slow speaker measure quieter than a fast one). Null when nothing is loud enough to count.
    /// Waveform peaks cannot be used for this: they are normalised per file.
    /// </summary>
    public static double? EstimateDb(ISliceSource source, long startFrame, long endFrame)
    {
        (startFrame, endFrame) = SliceRenderer.CheckRange(source, startFrame, endFrame);
        int channels = source.Channels, window = Math.Max(1, source.SampleRate / 50);
        var powers = new List<double>();
        var buffer = new float[window * channels];
        for (long at = startFrame; at < endFrame; at += window)
        {
            int frames = (int)Math.Min(window, endFrame - at), done = 0;
            while (done < frames)
            {
                int read = source.Read(at + done, buffer.AsSpan(done * channels, (frames - done) * channels));
                if (read <= 0) throw new SliceRangeException("That audio is no longer available.", startFrame, endFrame, source.FrameCount);
                done += read;
            }
            // A short last window would weigh a few samples like a full 20 ms; keep it only if it is all there is.
            if (frames < window / 2 && powers.Count > 0) break;
            double sum = 0;
            for (int i = 0; i < frames * channels; i++) if (float.IsFinite(buffer[i])) sum += (double)buffer[i] * buffer[i];
            powers.Add(sum / (frames * channels));
        }
        const double floor = 1e-5; // -50 dBFS
        var audible = powers.Where(p => p >= floor).ToArray();
        if (audible.Length == 0) return null;
        double gate = audible.Average() * 0.1;
        return 10 * Math.Log10(audible.Where(p => p >= gate).Average());
    }
}
