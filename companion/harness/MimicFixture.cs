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
    /// <summary>
    /// Seconds the tone had already been playing when listening started (the e2e harness uses 10, so "Last 3 s" right
    /// after Start finds 3 s, as it did with the 30 s buffer before 2.39). At most 20; 0 in unit tests.
    /// </summary>
    public int PrefillSeconds { get; init; }
    /// <summary>
    /// Delivers everything due since Start, a second at a time, waiting for the history to store it every 20 s. With a
    /// prefill, the first delivery after Start goes in one piece (up to 20 s), so the history dates its first frame
    /// correctly; unit tests (no prefill) keep one-second pieces.
    /// </summary>
    public void Pump()
    {
        lock (gate)
        {
            if (sink == null) return;
            long due = (long)(Stopwatch.GetElapsedTime(started).TotalSeconds * Rate) - written;
            var block = new float[written == 0 && PrefillSeconds > 0 ? Math.Max(Rate, (int)Math.Min(due, 20L * Rate)) : Rate];
            long sinceDrain = 0;
            while (due > 0)
            {
                int n = (int)Math.Min(due, block.Length);
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
        lock (gate) { sink = target; started = Stopwatch.GetTimestamp() - (long)(Math.Clamp(PrefillSeconds, 0, 20) * Stopwatch.Frequency); written = 0; Running = true; Status = "Listening"; SourceName = source.Name; }
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
