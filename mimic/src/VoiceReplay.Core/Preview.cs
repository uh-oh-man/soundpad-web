using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceReplay.Core;

public sealed record MonitorOutput(string Id, string Name, bool Allowed, string Reason, bool Muted, float Volume)
{
    public string Label => $"{Name}{(Muted ? " · MUTED" : "")} · {Volume:P0}{(!Allowed ? " · " + Reason : "")}";
}
public static class MonitorOutputs
{
    public static string Rejection(string name)
    {
        string n = name.ToLowerInvariant();
        if (new[] { "microphone", "stream mix", "stream output", "voice chat", "voicemeeter", "cable input", "soundpad", "nvidia broadcast", "steam streaming" }.Any(n.Contains))
            return "Outgoing/mixing endpoint — choose headphones or a monitor";
        if ((n.Contains("elgato virtual") || n.Contains("wave link")) && !n.Contains("monitor"))
            return "Elgato application bus — choose the Monitor output";
        return "";
    }
    public static List<MonitorOutput> List()
    {
        using var e = new MMDeviceEnumerator();
        var result = new List<MonitorOutput>();
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (d)
            {
                var reason = Rejection(d.FriendlyName);
                result.Add(new(d.ID, d.FriendlyName, reason.Length == 0, reason, d.AudioEndpointVolume.Mute, d.AudioEndpointVolume.MasterVolumeLevelScalar));
            }
        return result.OrderByDescending(x => x.Allowed).ThenBy(x => x.Name).ToList();
    }
    public static string SessionDiagnostic(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Choose a Monitor output first, then run Output diagnostic.");
        using var e = new MMDeviceEnumerator();
        using var d = e.GetDevice(id);
        string text = $"Output: {d.FriendlyName}; endpoint mute={d.AudioEndpointVolume.Mute}, volume={d.AudioEndpointVolume.MasterVolumeLevelScalar:P0}";
        var sessions = d.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
            using (var session = sessions[i])
                if (session.GetProcessID == Environment.ProcessId)
                    text += $"; app session mute={session.SimpleAudioVolume.Mute}, volume={session.SimpleAudioVolume.Volume:P0}, state={session.State}";
        return text;
    }
}
public sealed class PreviewSamples : ISampleProvider
{
    private readonly float[] samples; private readonly int first, last; private readonly bool loop; private int offset;
    public WaveFormat WaveFormat
    {
        get;
    }
    public int Length => last - first;
    public PreviewSamples(float[] samples, int rate, double start, double end, bool loop, double? cursor = null)
    {
        if (rate <= 0 || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > samples.Length / (double)rate + 1e-8 || end <= start)
            throw new ArgumentException("Choose a non-empty playback region inside the waveform.");
        first = (int)Math.Round(start * rate);
        last = Math.Min(samples.Length, (int)Math.Round(end * rate));
        if (last <= first)
            throw new ArgumentException("Selection is shorter than one sample.");
        if (cursor is { } point && (!double.IsFinite(point) || point < start || point >= end))
            throw new ArgumentException("Seek point must be inside the current playback region.");
        this.samples = samples;
        this.loop = loop;
        offset = cursor is { } position ? (int)Math.Round(position * rate) : first;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
    }
    public int Read(float[] buffer, int start, int count)
    {
        int read = 0;
        // NAudio WaveBuffer can expose a byte[] through a float[] view. Array.Copy checks the runtime type and fails.
        while (read < count)
        {
            if (offset >= last)
            {
                if (!loop)
                    break;
                offset = first;
            }
            int n = Math.Min(count - read, last - offset);
            for (int i = 0; i < n; i++)
                buffer[start + read + i] = samples[offset + i];
            offset += n;
            read += n;
        }
        return read;
    }
}
public interface IPreviewOutput : IDisposable
{
    bool Playing
    {
        get;
    }
    double Seconds
    {
        get;
    }
    event Action<Exception?>? Stopped;
    void Initialize(ISampleProvider source); void Play(); void Stop(); string Diagnostic();
}
public sealed class WasapiPreviewOutput : IPreviewOutput
{
    private readonly MMDevice device; private readonly WasapiOut output;
    public event Action<Exception?>? Stopped;
    public bool Playing => output.PlaybackState == PlaybackState.Playing;
    public double Seconds => output.GetPosition() / (double)output.OutputWaveFormat.AverageBytesPerSecond;
    public WasapiPreviewOutput(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Select a Monitor output below the waveform before pressing Play or Test Playback.");
        using var e = new MMDeviceEnumerator();
        try
        {
            device = e.GetDevice(id);
        }
        catch (Exception ex) { throw new InvalidOperationException("Selected monitor could not be opened. Refresh outputs and choose connected headphones or an Elgato Monitor output. " + ex.Message, ex); }
        try
        {
            string reason = MonitorOutputs.Rejection(device.FriendlyName);
            if (reason.Length > 0)
                throw new InvalidOperationException(reason);
            if (device.State != DeviceState.Active)
                throw new InvalidOperationException("Selected output is disconnected. Refresh outputs and choose a connected monitor.");
            if (device.AudioEndpointVolume.Mute || device.AudioEndpointVolume.MasterVolumeLevelScalar <= .0001f)
                throw new InvalidOperationException($"{device.FriendlyName} is muted or at 0%. Check Windows volume; the app will not change it.");
            output = new(device, AudioClientShareMode.Shared, false, 50);
            output.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
        }
        catch { device.Dispose(); throw; }
    }
    public void Initialize(ISampleProvider source) => output.Init(source);
    public void Play() => output.Play(); public void Stop() => output.Stop();
    public string Diagnostic() => MonitorOutputs.SessionDiagnostic(device.ID);
    public void Dispose()
    {
        output.Dispose();
        device.Dispose();
    }
}
public class PreviewPlayer : IDisposable
{
    private readonly Func<string, IPreviewOutput> factory; private readonly Action<string> log;
    private IPreviewOutput? output; private Action<Exception?>? handler; private double start, end; private bool loop; private volatile bool failed;
    private float[]? data; private int dataRate; private string device = ""; private double seekOffset;
    public string Status { get; private set; } = "Stopped";
    public bool Playing => !failed && output?.Playing == true;
    public double Position
    {
        get
        {
            if (Status == "Finished")
                return end;
            if (output is null)
                return start;
            double elapsed = output.Seconds + seekOffset;
            return start + (loop ? elapsed % (end - start) : Math.Min(end - start, elapsed));
        }
    }
    public PreviewPlayer(Action<string>? log = null, Func<string, IPreviewOutput>? factory = null)
    {
        this.log = log ?? (_ => { });
        this.factory = factory ?? (id => new WasapiPreviewOutput(id));
    }
    public void Play(float[] samples, int rate, double a, double b, string deviceId, bool repeat, double? cursor = null)
    {
        Stop();
        try
        {
            var source = new PreviewSamples(samples, rate, a, b, repeat, cursor);
            if (AudioFiles.Silent(samples.Skip((int)Math.Round(a * rate)).Take(source.Length).ToArray()))
                throw new InvalidOperationException("Selected audio contains only silence. Choose a region with visible signal.");
            var next = factory(deviceId);
            output = next;
            start = a;
            end = b;
            loop = repeat;
            data = samples;
            dataRate = rate;
            device = deviceId;
            seekOffset = (cursor ?? a) - a;
            failed = false;
            handler = error => { if (!ReferenceEquals(output, next)) return; failed = error is not null; Status = error is null ? "Finished" : "Playback failed: " + error.Message; log(error is null ? Status : error.ToString()); };
            next.Stopped += handler;
            next.Initialize(source);
            Status = "Playing";
            next.Play();
            string diagnostic;
            try
            {
                diagnostic = next.Diagnostic();
            }
            catch (Exception ex) { diagnostic = "Session diagnostics unavailable: " + ex.Message; }
            log($"Preview {a:F3}–{b:F3}s; {rate} Hz mono; loop={repeat}; " + diagnostic);
            if (diagnostic.Contains("app session mute=True") || diagnostic.Contains("app session mute=False, volume=0%"))
                throw new InvalidOperationException("App session is muted / at zero in Windows Volume Mixer. No settings were changed.");
        }
        catch (Exception ex) { Stop(); Status = "Playback failed: " + ex.Message; log(Status); throw new InvalidOperationException(Status, ex); }
    }
    public void Stop()
    {
        var old = output;
        output = null;
        if (old is not null)
        {
            old.Stopped -= handler;
            try
            {
                old.Stop();
            }
            finally { old.Dispose(); }
        }
        Status = "Stopped";
    }
    public void Seek(double seconds)
    {
        if (data is null)
            throw new InvalidOperationException("Start playback before seeking.");
        Play(data, dataRate, start, end, device, loop, seconds);
    }
    public void Dispose() => Stop();
    public static float[] TestTone(int rate = 48000) => Enumerable.Range(0, (int)(rate * .4)).Select(i => (float)(.035 * Math.Sin(2 * Math.PI * 660 * i / rate) * Math.Min(1, i / (rate * .02)) * Math.Min(1, (rate * .4 - i) / (rate * .04)))).ToArray();
}
