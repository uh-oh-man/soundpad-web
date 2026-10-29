using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class Tests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "soundpad-web-unit-" + Guid.NewGuid().ToString("N"));
    private StateStore Store => new(directory);
    private static Command Cmd(string op, string? sound = null, string? version = null, int? delta = null) => new(1, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op, sound, version, delta);
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    [Fact]
    public void XmlPreservesNestingOrderDuplicatesAndPrivacy()
    {
        var p = LibraryParser.Parse(MockSoundpad.Sounds, MockSoundpad.Categories, new());
        Assert.Equal(["Reactions", "Empty category", "Favorites"], p.Public.Categories.Select(c => c.Name));
        Assert.Equal("Deep cuts", p.Public.Categories[0].Children[0].Name);
        Assert.Empty(p.Public.Categories[1].SoundIds);
        Assert.Equal(2, p.Public.Sounds.Count(s => s.Name == "Laugh"));
        Assert.Equal(4, p.Public.Sounds.Select(s => s.Id).Distinct().Count());
        Assert.Equal(3, p.Indexes[p.Public.Categories[2].SoundIds[0]]);
        Assert.DoesNotContain("Private", Json.Write(p.Public));
    }
    [Fact] public void StableIdentitiesSurviveReordering() { var disk = new DiskState(); var a = LibraryParser.Parse(MockSoundpad.Sounds, MockSoundpad.Categories, disk); var b = LibraryParser.Parse(MockSoundpad.Sounds.Replace("index=\"1\"", "index=\"9\""), MockSoundpad.Categories.Replace("index=\"1\"", "index=\"9\""), disk); Assert.Equal(a.Public.Sounds[0].Id, b.Public.Sounds[0].Id); Assert.NotEqual(a.Public.Version, b.Public.Version); Assert.Equal(9, b.Indexes[b.Public.Sounds[0].Id]); }
    [Theory]
    [InlineData("<oops/>")]
    [InlineData("<Soundlist>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><Soundlist>&e;</Soundlist>")]
    public void RejectsBadXml(string xml) { Assert.ThrowsAny<Exception>(() => LibraryParser.Parse(xml, MockSoundpad.Categories, new())); }
    [Fact] public void RejectsMismatchedCategoryReference() { Assert.Throws<InvalidDataException>(() => LibraryParser.Parse(MockSoundpad.Sounds, MockSoundpad.Categories.Replace("C:\\Private\\laugh.wav", "C:\\Wrong.wav"), new())); }
    [Fact] public void PersistenceAndIconsSurviveReload() { var disk = new DiskState { Counter = 17 }; disk.Icons["opaque"] = "value"; Store.Save(disk); var reloaded = Store.Load(); Assert.Equal(17, reloaded.Counter); Assert.Equal(disk.IdentityKey, reloaded.IdentityKey); Assert.Equal("value", reloaded.Icons["opaque"]); Assert.False(File.Exists(Path.Combine(directory, "state.json.tmp"))); }
    [Fact] public void CorruptPersistenceDoesNotReset() { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "state.json"), "{"); Assert.Throws<InvalidDataException>(() => Store.Load()); Assert.Equal("{", File.ReadAllText(Path.Combine(directory, "state.json"))); }
    [Fact] public void RasterValidation() { Assert.Throws<InvalidDataException>(() => IconData.Normalize("data:image/svg+xml;base64,PHN2Zz4=")); Assert.Throws<InvalidDataException>(() => IconData.Normalize("data:image/png;base64," + new string('A', 180001))); using var bmp = new System.Drawing.Bitmap(12, 12); using var m = new MemoryStream(); bmp.Save(m, System.Drawing.Imaging.ImageFormat.Png); Assert.StartsWith("data:image/png;base64,", IconData.Normalize("data:image/png;base64," + Convert.ToBase64String(m.ToArray()))); }
    [Fact] public async Task RealPipeReadsLargeUtf8MessagesAndReconnects() { await using var mock = new MockSoundpad(); mock.AllXml = "<Soundlist>" + string.Concat(Enumerable.Repeat("é猫", 60000)) + "</Soundlist>"; using (var p = new SoundpadPipe(mock.Name)) { Assert.Equal(mock.AllXml, await p.Request("GetSoundlist()")); Assert.Equal("R-200", await p.Request("IsAlive()")); } await Task.Delay(50); using var p2 = new SoundpadPipe(mock.Name); Assert.Equal("R-200", await p2.Request("IsAlive()")); }
    [Fact] public async Task CounterDeduplicatesAcrossRestartAndEnforcesPermissions() { await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var b = new Bridge(pipe, Store); var c = Cmd("counter", delta: 1); Assert.False((await b.Execute(c, "guest")).Ok); Assert.True((await b.Execute(c, "host")).Ok); Assert.True((await b.Execute(c, "host")).Ok); Assert.Equal(1, b.Snapshot.Counter); var restarted = new Bridge(pipe, Store); Assert.True((await restarted.Execute(c, "host")).Ok); Assert.Equal(1, restarted.Snapshot.Counter); Assert.False((await b.Execute(Cmd("reset"), "guest")).Ok); Assert.False((await b.Execute(Cmd("record"), "host")).Ok); }
    [Fact] public async Task SerializesConcurrentCounterChanges() { await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var b = new Bridge(pipe, Store); var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => b.Execute(Cmd("counter", delta: 1), "host"))); Assert.All(results, r => Assert.True(r.Ok)); Assert.Equal(15, b.Snapshot.Counter); }
    [Fact] public async Task PlaybackUsesGlobalIndexAndRejectsStaleLibrary() { await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var b = new Bridge(pipe, Store); await b.Poll(true); var sound = b.Snapshot.Library.Sounds[2]; Assert.True((await b.Execute(Cmd("play", sound.Id, b.Snapshot.Library.Version), "guest")).Ok); Assert.Contains("DoPlaySound(3)", mock.Commands); var version = b.Snapshot.Library.Version; mock.AllXml = MockSoundpad.Sounds.Replace("index=\"3\"", "index=\"9\""); mock.CategoryXml = MockSoundpad.Categories.Replace("index=\"3\"", "index=\"9\""); Assert.False((await b.Execute(Cmd("play", sound.Id, version), "guest")).Ok); Assert.DoesNotContain("DoPlaySound(9)", mock.Commands); Assert.True((await b.Execute(Cmd("stop"), "host")).Ok); }
    [Fact] public async Task ExpiredCommandsRejected() { await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var b = new Bridge(pipe, Store); foreach (var at in new[] { 0L, long.MinValue, long.MaxValue }) Assert.False((await b.Execute(Cmd("counter", delta: 1) with { At = at }, "host")).Ok); Assert.Equal(0, b.Snapshot.Counter); }
    [Fact] public async Task IconsPersistThroughBridgeRestartAndGuestsCannotEdit() { await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var b = new Bridge(pipe, Store); await b.Poll(true); var id = b.Snapshot.Library.Sounds[0].Id; using var bmp = new System.Drawing.Bitmap(16, 16); using var data = new MemoryStream(); bmp.Save(data, System.Drawing.Imaging.ImageFormat.Png); var c = Cmd("icon", id) with { Image = "data:image/png;base64," + Convert.ToBase64String(data.ToArray()) }; Assert.False((await b.Execute(c, "guest")).Ok); Assert.True((await b.Execute(c, "host")).Ok); var restarted = new Bridge(pipe, Store); await restarted.Poll(true); Assert.StartsWith("data:image/png;base64,", restarted.Snapshot.Library.Sounds[0].Icon); }
    [Fact] public async Task LoopbackRejectsInvalidTokenAndBindsOnlyLoopback() { await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var b = new Bridge(pipe, Store); await using var server = new LocalServer(b, "https://example.test"); await server.Start(); var endpoints = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == server.Port).ToArray(); Assert.NotEmpty(endpoints); Assert.All(endpoints, e => Assert.True(System.Net.IPAddress.IsLoopback(e.Address))); using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Origin", "https://example.test"); await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/bridge"), CancellationToken.None); await ws.SendAsync(Encoding.UTF8.GetBytes("{\"token\":\"invalid\"}"), WebSocketMessageType.Text, true, CancellationToken.None); var r = await ws.ReceiveAsync(new byte[1000], CancellationToken.None); Assert.Equal(WebSocketMessageType.Close, r.MessageType); Assert.Equal(WebSocketCloseStatus.PolicyViolation, r.CloseStatus); }
    [Fact] public async Task UnavailablePipeHasBoundedTimeout() { using var p = new SoundpadPipe("not-soundpad-" + Guid.NewGuid()); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.Request("IsAlive()")); }
    [Fact] public async Task SoundpadClosureAndRestartRecoverWithoutResettingCounter() { var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var b = new Bridge(pipe, Store); await b.Poll(true); await b.Execute(Cmd("counter", delta: 1), "host"); var name = mock.Name; await mock.DisposeAsync(); await b.Poll(false); Assert.False(b.Snapshot.Available); await using var restarted = new MockSoundpad(name); await b.Poll(true); Assert.True(b.Snapshot.Available); Assert.Equal(1, b.Snapshot.Counter); }
    [Fact]
    public async Task LoopbackRequiresOriginTokenAndAcceptsAuthenticatedCommands()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var b = new Bridge(pipe, Store); await b.Poll(true);
        await using var server = new LocalServer(b, "https://example.test"); await server.Start();
        using (var wrong = new ClientWebSocket()) { wrong.Options.SetRequestHeader("Origin", "https://evil.test"); await Assert.ThrowsAsync<WebSocketException>(() => wrong.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/bridge"), CancellationToken.None)); }
        using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Origin", "https://example.test"); await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/bridge"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes(Json.Write(new { token = server.Token })), WebSocketMessageType.Text, true, CancellationToken.None);
        var bytes = new byte[50000]; var r = await ws.ReceiveAsync(bytes, CancellationToken.None); Assert.Contains("\"type\":\"state\"", Encoding.UTF8.GetString(bytes, 0, r.Count));
        await ws.SendAsync(Encoding.UTF8.GetBytes(Json.Write(new { actor = "host", command = Cmd("counter", delta: 1) })), WebSocketMessageType.Text, true, CancellationToken.None);
        r = await ws.ReceiveAsync(bytes, CancellationToken.None); Assert.Contains("\"ok\":true", Encoding.UTF8.GetString(bytes, 0, r.Count));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
