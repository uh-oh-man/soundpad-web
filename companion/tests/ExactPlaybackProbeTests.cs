using System.Xml.Linq;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// 2.37: the real-Soundpad exact-playback probe, with stand-ins: what it judges in a capture, when it refuses, and
/// that a whole run (against the mock, with a silent capture) leaves the library exactly as it was.
/// </summary>
public sealed class ExactPlaybackProbeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-exact-probe-test-" + Guid.NewGuid().ToString("N"));
    public ExactPlaybackProbeTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private const int Rate = 48000;
    private static float[] Silence(int ms) => new float[Rate * ms / 1000];
    private static float[] Join(params float[][] parts) => parts.SelectMany(p => p).ToArray();
    private static float[] Tone(double hz, int ms) => ExactPlaybackAnalysis.Tones(Rate, ExactPlaybackAnalysis.QuietLevel, (hz, ms));

    [Fact]
    public void AnExactSliceHasNoLeakAndNoOverrunWhileThePlayThenSeekLeakIsMeasured()
    {
        double floor = ExactPlaybackAnalysis.Floor(Silence(300));
        // 45 ms of output latency, then exactly the wanted second.
        var exact = ExactPlaybackAnalysis.Judge(Join(Silence(45), Tone(1900, 1000), Silence(500)), Rate, floor, 1900, 1000, [700], [3100]);
        Assert.True(exact.Exact, exact.Detail); Assert.Equal(0, exact.LeakMs); Assert.Equal(0, exact.OverrunMs); Assert.InRange(exact.OnsetMs!.Value, 40, 50);
        // What the old way did once on the real Soundpad: 37 ms of the beginning before the seek took effect.
        var leaked = ExactPlaybackAnalysis.Judge(Join(Silence(45), Tone(700, 37), Tone(1900, 1000), Silence(500)), Rate, floor, 1900, 1000, [700], [3100]);
        Assert.False(leaked.Exact); Assert.InRange(leaked.LeakMs, 30, 40);
        // A region whose end was found late, by polling.
        var late = ExactPlaybackAnalysis.Judge(Join(Silence(45), Tone(1900, 1000), Tone(3100, 120), Silence(300)), Rate, floor, 1900, 1000, [700], [3100]);
        Assert.False(late.Exact); Assert.InRange(late.OverrunMs, 110, 125);
        // A region cut short.
        Assert.False(ExactPlaybackAnalysis.Judge(Join(Silence(45), Tone(1900, 900), Silence(600)), Rate, floor, 1900, 1000, [700], [3100]).Exact);
        // Nothing captured is never a pass.
        var none = ExactPlaybackAnalysis.Judge(Silence(1600), Rate, floor, 1900, 1000, [700], [3100]);
        Assert.False(none.Exact); Assert.Null(none.WantedMs);
    }

    private sealed class SilentCapture : IMimicCapture
    {
        public IReadOnlyList<MimicSource> Sources() => [];
        int IMimicCapture.Rate => Rate;
        public Task Start(MimicSource source, IMimicSink sink) { Running = true; SourceName = source.Name; return Task.CompletedTask; }
        public void Stop() => Running = false;
        public void Pump() { }
        public bool Running { get; private set; }
        public string Status => Running ? "Listening (stand-in)" : "Off";
        public string? SourceName { get; private set; }
        public void Dispose() { }
    }

    [Fact]
    public async Task TheProbeRefusesWhileTheCompanionOrVRChatRunsOrSoundpadIsBusy()
    {
        ISoundpad Never() => throw new InvalidOperationException("the pipe must not be opened");
        var log = new StringWriter();
        Assert.Equal(2, await RealExactPlaybackProbe.Run(new(name => name == "VRChat", () => 1, Never, () => new SilentCapture(), folder), log));
        Assert.Contains("VRChat is running", log.ToString());
        Assert.Equal(2, await RealExactPlaybackProbe.Run(new(name => name == "SoundpadWeb", () => 1, Never, () => new SilentCapture(), folder), log));
        Assert.Equal(2, await RealExactPlaybackProbe.Run(new(_ => false, () => null, Never, () => new SilentCapture(), folder), log));
        await using var mock = new MockSoundpad(); mock.BeginFixturePlayback();
        Assert.Equal(2, await RealExactPlaybackProbe.Run(new(_ => false, () => 1, () => new SoundpadPipe(mock.Name), () => new SilentCapture(), folder), log));
        Assert.Contains("Soundpad is busy (PLAYING)", log.ToString());
        lock (mock.Commands) Assert.Equal(new[] { "GetPlayStatus()" }, mock.Commands.ToArray());
    }

    [Fact]
    public async Task AWholeRunPlaysSlicesThroughTheBridgeAndLeavesTheLibraryExactlyAsItWas()
    {
        await using var mock = new MockSoundpad { FollowFileLength = true };
        string before = mock.AllXml, categories = XDocument.Parse(mock.CategoryXml).ToString();
        var log = new StringWriter();
        int result = await RealExactPlaybackProbe.Run(new(_ => false, () => 1, () => new SoundpadPipe(mock.Name), () => new SilentCapture(), SlotFiles.LongPath(folder)), log);
        var text = log.ToString();
        // A silent stand-in capture hears nothing, so the verdict is "inconclusive", never a pass.
        Assert.True(result == 3, text); Assert.Contains("INCONCLUSIVE", text);
        // Three regions and three cue starts went through the Bridge as exact slices, and the 2.36 reference did not.
        var slots = mock.SlotPlays.ToArray();
        Assert.Equal(6, slots.Length);
        Assert.All(slots.Where((_, i) => i % 2 == 0), p => Assert.Equal(Rate, p.Frames)); // [1.0 s, 2.0 s)
        Assert.All(slots.Where((_, i) => i % 2 == 1), p => Assert.Equal(Rate, p.Frames)); // from 2.0 s to the end (3.0 s)
        Assert.Contains("DoSeekMs(1000)", mock.Commands);
        // Restored: the same sounds and categories as before, no probe or Temp category, no slot entry, never a risky removal.
        Assert.Contains("library identical to before: True", text);
        Assert.Equal(XDocument.Parse(before).ToString(), XDocument.Parse(mock.AllXml).ToString());
        Assert.Equal(categories, XDocument.Parse(mock.CategoryXml).ToString());
        Assert.Equal(0, mock.RiskyRemovals);
        Assert.Empty(Directory.GetDirectories(SlotFiles.LongPath(folder)));
    }
}
