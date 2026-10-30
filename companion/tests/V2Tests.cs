using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class V2Tests : IDisposable
{
    [Theory]
    [InlineData("Spotify.exe", true)]
    [InlineData("Spotify", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", true)]
    [InlineData("chrome.exe", false)]
    [InlineData("NotSpotify.exe", false)]
    [InlineData("SpotifyFake.Player", false)]
    public void SpotifySelectionIgnoresEveryOtherMediaApplication(string source, bool expected) => Assert.Equal(expected, SpotifyControl.IsSpotifySource(source));
    [Fact]
    public async Task RapidSeeksKeepIdentityDuringDelayedPositionsAndTransientStops()
    {
        await using var mock = new MockSoundpad { DurationMs = 30000, SeekLagSamples = 4, TransientSeekStop = true }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, Store); await bridge.Poll(true); var lib = bridge.Snapshot.Library; var id = lib.Sounds[0].Id;
        Assert.True((await bridge.Execute(Cmd("play", id, lib.Version), "host")).Ok); var epoch = bridge.Snapshot.Playback.Epoch;
        foreach (var at in new long[] { 15000, 2000, 21000, 6000, 18000 })
        {
            Assert.True((await bridge.Execute(Cmd("seek", id, position: at, epoch: epoch), "guest")).Ok); await bridge.Poll(false); Assert.Equal(id, bridge.Snapshot.Playback.SoundId); Assert.Equal(epoch, bridge.Snapshot.Playback.Epoch);
        }
        Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok); Assert.Null(bridge.Snapshot.Playback.SoundId);
    }
    [Fact]
    public async Task ConcurrentGuestsCannotSeekTheSoundThatReplacedTheirEpoch()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, Store); await bridge.Poll(true); var lib = bridge.Snapshot.Library; var id = lib.Sounds[0].Id;
        Assert.True((await bridge.Execute(Cmd("play", id, lib.Version), "guest")).Ok); var epoch = bridge.Snapshot.Playback.Epoch;
        var seeks = await Task.WhenAll(bridge.Execute(Cmd("seek", id, position: 25, epoch: epoch), "guest"), bridge.Execute(Cmd("seek", id, position: 50, epoch: epoch), "guest")); Assert.All(seeks, r => Assert.True(r.Ok));
        await Task.WhenAll(bridge.Execute(Cmd("seek", id, position: 100, epoch: epoch), "guest"), bridge.Execute(Cmd("play", lib.Sounds[1].Id, lib.Version), "guest"));
        Assert.Equal(lib.Sounds[1].Id, bridge.Snapshot.Playback.SoundId); Assert.False((await bridge.Execute(Cmd("seek", id, position: 200, epoch: epoch), "guest")).Ok);
        Assert.True(mock.Commands.FindLastIndex(c => c.StartsWith("DoSeekMs(")) < mock.Commands.FindLastIndex(c => c == "DoPlaySound(2)"));
    }
    [Fact]
    public void FailedMigrationPreservesOriginalStateAndRecoveryCopy()
    {
        Store.Save(new DiskState()); var original = File.ReadAllBytes(Path.Combine(folder, "state.json"));
        using var bitmap = new System.Drawing.Bitmap(12, 12); using var image = new MemoryStream(); bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
        var legacy = new DiskState(); legacy.Icons[new('a', 64)] = "data:image/png;base64," + Convert.ToBase64String(image.ToArray()); Store.Save(legacy); original = File.ReadAllBytes(Path.Combine(folder, "state.json"));
        File.WriteAllText(Path.Combine(folder, "icons"), "fixture blocks directory creation");
        Assert.ThrowsAny<IOException>(() => Store.Load()); Assert.True(original.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "state.json")))); Assert.True(original.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "state.v1.backup.json"))));
    }
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-web-v2-" + Guid.NewGuid().ToString("N"));
    private StateStore Store => new(folder);
    public V2Tests() { Directory.CreateDirectory(folder); }
    public void Dispose() { Directory.Delete(folder, true); }
    private static Command Cmd(string op, string? id = null, string? version = null, long? position = null, string? epoch = null, long? end = null, bool repeat = false) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op, id, version, PositionMs: position, Epoch: epoch, EndMs: end, Repeat: repeat);
    private static void WriteWave(string path, int milliseconds = 1000) { using var output = new BinaryWriter(File.Create(path)); int frames = 48000 * milliseconds / 1000; output.Write("RIFF"u8); output.Write(36 + frames * 4); output.Write("WAVEfmt "u8); output.Write(16); output.Write((short)1); output.Write((short)2); output.Write(48000); output.Write(48000 * 4); output.Write((short)4); output.Write((short)16); output.Write("data"u8); output.Write(frames * 4); for (int i = 0; i < frames; i++) { short sample = i < frames / 4 ? (short)0 : (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 25000); output.Write(sample); output.Write((short)-sample); } }
    [Fact]
    public async Task RealWaveformsRespectStereoTimingCacheAndInvalidation()
    {
        var path = Path.Combine(folder, "tone.wav"); WriteWave(path); string id = new('a', 64);
        using (var waves = new Waveforms(folder)) { var data = await waves.Request(id, path); Assert.Equal("ready", data.Status); Assert.Equal(1000, data.DurationMs); Assert.Equal(3, data.Levels!.Length); Assert.All(data.Levels, l => { Assert.Equal(l.Bins * 2, l.Peaks.Length); Assert.Contains(l.Peaks, p => p < 0); Assert.Contains(l.Peaks, p => p > 0); Assert.All(l.Peaks.Take(l.Bins / 4), v => Assert.Equal(0, v)); }); Assert.Equal(1, waves.DecodeCount); Assert.Equal(data.Version, (await waves.Request(id, path)).Version); Assert.Equal(1, waves.DecodeCount); }
        using (var waves = new Waveforms(folder)) { Assert.Equal("ready", (await waves.Request(id, path)).Status); Assert.Equal(0, waves.DecodeCount); WriteWave(path, 2000); Assert.Equal(2000, (await waves.Request(id, path)).DurationMs); Assert.Equal(1, waves.DecodeCount); }
    }
    [Fact] public async Task MissingAndCorruptAudioHaveHonestUnavailableState() { using var waves = new Waveforms(folder); Assert.Equal("unavailable", (await waves.Request(new('b', 64), Path.Combine(folder, "missing.wav"))).Status); var path = Path.Combine(folder, "bad.wav"); File.WriteAllText(path, "not audio"); Assert.Equal("unavailable", (await waves.Request(new('b', 64), path)).Status); }
    [Fact]
    public async Task CorruptWaveformCacheIsRebuiltAndReused()
    {
        var path = Path.Combine(folder, "tone.wav"); WriteWave(path); string id = new('a', 64);
        using (var waves = new Waveforms(folder))
        {
            var first = await waves.Request(id, path); File.WriteAllText(Path.Combine(folder, "waveforms", first.Version + ".br"), "corrupt");
            Assert.Equal("ready", (await waves.Request(id, path)).Status); Assert.Equal(2, waves.DecodeCount);
            Assert.Equal("ready", (await waves.Request(id, path)).Status); Assert.Equal(2, waves.DecodeCount);
        }
    }
    [Fact]
    public async Task MediaFoundationDecodesGeneratedMp3WithoutAudioOutput()
    {
        var path = Path.Combine(folder, "tone.wav"); var mp3 = Path.Combine(folder, "tone.mp3"); WriteWave(path);
        await Task.Run(() => { using var reader = new NAudio.Wave.WaveFileReader(path); NAudio.Wave.MediaFoundationEncoder.EncodeToMp3(reader, mp3, 128000); });
        using var waves = new Waveforms(folder); var data = await waves.Request(new('a', 64), mp3);
        Assert.Equal("ready", data.Status); Assert.InRange(data.DurationMs!.Value, 900, 1300); Assert.Contains(data.Levels![0].Peaks, p => p > 0);
    }
    [Fact]
    public async Task ModernLoopbackSendsLightweightTicksAfterTheFullLibrary()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, Store); await bridge.Poll(true);
        await using var server = new LocalServer(bridge, "https://example.test"); await server.Start();
        using var socket = new System.Net.WebSockets.ClientWebSocket(); socket.Options.SetRequestHeader("Origin", "https://example.test"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/bridge"), timeout.Token);
        await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(Json.Write(new { token = server.Token, protocol = 2 })), System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[50000]; var first = await socket.ReceiveAsync(buffer, timeout.Token); using (var state = JsonDocument.Parse(buffer.AsMemory(0, first.Count))) Assert.Equal("state", state.RootElement.GetProperty("type").GetString());
        await bridge.Poll(false); var next = await socket.ReceiveAsync(buffer, timeout.Token); using (var tick = JsonDocument.Parse(buffer.AsMemory(0, next.Count))) { Assert.Equal("tick", tick.RootElement.GetProperty("type").GetString()); Assert.Equal(JsonValueKind.Null, tick.RootElement.GetProperty("state").GetProperty("library").ValueKind); }
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }
    [Fact]
    public void V1MigrationPreservesCounterPermissionsKeysAndIcons()
    {
        using var bitmap = new System.Drawing.Bitmap(12, 12); using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); string id = new('a', 64), image = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        var original = new DiskState { Counter = 42, Permissions = new(false, true), Website = "https://example.test/" }; original.Icons[id] = image; original.Receipts[Guid.NewGuid().ToString()] = 123; Store.Save(original);
        var state = Store.Load(); Assert.Equal(2, state.SchemaVersion); Assert.Equal(42, state.Counter); Assert.Equal(original.IdentityKey, state.IdentityKey); Assert.Equal(original.Permissions, state.Permissions); Assert.Equal(original.Receipts, state.Receipts); Assert.Empty(state.Icons); Assert.True(File.Exists(Path.Combine(folder, "state.v1.backup.json"))); Assert.Contains("data:image/png", File.ReadAllText(Path.Combine(folder, "state.v1.backup.json"))); Assert.StartsWith("data:image/png", Store.ReadIcons(state)[id]); Assert.Equal(state.IconFiles, Store.Load().IconFiles);
    }
    [Fact] public void NativeStatisticsMapOnlyVerifiedHashesAndPreserveDatePrecision() { File.WriteAllText(Path.Combine(folder, "stats.spl"), "<Stats><Sound hash=\"opaque-1\" playCount=\"19\" lastPlayedOn=\"2026-10-29\"/></Stats>"); File.WriteAllText(Path.Combine(folder, "working.spl"), "<Soundlist><Sound url=\"C:\\Sounds\\one.wav\" hash=\"opaque-1\"/></Soundlist>"); var stats = new NativeStatistics(folder); stats.Refresh(); Assert.Equal(new NativeStat(19, "2026-10-29"), stats.Find(@"C:\Sounds\one.wav", "")); Assert.Null(stats.Find(@"C:\Sounds\other.wav", "")); File.WriteAllText(Path.Combine(folder, "stats.spl"), "<broken/>"); stats.Refresh(); Assert.Null(stats.Find(@"C:\Sounds\one.wav", "")); }
    [Fact]
    public async Task SeeksAreEpochBoundClampedPermissionCheckedAndNotCountedAsPlays()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, Store); await bridge.Poll(true); var library = bridge.Snapshot.Library; var id = library.Sounds[0].Id;
        var play = Cmd("play", id, library.Version); Assert.True((await bridge.Execute(play, "guest", new(Guid.NewGuid().ToString(), "Alice"))).Ok); Assert.True((await bridge.Execute(play, "guest")).Ok); var epoch = bridge.Snapshot.Playback.Epoch; Assert.NotNull(epoch);
        Assert.True((await bridge.Execute(Cmd("seek", id, position: 1000, epoch: epoch), "guest")).Ok); Assert.Contains("DoSeekMs(1000)", mock.Commands);
        Assert.True((await bridge.Execute(Cmd("pause", id, epoch: epoch), "guest")).Ok); Assert.Equal("PAUSED", bridge.Snapshot.Playback.Status);
        var stats = await bridge.Execute(Cmd("statistics", id), "guest"); using (var json = JsonDocument.Parse(Json.Write(stats.Data))) Assert.Equal(1, json.RootElement.GetProperty("statistics").GetProperty("total").GetInt64());
        Assert.True((await bridge.Execute(Cmd("play", library.Sounds[1].Id, library.Version), "host")).Ok); Assert.False((await bridge.Execute(Cmd("seek", id, position: 100, epoch: epoch), "guest")).Ok);
        await bridge.Execute(Cmd("permissions") with { Permissions = new(true, false, false) }, "host"); Assert.False((await bridge.Execute(Cmd("seek", bridge.Snapshot.Playback.SoundId, position: 100, epoch: bridge.Snapshot.Playback.Epoch), "guest")).Ok);
    }
    [Fact] public async Task RegionEndsAndCompetingPlayCancelsOldBoundary() { await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, Store); await bridge.Poll(true); var lib = bridge.Snapshot.Library; Assert.True((await bridge.Execute(Cmd("region", lib.Sounds[0].Id, lib.Version, 100, end: 250), "host")).Ok); await Task.Delay(200); await bridge.Poll(false); Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status); Assert.True((await bridge.Execute(Cmd("region", lib.Sounds[0].Id, lib.Version, 100, end: 250), "host")).Ok); Assert.True((await bridge.Execute(Cmd("play", lib.Sounds[1].Id, lib.Version), "host")).Ok); await Task.Delay(200); await bridge.Poll(false); Assert.Equal(lib.Sounds[1].Id, bridge.Snapshot.Playback.SoundId); }
    [Fact] public async Task WaveformQueriesCannotReadAnArbitraryPathAndSpotifyIsHostOnly() { await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, Store); Assert.False((await bridge.Execute(Cmd("waveform", @"C:\secret.wav"), "guest")).Ok); Assert.False((await bridge.Execute(Cmd("spotify"), "guest")).Ok); var reply = await bridge.Execute(Cmd("spotify") with { Action = "status" }, "host"); Assert.True(reply.Ok); Assert.Contains("disabled", Json.Write(reply.Data)); }
    [Fact]
    public async Task ContradictoryNativePlaybackClearsIdentityAndRejectsTheOldEpoch()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, Store); await bridge.Poll(true); var lib = bridge.Snapshot.Library; var id = lib.Sounds[0].Id;
        Assert.True((await bridge.Execute(Cmd("play", id, lib.Version), "host")).Ok); var epoch = bridge.Snapshot.Playback.Epoch;
        Assert.True((await bridge.Execute(Cmd("seek", id, position: 100, epoch: epoch), "host")).Ok); Assert.Equal(1, bridge.Snapshot.Playback.PositionRevision);
        mock.DurationMs = 5000; await bridge.Poll(false); Assert.Null(bridge.Snapshot.Playback.SoundId); Assert.Equal("unknown", bridge.Snapshot.Playback.IdentitySource);
        Assert.False((await bridge.Execute(Cmd("seek", id, position: 200, epoch: epoch), "host")).Ok);
    }
    [Fact]
    public async Task RegionRepeatsDoNotCountAgainAndStatisticsSurviveRestart()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, Store); await bridge.Poll(true); var lib = bridge.Snapshot.Library; var id = lib.Sounds[0].Id;
        Assert.True((await bridge.Execute(Cmd("region", id, lib.Version, 100, end: 250, repeat: true), "guest", new(Guid.NewGuid().ToString(), "Alice"))).Ok);
        await Task.Delay(200); await bridge.Poll(false); Assert.Equal("PLAYING", bridge.Snapshot.Playback.Status); Assert.True(bridge.Snapshot.Playback.PositionRevision >= 2);
        using var restarted = new Bridge(pipe, Store); var reply = await restarted.Execute(Cmd("statistics", id), "guest"); using var json = JsonDocument.Parse(Json.Write(reply.Data)); Assert.Equal(1, json.RootElement.GetProperty("statistics").GetProperty("total").GetInt64());
        Assert.True((await restarted.Execute(Cmd("counter") with { Delta = 1 }, "host")).Ok); Assert.True((await restarted.Execute(Cmd("clearStatistics"), "host")).Ok); Assert.Equal(1, restarted.Snapshot.Counter);
    }
}
