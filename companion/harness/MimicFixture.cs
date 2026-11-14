using System.Diagnostics;
namespace SoundpadWeb.Testing;

/// <summary>
/// Test-only Mimic source: a 0.5 s, 440 Hz tone every 2 s, generated in real time and delivered whenever someone
/// pumps it. Opens no audio device and never ships in the EXE.
/// </summary>
public sealed class MimicFixture : IMimicCapture
{
    public const int Rate = 16000;
    int IMimicCapture.Rate => Rate;
    private readonly object gate = new();
    private IMimicSink? sink;
    private long started, written;
    public static readonly MimicSource Source = new("fixture:tone", "Fixture: tone every 2 s", "output");
    public bool Running { get; private set; }
    public string Status { get; private set; } = "Off";
    public string? SourceName { get; private set; }
    public IReadOnlyList<MimicSource> Sources() => [Source];
    /// <summary>Delivers everything due since Start, a second at a time, waiting for the history to store it every 20 s.</summary>
    public void Pump()
    {
        lock (gate)
        {
            if (sink == null) return;
            long due = (long)(Stopwatch.GetElapsedTime(started).TotalSeconds * Rate) - written;
            var block = new float[Rate];
            long sinceDrain = 0;
            while (due > 0)
            {
                int n = (int)Math.Min(due, Rate);
                for (int i = 0; i < n; i++) block[i] = Sample(written + i);
                sink.Write(block.AsSpan(0, n));
                written += n; due -= n; sinceDrain += n;
                if (sinceDrain >= 20L * Rate) { sink.Drain(); sinceDrain = 0; }
            }
        }
    }
    /// <summary>Advance the fixture clock without waiting, for unit tests.</summary>
    public void Advance(TimeSpan time) { lock (gate) started -= (long)(time.TotalSeconds * Stopwatch.Frequency); }
    public static float Sample(long frame) => frame % (2 * Rate) < Rate / 2 ? 0.5f * MathF.Sin(2 * MathF.PI * 440 * frame / Rate) : 0;
    public Task Start(MimicSource source, IMimicSink target)
    {
        lock (gate) { sink = target; started = Stopwatch.GetTimestamp(); written = 0; Running = true; Status = "Listening"; SourceName = source.Name; }
        return Task.CompletedTask;
    }
    /// <summary>Simulates the output device going away (unplugged), as WASAPI reports it.</summary>
    public void Lose()
    {
        Pump();
        lock (gate)
        {
            if (sink == null) return;
            Running = false; Status = "The output device went away. Choose another output and start again; the recording history is kept.";
            sink.Lost(Status); sink = null; SourceName = null;
        }
    }
    public void Stop() { lock (gate) { sink = null; Running = false; Status = "Off"; SourceName = null; } }
    public void Dispose() => Stop();
}
