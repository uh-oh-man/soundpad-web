using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace VoiceReplay.Core;

public sealed record AudioSource(string Id, string Name, string Kind, string ProcessName = "")
{
    public override string ToString() => Name;
}
public sealed class CaptureService : IDisposable
{
    private WasapiCapture? capture;
    private MMDevice? device;
    private CancellationTokenSource? processCancellation;
    private Task? processTask;
    private readonly object clockGate = new();
    private long lastFrame;
    private int generation;
    public RingBuffer? Buffer
    {
        get; private set;
    }
    public string Status { get; private set; } = "Paused";
    public float Peak
    {
        get; private set;
    }
    public DateTime LastSignal
    {
        get; private set;
    }
    public bool Running
    {
        get; private set;
    }
    public string SourceName { get; private set; } = "";
    public static List<AudioSource> Sources()
    {
        var result = new List<AudioSource>();
        using var e = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d)
                    result.Add(new(d.ID, $"{(flow == DataFlow.Render ? "Output" : "MIC — explicit input")}: {d.FriendlyName}", flow == DataFlow.Render ? "Output" : "Microphone"));
            }
        foreach (var p in Process.GetProcesses().OrderBy(x => x.ProcessName))
        {
            using (p)
                try
                {
                    if (p.Id != Environment.ProcessId && (p.MainWindowHandle != IntPtr.Zero || p.ProcessName.Contains("discord", StringComparison.OrdinalIgnoreCase)))
                        result.Add(new(p.Id.ToString(), $"App: {p.ProcessName} [{p.Id}]", "Process", p.ProcessName));
                }
                catch { }
        }
        return result;
    }
    public async Task Start(AudioSource source, int seconds = 30)
    {
        Stop();
        int session = Volatile.Read(ref generation);
        SourceName = source.Name;
        Status = "Connecting…";
        try
        {
            if (source.Kind == "Process")
            {
                processCancellation = new();
                var token = processCancellation.Token;
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // IAudioClient returned by process activation cannot be marshaled to WPF's STA.
                // Activate, initialize, read and release it entirely in the MTA.
                processTask = Task.Run(async () =>
                {
                    try
                    {
                        using var client = await ProcessLoopback.Activate(int.Parse(source.Id));
                        if (token.IsCancellationRequested || session != Volatile.Read(ref generation))
                        {
                            ready.TrySetResult();
                            return;
                        }
                        var format = new WaveFormat(48000, 16, 2);
                        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 200000, 0, format, Guid.Empty);
                        var ring = new RingBuffer(format.SampleRate, seconds);
                        lock (clockGate)
                        {
                            if (token.IsCancellationRequested || session != Volatile.Read(ref generation))
                            {
                                ready.TrySetResult();
                                return;
                            }
                            Buffer = ring;
                            Running = true;
                            Status = "Monitoring application";
                            lastFrame = Stopwatch.GetTimestamp();
                            client.Start();
                        }
                        ready.TrySetResult();
                        try
                        {
                            var reader = client.AudioCaptureClient;
                            while (!token.IsCancellationRequested)
                            {
                                while (!token.IsCancellationRequested && reader.GetNextPacketSize() > 0)
                                {
                                    var ptr = reader.GetBuffer(out int frames, out var flags);
                                    try
                                    {
                                        var bytes = new byte[frames * format.BlockAlign];
                                        if (!flags.HasFlag(AudioClientBufferFlags.Silent))
                                            Marshal.Copy(ptr, bytes, 0, bytes.Length);
                                        Feed(bytes, bytes.Length, format, ring);
                                    }
                                    finally { reader.ReleaseBuffer(frames); }
                                }
                                await Task.Delay(10, token);
                            }
                        }
                        finally { client.Stop(); }
                    }
                    catch (OperationCanceledException) { ready.TrySetResult(); }
                    catch (Exception ex)
                    {
                        if (session == Volatile.Read(ref generation))
                        {
                            Running = false;
                            Status = "Application capture disconnected: " + ex.Message;
                            ready.TrySetException(ex);
                        }
                        else
                            ready.TrySetResult();
                    }
                });
                await ready.Task;
            }
            else
            {
                using var e = new MMDeviceEnumerator();
                device = e.GetDevice(source.Id);
                var activeCapture = source.Kind == "Microphone" ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
                capture = activeCapture;
                Buffer = new(activeCapture.WaveFormat.SampleRate, seconds);
                activeCapture.DataAvailable += (_, args) => { if (ReferenceEquals(capture, activeCapture)) Feed(args.Buffer, args.BytesRecorded, activeCapture.WaveFormat); };
                activeCapture.RecordingStopped += (_, args) => { if (!ReferenceEquals(capture, activeCapture)) return; Running = false; Status = args.Exception is null ? "Paused" : "Device disconnected: " + args.Exception.Message; };
                Running = true;
                Status = "Monitoring " + source.Kind.ToLowerInvariant();
                activeCapture.StartRecording();
            }
            LastSignal = DateTime.Now;
            lastFrame = Stopwatch.GetTimestamp();
        }
        catch (Exception ex) { if (session == Volatile.Read(ref generation)) { Stop(); Status = "Cannot capture: " + ex.Message; } throw; }
    }
    private void Feed(byte[] bytes, int length, WaveFormat format, RingBuffer? expected = null)
    {
        if (length == 0)
        {
            UpdateClock();
            return;
        }
        var samples = AudioFiles.Decode(bytes, length, format);
        lock (clockGate)
        {
            if (expected is not null && !ReferenceEquals(expected, Buffer))
                return;
            PadSilence(samples.Length / (double)format.SampleRate);
            Buffer?.Write(samples);
            lastFrame = Stopwatch.GetTimestamp();
        }
        float peak = 0;
        foreach (float s in samples)
            peak = Math.Max(peak, Math.Abs(s));
        Peak = peak;
        if (peak > 0.0005f)
            LastSignal = DateTime.Now;
    }
    private void PadSilence(double incomingSeconds = 0)
    {
        if (Buffer is null || lastFrame == 0)
            return;
        double gap = Stopwatch.GetElapsedTime(lastFrame).TotalSeconds - incomingSeconds;
        if (gap > (incomingSeconds == 0 ? .025 : .1))
        {
            Buffer.Write(new float[(int)(Math.Min(gap, 120) * Buffer.Rate)]);
            lastFrame = Stopwatch.GetTimestamp();
            Peak = 0;
        }
    }
    public void UpdateClock()
    {
        if (Running)
            lock (clockGate)
                PadSilence();
    }
    public float[] Snapshot(int seconds)
    {
        if (!Running || Buffer is null)
            throw new InvalidOperationException("Monitoring is paused or disconnected.");
        UpdateClock();
        var audio = Buffer.Snapshot(seconds);
        if (AudioFiles.Silent(audio))
            throw new InvalidOperationException("The selected source is silent. No empty clip was saved. Check the source diagnostic.");
        return audio;
    }
    public void Stop()
    {
        Interlocked.Increment(ref generation);
        processCancellation?.Cancel();
        processTask = null;
        processCancellation?.Dispose();
        processCancellation = null;
        var oldCapture = capture;
        capture = null;
        if (oldCapture is not null)
        {
            try
            {
                oldCapture.StopRecording();
            }
            catch { }
            oldCapture.Dispose();
        }
        device?.Dispose();
        device = null;
        Running = false;
        Peak = 0;
        lock (clockGate)
        {
            Buffer = null;
            lastFrame = 0;
        }
        Status = "Paused";
    }
    public void Dispose() => Stop();
}

