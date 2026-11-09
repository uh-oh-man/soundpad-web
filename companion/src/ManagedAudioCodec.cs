using NAudio.Wave;
namespace SoundpadWeb;

internal static class ManagedAudioCodec
{
    private const string TooLong = "This sound is too long to reverse: over 512 MiB once decoded (about 46 minutes of stereo audio).";
    /// <summary>
    /// Largest file the audio journal accepts for one variant (every companion version checks it, so it can't
    /// grow without breaking rollback): about 46 minutes of 48 kHz stereo. Since 2.16 this is reverse's only size
    /// limit; the old ten-minute / 64 MiB caps are gone.
    /// </summary>
    public const long MaxVariantBytes = 536870912;
    public static void Convert(string input, string output, bool reverse, long byteLimit, CancellationToken cancel = default)
    {
        ManagedFiles.LocalSource(input);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        long limit = Math.Min(MaxVariantBytes, byteLimit) - 128;
        if (limit < 4) throw new InvalidDataException("Temporary storage has insufficient space for another variant.");
        byte[] pcm; int channels, rate;
        WaveFileReader? exact = null;
        try
        {
            if (Path.GetExtension(input).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var wave = new WaveFileReader(input);
                if (wave.WaveFormat.Encoding == WaveFormatEncoding.Pcm && wave.WaveFormat.BitsPerSample == 16) exact = wave; else wave.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { exact?.Dispose(); exact = null; }
        if (exact != null)
        {
            using (exact)
            {
                channels = exact.WaveFormat.Channels; rate = exact.WaveFormat.SampleRate;
                if (exact.Length > limit || exact.Length == 0 || channels is < 1 or > 8 || rate is < 8000 or > 384000) throw new InvalidDataException(exact.Length > limit ? TooLong : "Audio must have one to eight channels at 8 to 384 kHz.");
                pcm = new byte[checked((int)exact.Length)]; int offset = 0, count;
                while (offset < pcm.Length && (count = exact.Read(pcm, offset, pcm.Length - offset)) > 0) { deadline.Token.ThrowIfCancellationRequested(); offset += count; }
                if (offset != pcm.Length) throw new InvalidDataException("Audio ended before its declared length.");
            }
        }
        else
        {
            using var decoder = new DecodedAudio(input); channels = decoder.Channels; rate = decoder.Rate;
            if (channels > 8) throw new InvalidDataException("Session audio supports up to eight channels.");
            using var data = new MemoryStream(); var buffer = new float[4096 * channels];
            using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true))
            {
                int count; while ((count = decoder.Read(buffer)) > 0)
                {
                    deadline.Token.ThrowIfCancellationRequested(); if (data.Length + count * 2L > limit) throw new InvalidDataException(TooLong);
                    for (int i = 0; i < count; i++) writer.Write((short)(Math.Clamp(float.IsFinite(buffer[i]) ? buffer[i] : 0, -1, 1) * 32767));
                }
            }
            pcm = data.ToArray();
        }
        int frame = channels * 2;
        if (pcm.Length == 0 || pcm.Length % frame != 0) throw new InvalidDataException("Empty or incomplete audio frames.");
        if (reverse) for (int a = 0, b = pcm.Length - frame; a < b; a += frame, b -= frame) for (int c = 0; c < frame; c++) (pcm[a + c], pcm[b + c]) = (pcm[b + c], pcm[a + c]);
        deadline.Token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var wave = new WaveFileWriter(file, new WaveFormat(rate, 16, channels))) wave.Write(pcm, 0, pcm.Length);
        using var durable = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None); durable.Flush(true);
    }
}
