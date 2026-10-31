using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class MimicTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-mimic-test-" + Guid.NewGuid().ToString("N"));
    public MimicTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static T Data<T>(Reply reply) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(reply.Data, Json.Options), Json.Options)!;

    [Fact]
    public void RingReadsAbsoluteFramesAndRejectsOverwrittenAudio()
    {
        var ring = new MimicRing(10, 2);
        ring.Write(Enumerable.Range(0, 15).Select(i => (float)i).ToArray());
        Assert.Equal(15, ring.Written); Assert.Equal(0, ring.Oldest);
        Assert.Equal([12f, 13f, 14f], ring.Read(12, 15));
        ring.WriteSilence(10);
        Assert.Equal(5, ring.Oldest);
        Assert.Equal([14f, 0f], ring.Read(14, 16));
        Assert.Throws<InvalidDataException>(() => ring.Read(4, 6));
        Assert.Throws<InvalidDataException>(() => ring.Read(20, 26));
        Assert.Equal([0, 0], ring.Levels(25, 4, 2));
    }

    [Fact]
    public void WavIsMono16BitAtTheCaptureRate()
    {
        using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(MimicRing.Wav([0f, 0.5f, -1f], 16000)));
        Assert.Equal((1, 16, 16000), (reader.WaveFormat.Channels, reader.WaveFormat.BitsPerSample, reader.WaveFormat.SampleRate));
        Assert.Equal(6, reader.Length);
    }

    [Fact]
    public async Task OnlyTheHostChoosesSourcesAndGuestsNeedPermission()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.False(bridge.Snapshot.Mimic!.Active);
        Assert.False((await bridge.Execute(Cmd("mimicSources"), "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicStart") with { SourceId = "output:not-listed" }, "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        Assert.True(bridge.Snapshot.Mimic!.Active); Assert.Equal(MimicFixture.Source.Name, bridge.Snapshot.Mimic.Source);
        Assert.True((await bridge.Execute(Cmd("mimicPeek"), "host")).Ok);
        var denied = await bridge.Execute(Cmd("mimicPeek"), "guest");
        Assert.False(denied.Ok); Assert.Contains("not enabled Mimic", denied.Error);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicPeek"), "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicStop"), "guest")).Ok);
        Assert.True((await bridge.Execute(Cmd("endSession"), "host")).Ok);
        Assert.False(bridge.Snapshot.Mimic!.Active);
    }

    [Fact]
    public async Task SelectionsReachingBeforeCaptureStartedAreTrimmed()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(2.2));
        var peek = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "host"));
        // "Last 5 s" after 2.2 s of listening: the first 2.8 s were never recorded.
        var clip = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 25000, EndMs = 30000 }, "host");
        Assert.True(clip.Ok, clip.Error);
        var tooEarly = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 1000, EndMs = 3000 }, "host");
        Assert.False(tooEarly.Ok); Assert.Contains("not started", tooEarly.Error);
    }
    [Fact]
    public async Task GuestClipBecomesATempSoundWithExactlyTheSelectedAudio()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true, GuestUploads: false) }, "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(31));
        var peek = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "guest"));
        Assert.Equal(Bridge.MimicBuckets, peek.Levels.Length); Assert.Contains(peek.Levels, l => l > 400); Assert.Contains(0, peek.Levels);
        long windowStart = peek.End - MimicFixture.Rate * 30L;
        // Find the start of a tone burst inside the window and select exactly its half second.
        long burst = windowStart + (2 * MimicFixture.Rate - windowStart % (2 * MimicFixture.Rate)) % (2 * MimicFixture.Rate);
        long fromMs = (burst - windowStart) * 1000 / MimicFixture.Rate;
        var silent = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs + 600, EndMs = fromMs + 1400 }, "guest");
        Assert.False(silent.Ok); Assert.Contains("silent", silent.Error);
        var clip = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs, EndMs = fromMs + 500, Name = "Said it" }, "guest", new("guest-1", "Alice"), "peer-1");
        Assert.True(clip.Ok, clip.Error);
        var sound = bridge.Snapshot.Library.Sounds.Single(s => s.Temporary);
        Assert.Equal("Alice", sound.UploadedBy); Assert.StartsWith("Said it", sound.Name);
        var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        using (var wave = new NAudio.Wave.WaveFileReader(journal.Assets.Single().Variants.Single().Path))
            Assert.InRange(wave.TotalTime.TotalMilliseconds, 480, 520);
        fixture.Advance(TimeSpan.FromSeconds(31 + Bridge.MimicGraceSeconds));
        var expired = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs, EndMs = fromMs + 500 }, "guest");
        Assert.False(expired.Ok); Assert.Contains("scrolled out", expired.Error);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: false) }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 0, EndMs = 500 }, "guest")).Ok);
    }
}
