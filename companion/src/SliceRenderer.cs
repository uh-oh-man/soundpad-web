namespace SoundpadWeb;

/// <summary>How a slice is shaped after its exact frames are taken. The defaults change nothing.</summary>
public sealed record SliceOptions
{
    /// <summary>The exact slice: no gain, no fades, the source's own rate and channels.</summary>
    public static readonly SliceOptions Exact = new();
    /// <summary>Linear gain (1 = unchanged). A slice that would exceed full scale is clamped, not limited.</summary>
    public double Gain { get; init; } = 1;
    /// <summary>Anti-click ramp over the first milliseconds INSIDE the range. 0 keeps the first sample as it is.</summary>
    public double FadeInMs { get; init; }
    /// <summary>Anti-click ramp over the last milliseconds INSIDE the range. 0 keeps the last sample as it is.</summary>
    public double FadeOutMs { get; init; }
    /// <summary>Plays the range backwards. Fades still apply to the start and end of what is heard.</summary>
    public bool Reverse { get; init; }
    /// <summary>Resample to this rate (windowed sinc, <see cref="SliceResampler"/>). Null keeps the source rate.</summary>
    public int? TargetRate { get; init; }
    /// <summary>Convert to this many channels (see <see cref="SliceRenderer.Remix"/>). Null keeps the source's.</summary>
    public int? TargetChannels { get; init; }
    /// <summary>
    /// Trim a range that sticks out of the source to what exists instead of refusing it. An empty result still
    /// throws <see cref="SliceRangeException"/>.
    /// </summary>
    public bool ClampToSource { get; init; }
}