// Microsoft process-loopback activation; no injection, driver, or routing changes.
public static class ProcessLoopback
{
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public int Size; [FieldOffset(16)] public IntPtr Data;
    }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOperation
    {
        [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }
    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICompletion
    {
        [PreserveSig] int ActivateCompleted(IOperation operation);
    }
    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgile
    {
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : ICompletion, IAgile, ICustomQueryInterface
    {
        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr pointer)
        {
            pointer = IntPtr.Zero;
            if (iid == typeof(IAgile).GUID)
            {
                pointer = Marshal.GetComInterfaceForObject(this, typeof(IAgile), CustomQueryInterfaceMode.Ignore);
                return CustomQueryInterfaceResult.Handled;
            }
            return CustomQueryInterfaceResult.NotHandled;
        }
        public readonly TaskCompletionSource<AudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IOperation operation)
        {
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int hr, out object instance));
                Marshal.ThrowExceptionForHR(hr);
                Result.TrySetResult(new AudioClient((IAudioClient)instance));
            }
            catch (Exception ex) { Result.TrySetException(ex); }
            return 0;
        }
    }
    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, IntPtr parameters, ICompletion callback, out IOperation operation);
    public static async Task<AudioClient> Activate(int pid)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("Application capture requires Windows build 20348+. Choose an output device.");
        IntPtr blob = Marshal.AllocHGlobal(12);
        IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        var handler = new Completion();
        IOperation? operation = null;
        try
        {
            Marshal.WriteInt32(blob, 0, 1);
            Marshal.WriteInt32(blob, 4, pid);
            Marshal.WriteInt32(blob, 8, 0);
            var prop = new PropVariant { Type = 65, Size = 12, Data = blob };
            var iid = typeof(IAudioClient).GUID;
            Marshal.StructureToPtr(prop, variant, false);
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, handler, out operation));
            return await handler.Result.Task; // activation callback owns completion, keep blob alive until it arrives
        }
        finally { Marshal.FreeHGlobal(variant); Marshal.FreeHGlobal(blob); if (operation is not null) Marshal.ReleaseComObject(operation); GC.KeepAlive(handler); }
    }
}
