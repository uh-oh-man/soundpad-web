using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace SoundpadWeb;

// Mimic: what an app or output device plays, kept as a rolling recording history (2.38, HistoryStore), so a
// session can turn something that was just said into a sound. Ported from Voice Replay Studio (docs/MIMIC.md).
// Capture never starts on its own, never records microphones or any other input, and audio never leaves this PC.

public sealed record MimicSource(string Id, string Name, string Kind);
/// <summary>A clip made with Mimic this session, shown to everyone so they can replay it.</summary>
public sealed record MimicClipInfo(string AssetId, string Name, string By, long At);
public sealed record MimicState(bool Active, string? Source, string Status, int WindowMs, bool Transcribing = false, bool TranscriptModel = false, string TranscriptStatus = "Off", bool Hotkey = false, string HotkeyStatus = "Off", MimicClipInfo[]? Recent = null, int HotkeySeconds = 10, string HotkeyKey = "F6",
    // 2.38: the rolling recording history. Older websites ignore it.
    MimicHistoryState? History = null);
/// <summary>What the website shows about the recording history (2.38).</summary>
/// <param name="SessionId">Id of this launch's history, also the "ringId" of peeks; null before anything was recorded.</param>
/// <param name="Seconds">How much history is kept (60 to 3600 s).</param>
/// <param name="Subtitles">"gaming", "balanced" or "fast": how hard local speech recognition may work.</param>
/// <param name="SourceLost">The output that was being recorded went away; the history is kept and the host is asked for another output.</param>
/// <param name="RecoveredMs">History recovered after the companion did not close normally; 0 once discarded or when nothing was recovered.</param>
/// <param name="Problem">A disk problem with the history, for the host.</param>
public sealed record MimicHistoryState(string? SessionId, int Seconds, string Subtitles, bool SourceLost, long RecoveredMs, string? Problem);
public sealed record MimicPeek(long End, int Rate, int WindowMs, int[] Levels, bool Active, TranscriptLine[]? Transcript = null, string? RingId = null);

/// <summary>
/// Where a capture delivers its audio. The companion writes it to the recording history; diagnostics use a
/// <see cref="MimicRing"/>. Write, Silence and Lost are called on the capture thread and must never wait on a disk.
/// </summary>
public interface IMimicSink
{
    void Write(ReadOnlySpan<float> mono);
    /// <summary>Digital silence: loopback delivers nothing while its source is quiet.</summary>
    void Silence(long frames);
    /// <summary>The source went away (device unplugged, app closed). Audio already delivered stays.</summary>
    void Lost(string status);
    /// <summary>Waits until delivered audio is stored. Only for test fixtures that deliver minutes at once; never call it from a real capture thread.</summary>
    void Drain();
}

/// <summary>Mono rolling sample buffer addressed by an absolute frame counter. Used by diagnostics; Mimic itself records into the history.</summary>
public sealed class MimicRing(int rate, int seconds) : IMimicSink
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
    void IMimicSink.Silence(long frames) => WriteSilence(frames);
    void IMimicSink.Lost(string status) { }
    void IMimicSink.Drain() { }
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
    /// <summary>Sample rate of the mono audio delivered to the sink (48 kHz in production, 16 kHz in fixtures).</summary>
    int Rate { get; }
    IReadOnlyList<MimicSource> Sources();
    /// <summary>Starts delivering audio to <paramref name="sink"/>, replacing any running capture.</summary>
    Task Start(MimicSource source, IMimicSink sink);
    void Stop();
    /// <summary>Brings the sink up to now: pads the silence loopback does not deliver. Readers call it before they look.</summary>
    void Pump();
    bool Running { get; }
    string Status { get; }
    string? SourceName { get; }
}

/// <summary>Captures an output device (everything it plays) or one app's process tree through WASAPI loopback.</summary>
public sealed class WasapiMimicCapture(Func<string, DataFlow>? flowOf = null) : IMimicCapture
{
    public const int CaptureRate = 48000;
    public int Rate => CaptureRate;
    private const int Rate48 = CaptureRate;
    private readonly object gate = new();
    private readonly Func<string, DataFlow> flowOf = flowOf ?? DeviceFlow;
    private CancellationTokenSource? captureStop;
    private IMimicSink? sink;
    private long lastFrameTicks;
    private int generation;
    public bool Running { get; private set; }
    public string Status { get; private set; } = "Off";
    public string? SourceName { get; private set; }
    /// <summary>Capture packets received from Windows since Start; used by the device probe.</summary>
    public long Packets => Interlocked.Read(ref packets);
    private long packets;
    public void Pump() { lock (gate) PadSilence(0); }

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

    /// <summary>Reads an endpoint's data flow without opening any stream on it.</summary>
    private static DataFlow DeviceFlow(string endpointId)
    {
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDevice(endpointId);
        return device.DataFlow;
    }

    /// <summary>
    /// Mimic records what this PC plays, never a microphone (2.38: checked here, not only by the source list). Refuses
    /// an endpoint whose data flow is not Render, and capture endpoint ids ({0.0.1.…}) whatever they report.
    /// </summary>
    public static void RequireOutput(string endpointId, DataFlow flow)
    {
        if (flow != DataFlow.Render || endpointId.StartsWith("{0.0.1.", StringComparison.Ordinal))
            throw new InvalidDataException("Mimic only records an output (what this PC plays), never a microphone or another input.");
    }