/// <summary>
/// Renders an exact range of a source as a 16-bit PCM WAV. Soundpad can only start an entry and then seek, which
/// lets the opening of the sound through; a file that holds nothing but the wanted frames cannot leak anything.
/// </summary>
public static class SliceRenderer
{
    /// <summary>The frame a time falls on: round(ms * rate / 1000), halves away from zero. Every boundary uses this.</summary>
    public static long FrameOf(double ms, int rate)
    {
        if (!double.IsFinite(ms)) throw new ArgumentOutOfRangeException(nameof(ms));
        return (long)Math.Round(ms * rate / 1000.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>The time of a frame boundary in milliseconds.</summary>
    public static double MsOf(long frame, int rate) => frame * 1000.0 / rate;

    /// <summary>
    /// The frames [startFrame, endFrame) of <paramref name="source"/> as a 16-bit PCM WAV: the first sample is
    /// source[startFrame], the last is source[endFrame - 1], and nothing outside the range is read into the file.
    /// Throws <see cref="SliceRangeException"/> when the range is empty or outside the source.
    /// </summary>
    public static byte[] Render(ISliceSource source, long startFrame, long endFrame, SliceOptions? options = null, CancellationToken cancel = default)
    {
        var audio = RenderSamples(source, startFrame, endFrame, options, cancel);
        return SliceWav.Encode(audio.Samples, audio.Rate, audio.Channels, cancel);
    }

    /// <summary><see cref="Render"/> with the boundaries given in milliseconds (converted with <see cref="FrameOf"/>).</summary>
    public static byte[] RenderMs(ISliceSource source, double startMs, double endMs, SliceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Render(source, FrameOf(startMs, source.SampleRate), FrameOf(endMs, source.SampleRate), options);
    }

    /// <summary>
    /// The same slice as float samples, for mixing or a browser preview. Order: take the frames, reverse,
    /// resample, convert channels, then gain and fades (so fade lengths are in output time).
    /// </summary>
    public static SliceAudio RenderSamples(ISliceSource source, long startFrame, long endFrame, SliceOptions? options = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(source); options ??= SliceOptions.Exact;
        if (!double.IsFinite(options.Gain) || !double.IsFinite(options.FadeInMs) || !double.IsFinite(options.FadeOutMs) || options.FadeInMs < 0 || options.FadeOutMs < 0)
            throw new InvalidDataException("Invalid gain or fade.");
        int channels = source.Channels, rate = source.SampleRate;
        (startFrame, endFrame) = CheckRange(source, startFrame, endFrame, options.ClampToSource);
        var samples = Extract(source, startFrame, endFrame, cancel);
        if (options.Reverse) ReverseFrames(samples, channels);
        if (options.TargetRate is int targetRate && targetRate != rate) { samples = SliceResampler.Resample(samples, channels, rate, targetRate); rate = targetRate; }
        if (options.TargetChannels is int targetChannels && targetChannels != channels) { samples = Remix(samples, channels, targetChannels); channels = targetChannels; }
        Shape(samples, channels, options.Gain, FrameOf(options.FadeInMs, rate), FrameOf(options.FadeOutMs, rate));
        return new(samples, rate, channels);
    }

    /// <summary>
    /// Checks a range against its source and returns it (trimmed when <paramref name="clamp"/> is set). Throws
    /// <see cref="SliceRangeException"/> for an empty range or one that reaches outside the source.
    /// </summary>
    public static (long Start, long End) CheckRange(ISliceSource source, long startFrame, long endFrame, bool clamp = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Channels is < 1 or > 16 || source.SampleRate is < 8000 or > 384000) throw new InvalidDataException("Unsupported audio format.");
        long count = source.FrameCount;
        if (clamp) { startFrame = Math.Clamp(startFrame, 0, count); endFrame = Math.Clamp(endFrame, 0, count); }
        if (startFrame >= endFrame) throw new SliceRangeException("That selection is empty.", startFrame, endFrame, count);
        if (startFrame < 0 || endFrame > count) throw new SliceRangeException("That selection reaches outside the recorded audio.", startFrame, endFrame, count);
        if ((endFrame - startFrame) * source.Channels * 2 > ManagedAudioCodec.MaxVariantBytes - 128) throw new InvalidDataException("That selection is too long to prepare.");
        return (startFrame, endFrame);
    }

    /// <summary>
    /// Channel conversion: to mono averages every channel, from mono copies it to each channel; otherwise output
    /// channel c is the average of the source channels s with s % to == c (5.1 to stereo keeps left/right sides),
    /// or source channel c % from when there are fewer.
    /// </summary>
    public static float[] Remix(float[] interleaved, int from, int to)
    {
        if (from is < 1 or > 16 || to is < 1 or > 16 || interleaved.Length % from != 0) throw new InvalidDataException("Unsupported audio format.");
        if (from == to) return interleaved;
        int frames = interleaved.Length / from;
        var output = new float[frames * to];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < to; c++)
            {
                if (from < to) { output[f * to + c] = interleaved[f * from + c % from]; continue; }
                float sum = 0; int count = 0;
                for (int s = c; s < from; s += to) { sum += interleaved[f * from + s]; count++; }
                output[f * to + c] = sum / count;
            }
        }
        return output;
    }

    /// <summary>Reads exactly the frames [startFrame, endFrame); anything unreadable is a range error. Stops when cancelled (2.37).</summary>
    internal static float[] Extract(ISliceSource source, long startFrame, long endFrame, CancellationToken cancel = default)
    {
        int channels = source.Channels;
        cancel.ThrowIfCancellationRequested();
        var samples = new float[(endFrame - startFrame) * channels];
        int done = 0;
        while (done < samples.Length)
        {
            cancel.ThrowIfCancellationRequested();
            // In pieces of about 64K samples, so a cancelled render of a long range stops within a few ms.
            int frames = source.Read(startFrame + done / channels, samples.AsSpan(done, Math.Min(samples.Length - done, Math.Max(channels, 65536 / channels * channels))));
            if (frames <= 0) throw new SliceRangeException("That audio is no longer available.", startFrame, endFrame, source.FrameCount);
            done += frames * channels;
        }
        cancel.ThrowIfCancellationRequested();
        for (int i = 0; i < samples.Length; i++) if (!float.IsFinite(samples[i])) samples[i] = 0;
        return samples;
    }

    internal static void ReverseFrames(float[] samples, int channels)
    {
        for (int a = 0, b = samples.Length - channels; a < b; a += channels, b -= channels)
            for (int c = 0; c < channels; c++) (samples[a + c], samples[b + c]) = (samples[b + c], samples[a + c]);
    }

    /// <summary>
    /// Gain and linear ramps. A ramp of n frames scales frame i by (i + 0.5) / n, so it never mutes a frame
    /// completely and never touches a frame beyond the n-th. Ramps longer than the audio shrink in proportion.
    /// </summary>
    internal static void Shape(float[] samples, int channels, double gain, long fadeIn, long fadeOut)
    {
        long frames = samples.Length / channels;
        if (fadeIn + fadeOut > frames) { fadeIn = frames * fadeIn / (fadeIn + fadeOut); fadeOut = frames - fadeIn; }
        if (gain == 1 && fadeIn == 0 && fadeOut == 0) return;
        for (long f = 0; f < frames; f++)
        {
            double g = gain;
            if (f < fadeIn) g *= (f + 0.5) / fadeIn;
            if (frames - 1 - f < fadeOut) g *= (frames - 1 - f + 0.5) / fadeOut;
            if (g == 1) continue;
            for (int c = 0; c < channels; c++) samples[f * channels + c] = (float)(samples[f * channels + c] * g);
        }
    }
}
