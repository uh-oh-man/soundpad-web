using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace SoundpadWeb;

// Mimic: a rolling "instant replay" buffer of what an app or output device is playing, so a session
// can turn something that was just said into a sound. Ported from Voice Replay Studio (docs/MIMIC.md).
// Capture never starts on its own, never records microphones, and audio never leaves this PC.

public sealed record MimicSource(string Id, string Name, string Kind);
public sealed record MimicState(bool Active, string? Source, string Status, int WindowMs);
public sealed record MimicPeek(long End, int Rate, int WindowMs, int[] Levels, bool Active);

/// <summary>Mono rolling sample buffer addressed by an absolute frame counter.</summary>
public sealed class MimicRing(int rate, int seconds)
{
    private readonly float[] data = new float[checked(rate * seconds)];
    private readonly object gate = new();
    private long written;
    public int Rate { get; } = rate;
    public long Written { get { lock (gate) return written; } }
    public long Oldest { get { lock (gate) return Math.Max(0, written - data.Length); } }
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (gate)
        {
            if (samples.Length > data.Length) samples = samples[^data.Length..];
            foreach (var s in samples) data[(int)(written++ % data.Length)] = s;
        }
    }
    public void WriteSilence(long frames)
    {
        lock (gate)
        {
            long n = Math.Min(frames, data.Length);
            for (long i = 0; i < n; i++) data[(int)((written + frames - n + i) % data.Length)] = 0;
            written += frames;
        }
    }
    /// <summary>Samples in [start, end). Throws when any part has already been overwritten.</summary>
    public float[] Read(long start, long end)
    {
        lock (gate)
        {
            if (end <= start) throw new InvalidDataException("Choose a longer Mimic selection.");
            if (end > written) throw new InvalidDataException("That selection is newer than the Mimic buffer.");
            if (start < Math.Max(0, written - data.Length)) throw new InvalidDataException("That moment has already scrolled out of Mimic's buffer. Select it again.");
            var result = new float[end - start];
            for (long i = start; i < end; i++) result[i - start] = data[(int)(i % data.Length)];
            return result;
        }
    }
    /// <summary>Peak level (0–1000) per bucket for the window ending at <paramref name="end"/>; unwritten time is 0.</summary>
    public int[] Levels(long end, long frames, int buckets)
    {
        lock (gate)
        {
            var result = new int[buckets];
            long oldest = Math.Max(0, written - data.Length), start = end - frames;
            for (int b = 0; b < buckets; b++)
            {
                long from = Math.Max(oldest, start + frames * b / buckets), to = Math.Min(end, start + frames * (b + 1) / buckets);
                float peak = 0;
                for (long i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(data[(int)(i % data.Length)]));
                result[b] = (int)Math.Round(Math.Clamp(peak, 0, 1) * 1000);
            }
            return result;
        }
    }
    public static byte[] Wav(float[] samples, int rate)
    {
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, new WaveFormat(rate, 16, 1)))
        {
            var pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++) BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue));
            writer.Write(pcm, 0, pcm.Length);
        }
        return output.ToArray();
    }
}

public interface IMimicCapture : IDisposable
{
    IReadOnlyList<MimicSource> Sources();
    Task Start(MimicSource source, int seconds);
    void Stop();
    bool Running { get; }
    string Status { get; }
    string? SourceName { get; }
    /// <summary>The live buffer, kept current with silence when the source is quiet.</summary>
    MimicRing? Ring { get; }
}

/// <summary>Captures an output device (everything it plays) or one app's process tree through WASAPI loopback.</summary>
public sealed class WasapiMimicCapture : IMimicCapture
{
    private const int Rate = 48000;
    private readonly object gate = new();
    private WasapiCapture? device;
    private CancellationTokenSource? processStop;
    private MimicRing? ring;
    private long lastFrameTicks;
    private int generation;
    public bool Running { get; private set; }
    public string Status { get; private set; } = "Off";
    public string? SourceName { get; private set; }
    public MimicRing? Ring { get { lock (gate) { PadSilence(0); return ring; } } }

