using NAudio.Wave;
namespace SoundpadWeb;

internal static class ManagedAudioCodec
{
    public static void Convert(string input, string output, bool reverse, long byteLimit, CancellationToken cancel = default)
    {
        ManagedFiles.LocalSource(input);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        long limit = Math.Min(64L * 1024 * 1024, byteLimit) - 128;
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
                if (exact.TotalTime > TimeSpan.FromMinutes(10) || exact.Length > limit || exact.Length == 0 || channels is < 1 or > 8 || rate is < 8000 or > 384000) throw new InvalidDataException("Audio exceeds the ten-minute, eight-channel or 64 MiB decoded limit.");
                pcm = new byte[checked((int)exact.Length)]; int offset = 0, count;
                while (offset < pcm.Length && (count = exact.Read(pcm, offset, pcm.Length - offset)) > 0) { deadline.Token.ThrowIfCancellationRequested(); offset += count; }
                if (offset != pcm.Length) throw new InvalidDataException("Audio ended before its declared length.");
            }
        }
        else
        {
            using var decoder = new DecodedAudio(input); channels = decoder.Channels; rate = decoder.Rate;
            if (decoder.Duration > TimeSpan.FromMinutes(10) || channels > 8) throw new InvalidDataException("Session audio supports ten minutes and eight channels.");
            using var data = new MemoryStream(); var buffer = new float[4096 * channels];
            using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true))
            {
                int count; while ((count = decoder.Read(buffer)) > 0)
                {
                    deadline.Token.ThrowIfCancellationRequested(); if (data.Length + count * 2L > limit || (data.Length + count * 2L) / (double)(rate * channels * 2) > 600) throw new InvalidDataException("Decoded audio exceeds available temporary storage or the 64 MiB limit.");
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
