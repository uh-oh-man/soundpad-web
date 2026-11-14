using System.Buffers.Binary;
using NAudio.Wave;
namespace SoundpadWeb;

/// <summary>Where the audio sits in a WAV file's bytes, as read by <see cref="SliceWav.TryRead"/>.</summary>
public readonly record struct SliceWavInfo(int SampleRate, int Channels, int BitsPerSample, int DataOffset, int DataBytes)
{
    public long Frames => Channels > 0 && BitsPerSample >= 8 ? DataBytes / (Channels * (BitsPerSample / 8)) : 0;
    public double DurationMs => SampleRate > 0 ? Frames * 1000.0 / SampleRate : 0;
}

/// <summary>
/// 16-bit PCM WAV bytes for slices and slot files. Written with NAudio's <see cref="WaveFileWriter"/>, like every
/// other WAV the companion makes, so Soundpad and <see cref="DecodedAudio"/> read them the same way.
/// </summary>
public static class SliceWav
{
    /// <summary>
    /// Float to 16-bit, the exact inverse of how a 16-bit WAV is decoded (sample / 32768), so an unchanged slice of
    /// a 16-bit source comes out bit for bit. Out-of-range values clamp; NaN and infinities become silence.
    /// </summary>
    public static short ToPcm16(float sample) => float.IsFinite(sample) ? (short)Math.Clamp(MathF.Round(sample * 32768f), -32768f, 32767f) : (short)0;

    /// <summary>Interleaved float samples as a 16-bit PCM WAV file.</summary>
    public static byte[] Encode(ReadOnlySpan<float> interleaved, int rate, int channels) => Encode(interleaved, rate, channels, default);

    /// <summary><see cref="Encode(ReadOnlySpan{float}, int, int)"/> that stops when cancelled (a slice nobody waits for any more, 2.37).</summary>
    public static byte[] Encode(ReadOnlySpan<float> interleaved, int rate, int channels, CancellationToken cancel)
    {
        Check(interleaved.Length, rate, channels);
        cancel.ThrowIfCancellationRequested();
        var pcm = new byte[interleaved.Length * 2];
        for (int i = 0; i < interleaved.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), ToPcm16(interleaved[i]));
        }
        cancel.ThrowIfCancellationRequested();
        return Wrap(pcm, rate, channels);
    }

    /// <summary>Pure digital silence (every sample zero) of exactly <paramref name="frames"/> frames.</summary>
    public static byte[] Silence(long frames, int rate, int channels)
    {
        if (frames < 1) throw new InvalidDataException("Empty or incomplete audio frames.");
        Check(frames * channels, rate, channels);
        return Wrap(new byte[frames * channels * 2], rate, channels);
    }

    /// <summary>
    /// Finds the format and the audio bytes of a WAV file held in memory. False for anything that is not a plain
    /// PCM or float WAV with both chunks present.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> wav, out SliceWavInfo info)
    {
        info = default;
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        long position = 12; int tag = 0, channels = 0, rate = 0, bits = 0;
        while (position + 8 <= wav.Length)
        {
            var id = wav.Slice((int)position, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice((int)position + 4)), body = position + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || body + 16 > wav.Length) return false;
                var format = wav.Slice((int)body, 16);
                tag = BinaryPrimitives.ReadUInt16LittleEndian(format); channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]); bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (tag is not (1 or 3 or 0xFFFE) || channels < 1 || rate < 1 || bits is not (8 or 16 or 24 or 32)) return false;
                long available = Math.Min(size, wav.Length - body); int frame = channels * (bits / 8);
                info = new(rate, channels, bits, (int)body, (int)(available - available % frame));
                return true;
            }
            position = body + size + (size & 1); // chunks are padded to an even length
        }
        return false;
    }

    /// <summary>The interleaved samples of a 16-bit PCM WAV file held in memory.</summary>
    public static short[] Pcm16(ReadOnlySpan<byte> wav)
    {
        if (!TryRead(wav, out var info) || info.BitsPerSample != 16) throw new InvalidDataException("Not a 16-bit PCM WAV file.");
        var samples = new short[info.DataBytes / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(info.DataOffset + i * 2));
        return samples;
    }

    /// <summary>True only for a 16-bit PCM WAV whose every sample is zero: nothing can be heard when it plays.</summary>
    public static bool IsDigitalSilence(ReadOnlySpan<byte> wav) =>
        TryRead(wav, out var info) && info.BitsPerSample == 16 && info.Frames > 0 && wav.Slice(info.DataOffset, info.DataBytes).IndexOfAnyExcept((byte)0) < 0;

    private static void Check(long samples, int rate, int channels)
    {
        // The limits ManagedAudioCodec applies to the WAVs it writes for Soundpad.
        if (channels is < 1 or > 8 || rate is < 8000 or > 384000) throw new InvalidDataException("Audio must have one to eight channels at 8 to 384 kHz.");
        if (samples == 0 || samples % channels != 0) throw new InvalidDataException("Empty or incomplete audio frames.");
        if (samples * 2 > ManagedAudioCodec.MaxVariantBytes - 128) throw new InvalidDataException("That audio is too long to prepare.");
    }

    private static byte[] Wrap(byte[] pcm, int rate, int channels)
    {
        using var output = new MemoryStream(pcm.Length + 64);
        using (var writer = new WaveFileWriter(output, new WaveFormat(rate, 16, channels))) writer.Write(pcm, 0, pcm.Length);
        return output.ToArray();
    }
}