    public IReadOnlyList<MimicSource> Sources()
    {
        var result = new List<MimicSource>();
        using (var devices = new MMDeviceEnumerator())
            foreach (var d in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (d) result.Add(new("output:" + d.ID, "Output: " + d.FriendlyName, "output"));
        foreach (var p in Process.GetProcesses().OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase))
            using (p)
                try { if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero) result.Add(new("app:" + p.Id, $"App: {p.ProcessName} [{p.Id}]", "app")); }
                catch (InvalidOperationException) { }
        return result;
    }

    public async Task Start(MimicSource source, int seconds)
    {
        Stop();
        int session;
        lock (gate) { session = generation; ring = new(Rate, seconds); lastFrameTicks = Stopwatch.GetTimestamp(); SourceName = source.Name; Status = "Connecting…"; }
        try
        {
            if (source.Kind == "app")
            {
                if (!uint.TryParse(source.Id.AsSpan(4), out uint pid)) throw new InvalidDataException("Unknown app.");
                var stop = processStop = new();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // The activated client is not marshalable across apartments: keep its whole life on one MTA task.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = await AudioClient.ActivateProcessLoopbackAsync(pid, ProcessLoopbackMode.IncludeTargetProcessTree);
                        var format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
                        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 200000, 0, format, Guid.Empty);
                        var reader = client.AudioCaptureClient;
                        client.Start();
                        lock (gate) if (session == generation) { Running = true; Status = "Listening"; }
                        ready.TrySetResult();
                        try
                        {
                            while (!stop.IsCancellationRequested)
                            {
                                while (reader.GetNextPacketSize() > 0)
                                {
                                    var pointer = reader.GetBuffer(out int frames, out var flags);
                                    try
                                    {
                                        var bytes = new byte[frames * format.BlockAlign];
                                        if (!flags.HasFlag(AudioClientBufferFlags.Silent)) System.Runtime.InteropServices.Marshal.Copy(pointer, bytes, 0, bytes.Length);
                                        Feed(session, bytes, bytes.Length, format);
                                    }
                                    finally { reader.ReleaseBuffer(frames); }
                                }
                                await Task.Delay(10, stop.Token);
                            }
                        }
                        finally { client.Stop(); }
                    }
                    catch (OperationCanceledException) { ready.TrySetResult(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Fail(session, "App capture stopped: " + ex.Message);
                        ready.TrySetException(new InvalidDataException("Cannot capture that app. It may have closed, or this Windows build lacks app capture."));
                    }
                });
                await ready.Task;
            }
            else
            {
                using var devices = new MMDeviceEnumerator();
                var capture = new WasapiLoopbackCapture(devices.GetDevice(source.Id["output:".Length..]));
                capture.DataAvailable += (_, e) => Feed(session, e.Buffer, e.BytesRecorded, capture.WaveFormat);
                capture.RecordingStopped += (_, e) => { if (e.Exception != null) Fail(session, "Output capture stopped: " + e.Exception.Message); };
                lock (gate) device = capture;
                capture.StartRecording();
                lock (gate) if (session == generation) { Running = true; Status = "Listening"; }
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or InvalidDataException))
        {
            Stop();
            throw new InvalidDataException("Cannot capture that source: " + ex.Message);
        }
        catch (InvalidDataException) { Stop(); throw; }
    }

    private void Feed(int session, byte[] bytes, int length, WaveFormat format)
    {
        if (length < format.BlockAlign) return;
        using var raw = new RawSourceWaveStream(bytes, 0, length - length % format.BlockAlign, format);
        var input = raw.ToSampleProvider();
        int channels = format.Channels;
        var frame = new float[channels * 1024];
        var mono = new List<float>(length / format.BlockAlign);
        int read;
        while ((read = input.Read(frame)) > 0)
            for (int i = 0; i + channels <= read; i += channels)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += frame[i + c];
                mono.Add(sum / channels);
            }
        lock (gate)
        {
            if (session != generation || ring == null) return;
            // Resample by nearest frame when a device runs at a different rate; Mimic clips are speech-sized.
            var samples = format.SampleRate == Rate ? mono.ToArray() : Enumerable.Range(0, (int)((long)mono.Count * Rate / format.SampleRate)).Select(i => mono[(int)((long)i * format.SampleRate / Rate)]).ToArray();
            PadSilence(samples.Length / (double)Rate);
            ring.Write(samples);
            lastFrameTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Loopback delivers nothing while a source is silent; keep the timeline moving with silence.</summary>
    private void PadSilence(double incomingSeconds)
    {
        if (ring == null || !Running) return;
        double gap = Stopwatch.GetElapsedTime(lastFrameTicks).TotalSeconds - incomingSeconds;
        if (gap > (incomingSeconds == 0 ? .025 : .1))
        {
            ring.WriteSilence((long)(Math.Min(gap, 120) * Rate));
            lastFrameTicks = Stopwatch.GetTimestamp();
        }
    }

    private void Fail(int session, string status)
    {
        lock (gate) { if (session != generation) return; Running = false; Status = status; }
    }

    public void Stop()
    {
        WasapiCapture? oldDevice;
        lock (gate)
        {
            generation++;
            processStop?.Cancel(); processStop = null;
            oldDevice = device; device = null;
            ring = null; Running = false; SourceName = null; Status = "Off";
        }
        if (oldDevice != null)
        {
            try { oldDevice.StopRecording(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            oldDevice.Dispose();
        }
    }
    public void Dispose() => Stop();
}
