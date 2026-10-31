using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace SoundpadWeb;

// Mimic: a rolling "instant replay" buffer of what an app or output device is playing, so a session
// can turn something that was just said into a sound. Ported from Voice Replay Studio (docs/MIMIC.md).
// Capture never starts on its own, never records microphones, and audio never leaves this PC.

public sealed record MimicSource(string Id, string Name, string Kind);
/// <summary>A clip made with Mimic this session, shown to everyone so they can replay it.</summary>
public sealed record MimicClipInfo(string AssetId, string Name, string By, long At);
public sealed record MimicState(bool Active, string? Source, string Status, int WindowMs, bool Transcribing = false, bool TranscriptModel = false, string TranscriptStatus = "Off", bool Hotkey = false, string HotkeyStatus = "Off", MimicClipInfo[]? Recent = null);
public sealed record MimicPeek(long End, int Rate, int WindowMs, int[] Levels, bool Active, TranscriptLine[]? Transcript = null, string? RingId = null);

/// <summary>Mono rolling sample buffer addressed by an absolute frame counter.</summary>
public sealed class MimicRing(int rate, int seconds)
{
    private readonly float[] data = new float[checked(rate * seconds)];
    private readonly object gate = new();
    private long written;
    public int Rate { get; } = rate;
    /// <summary>Distinguishes captures: frame counters restart at 0 on every start.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..16];
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
    private CancellationTokenSource? captureStop;
    private MimicRing? ring;
    private long lastFrameTicks;
    private int generation;
    public bool Running { get; private set; }
    public string Status { get; private set; } = "Off";
    public string? SourceName { get; private set; }
    /// <summary>Capture packets received from Windows since Start; used by the device probe.</summary>
    public long Packets => Interlocked.Read(ref packets);
    private long packets;
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
        CancellationTokenSource stop;
        lock (gate) { session = generation; stop = captureStop = new(); ring = new(Rate, seconds); lastFrameTicks = Stopwatch.GetTimestamp(); SourceName = source.Name; Status = "Connecting…"; }
        Interlocked.Exchange(ref packets, 0);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Build, run and dispose the recorder on one MTA task: process-loopback clients cannot cross apartments.
        _ = Task.Run(async () =>
        {
            MMDeviceEnumerator? devices = null;
            MMDevice? endpoint = null;
            try
            {
                var builder = new WasapiRecorderBuilder().WithSharedMode();
                if (source.Kind == "app")
                {
                    if (!uint.TryParse(source.Id.AsSpan(4), out uint pid)) throw new InvalidDataException("Unknown app.");
                    builder = builder.WithProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree).WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
                }
                else
                {
                    devices = new();
                    endpoint = devices.GetDevice(source.Id["output:".Length..]);
                    builder = builder.WithDevice(endpoint).WithLoopbackCapture();
                }
                await using var recorder = await builder.BuildAsync();
                var format = recorder.WaveFormat;
                lock (gate) if (session == generation) { Running = true; Status = "Listening"; }
                ready.TrySetResult();
                await foreach (var buffer in recorder.CaptureAsync(stop.Token))
                {
                    Interlocked.Increment(ref packets);
                    var bytes = buffer.Data.ToArray();
                    if (buffer.Flags.HasFlag(AudioClientBufferFlags.Silent)) Array.Clear(bytes);
                    Feed(session, bytes, bytes.Length, format);
                }
                // The stream ended without being stopped: the app closed or the device went away.
                if (!stop.IsCancellationRequested) Fail(session, source.Kind == "app" ? "The app closed. Choose a source and start again." : "The output device went away. Choose a source and start again.");
            }
            catch (OperationCanceledException) { ready.TrySetResult(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fail(session, "Capture stopped: " + ex.Message);
                ready.TrySetException(new InvalidDataException(source.Kind == "app"
                    ? "Cannot capture that app. It may have closed, or this Windows build lacks app capture."
                    : "Cannot capture that output: " + ex.Message));
            }
            finally { endpoint?.Dispose(); devices?.Dispose(); }
        });
        try { await ready.Task; }
        catch (InvalidDataException)
        {
            // Only undo this attempt; a newer Start may already be running.
            lock (gate) if (session == generation) { generation++; captureStop?.Cancel(); captureStop = null; ring = null; Running = false; SourceName = null; }
            throw;
        }
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

    private const double Jitter = 0.1;
    /// <summary>
    /// Loopback delivers nothing while a source is silent; keep the timeline moving with silence. Readers
    /// (waveform, transcript) leave the newest <see cref="Jitter"/> unpadded, so a packet that is merely late
    /// still lands where it belongs instead of after a silence that never happened.
    /// </summary>
    private void PadSilence(double incomingSeconds)
    {
        if (ring == null || !Running) return;
        double gap = Stopwatch.GetElapsedTime(lastFrameTicks).TotalSeconds - incomingSeconds;
        double keep = incomingSeconds == 0 ? Jitter : 0;
        if (gap > Jitter + keep)
        {
            double fill = Math.Min(gap - keep, 120);
            ring.WriteSilence((long)(fill * Rate));
            lastFrameTicks += (long)(fill * Stopwatch.Frequency);
        }
    }

    private void Fail(int session, string status)
    {
        lock (gate) { if (session != generation) return; Running = false; Status = status; }
    }

    public void Stop()
    {
        lock (gate)
        {
            generation++;
            captureStop?.Cancel(); captureStop = null;
            ring = null; Running = false; SourceName = null; Status = "Off";
        }
    }
    public void Dispose() => Stop();
}