    public async Task Start(MimicSource source, IMimicSink target)
    {
        // Only an output endpoint or an app's own audio, and checked before anything running is touched.
        if (source.Kind == "output" && source.Id.StartsWith("output:", StringComparison.Ordinal)) RequireOutput(source.Id["output:".Length..], flowOf(source.Id["output:".Length..]));
        else if (!(source.Kind == "app" && source.Id.StartsWith("app:", StringComparison.Ordinal))) throw new InvalidDataException("Mimic only records an output or an app.");
        Stop();
        int session;
        CancellationTokenSource stop;
        lock (gate) { session = generation; stop = captureStop = new(); sink = target; lastFrameTicks = Stopwatch.GetTimestamp(); SourceName = source.Name; Status = "Connecting…"; }
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
                    builder = builder.WithProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree).WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(Rate48, 2));
                }
                else
                {
                    devices = new();
                    endpoint = devices.GetDevice(source.Id["output:".Length..]);
                    RequireOutput(endpoint.ID, endpoint.DataFlow); // the device the id resolved to, once more
                    builder = builder.WithDevice(endpoint).WithLoopbackCapture();
                }
                await using var recorder = await builder.BuildAsync();
                var format = recorder.WaveFormat;
                // Windows nearly always delivers 32-bit float at 48 kHz: mix those packets straight from the
                // buffer into one reused array instead of building stream objects for every 10 ms packet.
                bool direct = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 && format.SampleRate == Rate48;
                float[] scratch = [];
                lock (gate) if (session == generation) { Running = true; Status = "Listening"; }
                ready.TrySetResult();
                await foreach (var buffer in recorder.CaptureAsync(stop.Token))
                {
                    Interlocked.Increment(ref packets);
                    bool silent = buffer.Flags.HasFlag(AudioClientBufferFlags.Silent);
                    if (direct)
                    {
                        ReadOnlySpan<byte> data = buffer.Data.Span;
                        int frames = data.Length / format.BlockAlign, channels = format.Channels;
                        if (scratch.Length < frames) scratch = new float[Math.Max(frames, 4800)];
                        var mono = scratch.AsSpan(0, frames);
                        if (silent) mono.Clear();
                        else
                        {
                            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data[..(frames * format.BlockAlign)]);
                            for (int f = 0; f < frames; f++)
                            {
                                float sum = 0;
                                for (int c = 0; c < channels; c++) sum += samples[f * channels + c];
                                mono[f] = sum / channels;
                            }
                        }
                        Append(session, mono);
                    }
                    else
                    {
                        var bytes = buffer.Data.ToArray();
                        if (silent) Array.Clear(bytes);
                        Feed(session, bytes, bytes.Length, format);
                    }
                }
                // The stream ended without being stopped: the app closed or the device went away.
                if (!stop.IsCancellationRequested) Fail(session, source.Kind == "app" ? "The app closed. Choose another source and start again; the recording history is kept." : "The output device went away. Choose another output and start again; the recording history is kept.");
            }
            catch (OperationCanceledException) { ready.TrySetResult(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fail(session, "Capture stopped: " + ex.Message);
                ready.TrySetException(new InvalidDataException(source.Kind == "app"
                    ? "Cannot capture that app. It may have closed, or this Windows build lacks app capture."
                    : ex is InvalidDataException ? ex.Message : "Cannot capture that output: " + ex.Message));
            }
            finally { endpoint?.Dispose(); devices?.Dispose(); }
        });
        try { await ready.Task; }
        catch (InvalidDataException)
        {
            // Only undo this attempt; a newer Start may already be running.
            lock (gate) if (session == generation) { generation++; captureStop?.Cancel(); captureStop = null; sink = null; Running = false; SourceName = null; }
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
        // Resample by nearest frame when a device runs at a different rate; Mimic clips are speech-sized.
        var samples = format.SampleRate == Rate48 ? mono.ToArray() : Enumerable.Range(0, (int)((long)mono.Count * Rate48 / format.SampleRate)).Select(i => mono[(int)((long)i * format.SampleRate / Rate48)]).ToArray();
        Append(session, samples);
    }

    /// <summary>Adds 48 kHz mono samples to the sink, padding any silence that came first.</summary>
    private void Append(int session, ReadOnlySpan<float> samples)
    {
        lock (gate)
        {
            if (session != generation || sink == null) return;
            PadSilence(samples.Length / (double)Rate48);
            sink.Write(samples);
            lastFrameTicks = Stopwatch.GetTimestamp();
        }
    }

    private const double Jitter = 0.1;
    /// <summary>
    /// Loopback delivers nothing while a source is silent; keep the timeline moving with silence. Readers
    /// (waveform, transcript) leave the newest <see cref="Jitter"/> unpadded, so a packet that is merely late
    /// still lands where it belongs instead of after a silence that never happened. The history takes silence in
    /// steps of at most a second (a single huge step would count as an overrun).
    /// </summary>
    private void PadSilence(double incomingSeconds)
    {
        if (sink == null || !Running) return;
        double gap = Stopwatch.GetElapsedTime(lastFrameTicks).TotalSeconds - incomingSeconds;
        double keep = incomingSeconds == 0 ? Jitter : 0;
        if (gap > Jitter + keep)
        {
            double fill = Math.Min(gap - keep, 120);
            for (long left = (long)(fill * Rate48); left > 0; left -= Rate48) sink.Silence(Math.Min(left, Rate48));
            lastFrameTicks += (long)(fill * Stopwatch.Frequency);
        }
    }

    private void Fail(int session, string status)
    {
        lock (gate)
        {
            if (session != generation) return;
            // Only a capture that was running can be "lost"; one that never started is answered by Start's error.
            bool wasRunning = Running;
            Running = false; Status = status;
            if (wasRunning) sink?.Lost(status);
            sink = null;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            generation++;
            captureStop?.Cancel(); captureStop = null;
            sink = null; Running = false; SourceName = null; Status = "Off";
        }
    }
    public void Dispose() => Stop();
}
