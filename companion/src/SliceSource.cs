namespace SoundpadWeb;

/// <summary>
/// Random access to decoded audio for exact slice rendering: interleaved 32-bit float frames at the source's own
/// rate and channel count. <see cref="PcmSliceSource"/> covers a decoded file (from <see cref="PcmCache"/>) and
/// any in-memory PCM; rolling-history audio can implement this over its own ring.
/// </summary>
public interface ISliceSource
{
    int SampleRate { get; }
    int Channels { get; }
    long FrameCount { get; }
    /// <summary>
    /// Copies whole frames starting at <paramref name="startFrame"/> into <paramref name="destination"/> (whose
    /// length must be a multiple of <see cref="Channels"/>) and returns how many frames were copied. Fewer than
    /// asked means the range runs past the end or, for rolling history, that the audio is no longer held; the
    /// renderer reports that as <see cref="SliceRangeException"/> instead of playing something else.
    /// </summary>
    int Read(long startFrame, Span<float> destination);
}

/// <summary>PCM held in memory: a decoded file, or samples the caller already has (a capture, a test tone).</summary>
public sealed class PcmSliceSource : ISliceSource
{
    public PcmSliceSource(ReadOnlyMemory<float> interleaved, int sampleRate, int channels)
    {
        // The same geometry limits as DecodedAudio, so every source the renderer sees obeys them.
        if (channels is < 1 or > 16 || sampleRate is < 8000 or > 384000) throw new InvalidDataException("Unsupported audio format.");
        if (interleaved.Length % channels != 0) throw new InvalidDataException("Empty or incomplete audio frames.");
        Samples = interleaved; SampleRate = sampleRate; Channels = channels;
    }
    /// <summary>The interleaved samples themselves (not a copy).</summary>
    public ReadOnlyMemory<float> Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long FrameCount => Samples.Length / Channels;
    /// <summary>Memory this source keeps alive; what <see cref="PcmCache"/> counts against its budget.</summary>
    public long ByteSize => (long)Samples.Length * sizeof(float);
    public int Read(long startFrame, Span<float> destination)
    {
        if (destination.Length % Channels != 0) throw new ArgumentException("The buffer must hold whole frames.", nameof(destination));
        if (startFrame < 0 || startFrame >= FrameCount) return 0;
        int frames = (int)Math.Min(destination.Length / Channels, FrameCount - startFrame);
        Samples.Span.Slice((int)(startFrame * Channels), frames * Channels).CopyTo(destination);
        return frames;
    }
}

/// <summary>Rendered audio before it becomes a WAV file: interleaved float samples with their rate and channels.</summary>
public sealed record SliceAudio(float[] Samples, int Rate, int Channels)
{
    public long Frames => Samples.Length / Channels;
    public double DurationMs => Frames * 1000.0 / Rate;
    /// <summary>16-bit PCM WAV bytes of this audio (see <see cref="SliceWav.Encode"/>).</summary>
    public byte[] ToWav() => SliceWav.Encode(Samples, Rate, Channels);
}

/// <summary>
/// A slice was asked for outside its source, or the source no longer holds it (rolling history moved on). The
/// message is fit to show. This is its own type and not an <see cref="InvalidDataException"/> (which is sealed),
/// so a command handler that only catches that one must catch this too.
/// </summary>
public sealed class SliceRangeException(string message, long startFrame, long endFrame, long frameCount) : Exception(message)
{
    public long StartFrame { get; } = startFrame;
    public long EndFrame { get; } = endFrame;
    /// <summary>Frames the source had when the range was checked.</summary>
    public long FrameCount { get; } = frameCount;
}
