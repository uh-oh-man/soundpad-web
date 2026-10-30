using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class SessionAudioTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-session-test-" + Guid.NewGuid().ToString("N"));
    public SessionAudioTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static Command Cmd(string op, string? id = null, string? version = null) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op, id, version);
    private string Wave()
    {
        var path = Path.Combine(folder, "source.wav");
        using var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48000, 16, 2));
        // Distinct stereo frames make reversal / channel swaps observable.
        writer.Write([0x10, 0x00, 0x20, 0x00, 0x30, 0x00, 0x40, 0x00, 0x50, 0x00, 0x60, 0x00], 0, 12); return path;
    }
    [Fact]
    public void ReversePreservesChannelsAndOriginalBytes()
    {
        var input = Wave(); var before = File.ReadAllBytes(input); var output = Path.Combine(folder, "reverse.wav"); SessionAudio.ConvertAudio(input, output, true);
        using var wave = new NAudio.Wave.WaveFileReader(output); var data = new byte[12]; Assert.Equal(12, wave.Read(data, 0, 12));
        // Float conversion to PCM may round down by one; compare the ordered sample pattern.
        Assert.InRange(BitConverter.ToInt16(data, 0), 79, 80); Assert.InRange(BitConverter.ToInt16(data, 2), 95, 96);
        Assert.InRange(BitConverter.ToInt16(data, 8), 15, 16); Assert.InRange(BitConverter.ToInt16(data, 10), 31, 32);
        Assert.Equal(before, File.ReadAllBytes(input));
    }
    [Fact]
    public async Task UploadsUseTempAndOnlyHostCanKeepBeforeCleanup()
    {
        var input = Wave(); await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var originals = bridge.Snapshot.Library.Sounds.Select(s => s.Id).ToArray();
        Assert.True((await bridge.Upload(File.ReadAllBytes(input), "First.wav", "Alice")).Ok);
        var first = bridge.Snapshot.Library.Sounds.Single(s => s.Temporary); Assert.Equal("Alice", first.UploadedBy);
        Assert.Contains(bridge.Snapshot.Library.Categories, c => c.Name == "temp" && c.SoundIds.Contains(first.Id));
        Assert.False((await bridge.Execute(Cmd("keepAudio", first.Id), "guest")).Ok);
        var beforeKeep = bridge.Snapshot.Library.Version;
        Assert.True((await bridge.Execute(Cmd("keepAudio", first.Id), "host")).Ok); Assert.NotEqual(beforeKeep, bridge.Snapshot.Library.Version);
        Assert.True((await bridge.Upload(File.ReadAllBytes(input), "Second.wav", "Bob")).Ok);
        Assert.True((await bridge.Execute(Cmd("endSession"), "host")).Ok);
        Assert.False((await bridge.Upload(File.ReadAllBytes(input), "Late.wav", "Guest")).Ok);
        Assert.Equal(5, bridge.Snapshot.Library.Sounds.Length); Assert.Contains(bridge.Snapshot.Library.Sounds, s => s.Id == first.Id && !s.Temporary); Assert.All(originals, id => Assert.Contains(bridge.Snapshot.Library.Sounds, s => s.Id == id));
        Assert.DoesNotContain("DoRemoveSelectedEntries(true)", mock.Commands);
        Assert.Single(Directory.GetFiles(Path.Combine(folder, "session-audio"), "*.wav"));
    }
    [Fact]
    public async Task ReverseImportsOnceAndPlaysThroughNormalSoundpadRoute()
    {
        var input = Wave(); await using var mock = new MockSoundpad { AllXml = MockSoundpad.Sounds.Replace(@"C:\Private\airhorn.wav", input), CategoryXml = MockSoundpad.Categories.Replace(@"C:\Private\airhorn.wav", input) };
        using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var original = bridge.Snapshot.Library.Sounds[0].Id;
        for (int i = 0; i < 2; i++) Assert.True((await bridge.Execute(Cmd("reverse", original, bridge.Snapshot.Library.Version), "guest")).Ok);
        Assert.Single(bridge.Snapshot.Library.Sounds, s => s.Temporary); Assert.Equal(2, mock.Commands.Count(c => c == "DoPlaySound(5)"));
        Assert.True((await bridge.Execute(Cmd("endSession"), "host")).Ok); Assert.Equal(4, bridge.Snapshot.Library.Sounds.Length);
    }
    [Fact]
    public async Task RecoveryCleanupNeverRemovesUnownedEntriesInExistingTemp()
    {
        var input = Wave(); await using var mock = new MockSoundpad { CategoryXml = MockSoundpad.Categories.Replace("</Categories>", "<Category index=\"9\" name=\"temp\"><Sound index=\"1\" url=\"C:\\Private\\airhorn.wav\"/></Category></Categories>") }; using var pipe = new SoundpadPipe(mock.Name);
        var audio = new SessionAudio(folder); var entry = await audio.Prepare(input, "Temp", "Guest", false); await audio.Import(pipe, entry);
        // New manager simulates a restarted companion reading its recovery journal.
        await new SessionAudio(folder).Cleanup(pipe); Assert.False(File.Exists(audio.PathFor(entry))); Assert.Contains("airhorn.wav", mock.CategoryXml); Assert.DoesNotContain(mock.Commands, c => c.StartsWith("DoAddCategory(")); Assert.Equal(4, System.Xml.Linq.XDocument.Parse(mock.AllXml).Descendants("Sound").Count());
    }
    [Fact]
    public async Task GuestCannotEnableSpotifyAndHostCanRestrictGuestTransport()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder));
        Assert.False((await bridge.Execute(Cmd("spotify") with { Action = "enable" }, "guest")).Ok);
        Assert.True((await bridge.Execute(Cmd("spotify") with { Action = "status" }, "guest")).Ok);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestSpotify: false) }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("spotify") with { Action = "next" }, "guest")).Ok);
    }
    [Fact]
    public async Task UploadEndpointRequiresExactOriginAndTokenAndRejectsUnsupportedAudio()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        await using var server = new LocalServer(bridge, "https://example.test"); await server.Start(); using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/session-audio") { Content = new ByteArrayContent([1, 2, 3]) };
        request.Headers.Add("Origin", "https://example.test");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        Assert.False((await bridge.Upload([1, 2, 3], "file.exe", "Guest")).Ok);
        Assert.Equal(4, bridge.Snapshot.Library.Sounds.Length);
    }
    [Fact]
    public async Task WaveformSnapshotReturnsImmediatelyAndBroadcastsReadyWithoutSecondDecode()
    {
        var input = Wave(); using var waves = new Waveforms(folder); var id = new string('a', 64); var complete = new TaskCompletionSource<Waveform>();
        waves.Changed += w => { if (w.Status == "ready") complete.TrySetResult(w); };
        var initial = waves.GetSnapshot(id, input); Assert.Contains(initial.Status, new[] { "loading", "ready" });
        var ready = initial.Status == "ready" ? initial : await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ready", waves.GetSnapshot(id, input).Status); Assert.Equal(1, waves.DecodeCount); Assert.Equal(ready.Version, waves.GetSnapshot(id, input).Version);
    }
}
