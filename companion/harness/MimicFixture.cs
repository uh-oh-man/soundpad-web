using System.Diagnostics;
namespace SoundpadWeb.Testing;

/// <summary>Test-only Mimic source: a 0.5 s, 440 Hz tone every 2 s, generated in real time. Never ships in the EXE.</summary>
public sealed class MimicFixture : IMimicCapture
{
    public const int Rate = 16000;
    private readonly object gate = new();
    private MimicRing? ring;
    private long started;
    public static readonly MimicSource Source = new("fixture:tone", "Fixture: tone every 2 s", "output");
    public bool Running { get; private set; }
    public string Status { get; private set; } = "Off";
    public string? SourceName { get; private set; }
    public IReadOnlyList<MimicSource> Sources() => [Source];
    public MimicRing? Ring
    {
        get
        {
            lock (gate)
            {
                if (ring == null) return null;
                long due = (long)(Stopwatch.GetElapsedTime(started).TotalSeconds * Rate) - ring.Written;
                if (due > 0) ring.Write(Enumerable.Range(0, (int)Math.Min(due, Rate * 60L)).Select(i => Sample(ring.Written + i)).ToArray());
                return ring;
            }
        }
    }
    /// <summary>Advance the fixture clock without waiting, for unit tests.</summary>
    public void Advance(TimeSpan time) { lock (gate) started -= (long)(time.TotalSeconds * Stopwatch.Frequency); }
    public static float Sample(long frame) => frame % (2 * Rate) < Rate / 2 ? 0.5f * MathF.Sin(2 * MathF.PI * 440 * frame / Rate) : 0;
    public Task Start(MimicSource source, int seconds)
    {
        lock (gate) { ring = new(Rate, seconds); started = Stopwatch.GetTimestamp(); Running = true; Status = "Listening"; SourceName = source.Name; }
        return Task.CompletedTask;
    }
    public void Stop() { lock (gate) { ring = null; Running = false; Status = "Off"; SourceName = null; } }
    public void Dispose() => Stop();
}
