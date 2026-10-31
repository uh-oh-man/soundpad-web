using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceReplay.Core;

// Mono float samples keep capture independent of endpoint channel layout and disk encoding.
public sealed class RingBuffer
{
    private readonly float[] data;
    private readonly object gate = new();
    private int position, count;
    private long written;
    private const int Bucket = 128;
    private readonly (long Tag, float Min, float Max)[] summaries;
    public Guid Id { get; } = Guid.NewGuid();
    public long Written
    {
        get
        {
            lock (gate)
                return written;
        }
    }
    public long Oldest
    {
        get
        {
            lock (gate)
                return written - count;
        }
    }
    public int Rate
    {
        get;
    }
    public int Count
    {
        get
        {
            lock (gate)
                return count;
        }
    }
    public RingBuffer(int rate, int seconds)
    {
        if (rate <= 0 || seconds < 1 || seconds > 120)
            throw new ArgumentOutOfRangeException();
        Rate = rate;
        data = new float[checked(rate * seconds)];
        summaries = new (long, float, float)[data.Length / Bucket + 2];
        for (int i = 0; i < summaries.Length; i++)
            summaries[i].Tag = -1;
    }
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (gate)
            foreach (var s in samples)
            {
                data[position] = float.IsFinite(s) ? Math.Clamp(s, -1, 1) : 0;
                long tag = written / Bucket;
                int b = (int)(tag % summaries.Length);
                float value = data[position];
                if (summaries[b].Tag != tag)
                    summaries[b] = (tag, value, value);
                else
                {
                    summaries[b].Min = Math.Min(summaries[b].Min, value);
                    summaries[b].Max = Math.Max(summaries[b].Max, value);
                }
                written++;
                position = (position + 1) % data.Length;
                count = Math.Min(count + 1, data.Length);
            }
    }
    public float[] Snapshot(double seconds)
    {
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (gate)
        {
            int n = Math.Min(count, (int)(seconds * Rate));
            if (n == 0)
                throw new InvalidOperationException("No audio buffered. Start monitoring and wait for audio.");
            var result = new float[n];
            int start = (position - n + data.Length) % data.Length;
            int first = Math.Min(n, data.Length - start);
            Array.Copy(data, start, result, 0, first);
            Array.Copy(data, 0, result, first, n - first);
            return result;
        }
    }
    public bool Contains(long first, long last)
    {
        lock (gate)
            return first >= written - count && last <= written && last > first;
    }
    public float[] ReadRange(long first, long last)
    {
        lock (gate)
        {
            if (!Contains(first, last))
                throw new InvalidOperationException("Selection expired from the rolling buffer, or no audio is available. Select a newer region.");
            int n = checked((int)(last - first));
            var result = new float[n];
            int at = (int)(first % data.Length), part = Math.Min(n, data.Length - at);
            Array.Copy(data, at, result, 0, part);
            Array.Copy(data, 0, result, part, n - part);
            return result;
        }
    }
    public LiveFrame View(int seconds, int bins)
    {
        if (seconds <= 0 || seconds > 120)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        bins = Math.Clamp(bins, 1, 4096);
        lock (gate)
        {
            long first = written - (long)seconds * Rate;
            var peaks = new (float Min, float Max)[bins];
            for (int i = 0; i < bins; i++)
            {
                long a = first + (long)i * seconds * Rate / bins, b = first + (long)(i + 1) * seconds * Rate / bins;
                long at = Math.Max(a, written - count);
                float min = at > a ? 0 : 1, max = at > a ? 0 : -1;
                while (at < b)
                {
                    long tag = at / Bucket;
                    int index = (int)(tag % summaries.Length);
                    if (at % Bucket == 0 && at + Bucket <= b && summaries[index].Tag == tag)
                    {
                        min = Math.Min(min, summaries[index].Min);
                        max = Math.Max(max, summaries[index].Max);
                        at += Bucket;
                    }
                    else
                    {
                        float s = data[(int)(at % data.Length)];
                        min = Math.Min(min, s);
                        max = Math.Max(max, s);
                        at++;
                    }
                }
                peaks[i] = min > max ? (0, 0) : (min, max);
            }
            return new(Id, first, written, Rate, seconds, peaks);
        }
    }
}
public sealed record LiveFrame(Guid BufferId, long First, long Last, int Rate, int Seconds, (float Min, float Max)[] Peaks);
public static class AudioFiles
{
    public static float[] Decode(byte[] bytes, int length, WaveFormat format)
    {
        using var raw = new RawSourceWaveStream(bytes, 0, length, format);
        var input = raw.ToSampleProvider();
        var all = new float[length / format.BlockAlign];
        var frame = new float[Math.Max(format.Channels, format.Channels * 1024)];
        int offset = 0, read;
        while ((read = input.Read(frame, 0, frame.Length)) > 0)
            for (int i = 0; i + format.Channels <= read; i += format.Channels)
            {
                float s = 0;
                for (int c = 0; c < format.Channels; c++)
                    s += frame[i + c];
                all[offset++] = s / format.Channels;
            }
        return all[..offset];
    }
    public static void Write(string path, float[] samples, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var writer = new WaveFileWriter(temp, new WaveFormat(rate, 16, 1)))
            {
                writer.WriteSamples(samples, 0, samples.Length);
                writer.Flush();
            }
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static (float[] Samples, int Rate) Read(string path)
    {
        using var reader = new AudioFileReader(path);
        var input = reader.ToMono();
        var result = new List<float>();
        var block = new float[16384];
        int n;
        while ((n = input.Read(block, 0, block.Length)) > 0)
            result.AddRange(block.Take(n));
        return (result.ToArray(), reader.WaveFormat.SampleRate);
    }
    public static float[] Resample(float[] samples, int from, int to)
    {
        if (from == to)
            return samples;
        var provider = new ArraySamples(samples, from);
        var resampler = new WdlResamplingSampleProvider(provider, to);
        var output = new List<float>();
        var block = new float[4096];
        int n;
        while ((n = resampler.Read(block, 0, block.Length)) > 0)
            output.AddRange(block.Take(n));
        return output.ToArray();
    }
    public static float[] Trim(float[] samples, int rate, double start, double end)
    {
        int a = Math.Clamp((int)Math.Round(start * rate), 0, samples.Length), b = Math.Clamp((int)Math.Round(end * rate), 0, samples.Length);
        if (b <= a)
            throw new ArgumentException("Selection must contain audio.");
        return samples[a..b];
    }
    public static (float Min, float Max)[] Peaks(float[] samples, int bins)
    {
        bins = Math.Min(samples.Length, Math.Max(1, bins));
        var output = new (float, float)[bins];
        for (int i = 0; i < bins; i++)
        {
            int a = (int)((long)i * samples.Length / bins), b = (int)((long)(i + 1) * samples.Length / bins);
            float min = 1, max = -1;
            for (int j = a; j < b; j++)
            {
                min = Math.Min(min, samples[j]);
                max = Math.Max(max, samples[j]);
            }
            output[i] = (min, max);
        }
        return output;
    }
    public static bool Silent(float[] samples) => samples.Length == 0 || samples.All(x => Math.Abs(x) < 0.0005f);
}
public sealed class ArraySamples(float[] data, int rate) : ISampleProvider
{
    private int offset;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
    public int Read(float[] buffer, int start, int count)
    {
        int n = Math.Min(count, data.Length - offset);
        // Accept NAudio's WaveBuffer float view as well as ordinary float arrays.
        for (int i = 0; i < n; i++)
            buffer[start + i] = data[offset + i];
        offset += n;
        return n;
    }
}
