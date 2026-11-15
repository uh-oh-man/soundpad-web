using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>2.44: the guided setup's setup.json, upgrade detection and "Remember this output".</summary>
public sealed class SetupTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-setup-test-" + Guid.NewGuid().ToString("N"));
    public SetupTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private string SetupFile => Path.Combine(folder, "setup.json");
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static Command SetupSet(string action) => Cmd("setupSet") with { Body = JsonDocument.Parse($"{{\"action\":\"{action}\"}}").RootElement.Clone() };
    private static JsonElement Data(Reply reply) => JsonSerializer.SerializeToElement(reply.Data ?? new { }, Json.Options);

    /// <summary>A capture fixture that also lists an app and (wrongly) an input, to prove only outputs are remembered.</summary>
    private sealed class ManySources : IMimicCapture
    {
        private readonly MimicFixture inner = new();
        public int Rate => MimicFixture.Rate;
        public IReadOnlyList<MimicSource> Sources() => [MimicFixture.Source, new("app:1234", "App: Game [1234]", "app"), new("input:mic", "Microphone", "input")];
        public Task Start(MimicSource source, IMimicSink sink) => inner.Start(source, sink);
        public void Stop() => inner.Stop();
        public void Pump() => inner.Pump();
        public bool Running => inner.Running;
        public string Status => inner.Status;
        public string? SourceName => inner.SourceName;
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public void SetupFileRulesNotDoneUntilFinishedDamagedLeftUntouchedUnknownKept()
    {
        var store = new SetupStore(SetupFile);
        Assert.False(store.Read().Done); Assert.False(store.Damaged); Assert.False(File.Exists(SetupFile));
        var done = store.Set("done", "Host", 1234);
        Assert.True(done.Done); Assert.Equal(1234, done.CompletedAt); Assert.Equal("Host", done.CompletedBy);
        Assert.True(new SetupStore(SetupFile).Read().Done);
        Assert.False(new SetupStore(SetupFile).Set("reset", "Host", 5).Done);
        Assert.True(new SetupStore(SetupFile).Set("dismiss", "Host", 6).Dismissed);
        Assert.True(new SetupStore(SetupFile).Read().Done); // dismissed counts as done for showing the guide
        Assert.Throws<InvalidDataException>(() => new SetupStore(SetupFile).Set("sideways", "Host", 7));
        foreach (var damaged in new[] { "{ nope", "[]", "{\"version\":2,\"completedAt\":1}", "{\"version\":1,\"completedAt\":\"yesterday\"}", "{\"version\":1,\"dismissed\":1}",
            "{\"version\":1,\"completedAt\":1,\"x\":\"" + new string('a', 5000) + "\"}" })
        {
            File.WriteAllText(SetupFile, damaged);
            var read = new SetupStore(SetupFile);
            Assert.False(read.Read().Done, damaged); Assert.True(read.Damaged, damaged);
            Assert.Equal(damaged, File.ReadAllText(SetupFile));
        }
        File.WriteAllText(SetupFile, "{\"version\":1,\"dismissed\":false,\"newer\":{\"steps\":[1]}}");
        new SetupStore(SetupFile).Set("done", "Host", 9);
        using var file = JsonDocument.Parse(File.ReadAllText(SetupFile));
        Assert.Equal("[1]", file.RootElement.GetProperty("newer").GetProperty("steps").GetRawText());
        Assert.Equal(9, file.RootElement.GetProperty("completedAt").GetInt64());
    }

    [Fact]
    public async Task SetupOpsAndTheSnapshotTellAFirstRunFromAnUpgrade()
    {
        await using (var mock = new MockSoundpad())
        {
            using var pipe = new SoundpadPipe(mock.Name, true);
            using var bridge = new Bridge(pipe, new StateStore(folder)); bridge.UseMimicCapture(new MimicFixture()); bridge.UseHostHotkeys(c => new HotkeyFixture(c.Text));
            await bridge.Poll(true); await bridge.StartupCleanup();
            Assert.False(bridge.Snapshot.SetupDone);
            Assert.Contains("\"setupDone\":false", Json.Write(bridge.Snapshot));
            var get = Data(await bridge.Execute(Cmd("setupGet"), "host"));
            Assert.False(get.GetProperty("done").GetBoolean()); Assert.False(get.GetProperty("existing").GetBoolean());
            Assert.True(get.GetProperty("soundpad").GetProperty("available").GetBoolean());
            Assert.Equal(JsonValueKind.Null, get.GetProperty("soundpad").GetProperty("problem").ValueKind);
            Assert.Equal(MimicFixture.Source.Id, get.GetProperty("outputs")[0].GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, get.GetProperty("remembered").ValueKind);
            var finished = await bridge.Execute(SetupSet("done"), "host");
            Assert.True(finished.Ok, finished.Error); Assert.True(bridge.Snapshot.SetupDone);
            Assert.False((await bridge.Execute(SetupSet("finish"), "host")).Ok);
            Assert.False((await bridge.Execute(Cmd("setupSet") with { Body = JsonDocument.Parse("{\"action\":\"done\",\"x\":1}").RootElement.Clone() }, "host")).Ok);
            // The Kick counter is kept data: the next launch is an upgrade-like install.
            Assert.True((await bridge.Execute(Cmd("counter") with { Delta = 1 }, "host")).Ok);
            Assert.True((await bridge.Execute(SetupSet("reset"), "host")).Ok); Assert.False(bridge.Snapshot.SetupDone);
            // Decided at startup: this launch began without kept data, and says so until it restarts.
            Assert.False(Data(await bridge.Execute(Cmd("setupGet"), "host")).GetProperty("existing").GetBoolean());
        }
        await using (var mock = new MockSoundpad())
        {
            using var pipe = new SoundpadPipe(mock.Name, true);
            using var bridge = new Bridge(pipe, new StateStore(folder)); bridge.UseMimicCapture(new MimicFixture()); bridge.UseHostHotkeys(c => new HotkeyFixture(c.Text));
            await bridge.Poll(true); await bridge.StartupCleanup();
            var get = Data(await bridge.Execute(Cmd("setupGet"), "host"));
            Assert.True(get.GetProperty("existing").GetBoolean()); Assert.False(get.GetProperty("done").GetBoolean());
            Assert.True((await bridge.Execute(SetupSet("dismiss"), "host")).Ok);
            Assert.True(bridge.Snapshot.SetupDone);
            Assert.True(Data(await bridge.Execute(Cmd("setupGet"), "host")).GetProperty("dismissed").GetBoolean());
        }
    }

    [Fact]
    public void ExistingDataIsKeptDataOfAnyKind()
    {
        var disk = new DiskState();
        Assert.False(SetupStore.Existing(folder, disk, false));
        Assert.True(SetupStore.Existing(folder, disk, true));
        Assert.True(SetupStore.Existing(folder, new DiskState { Counter = 3 }, false));
        var stats = new DiskState(); stats.Statistics["x"] = new();
        Assert.True(SetupStore.Existing(folder, stats, false));
        foreach (var file in new[] { "ui.json", "keep-settings.json", Path.Combine("library", "voice-library.v1.json") })
        {
            var path = Path.Combine(folder, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
            Assert.True(SetupStore.Existing(folder, disk, false), file);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RememberSavesAnOutputWithoutListeningAndRefusesAnythingElse()
    {
        await using var mock = new MockSoundpad();
        using var pipe = new SoundpadPipe(mock.Name, true);
        using var bridge = new Bridge(pipe, new StateStore(folder));
        var capture = new ManySources(); bridge.UseMimicCapture(capture);
        await bridge.Poll(true);
        Command Remember(string id) => Cmd("mimicSources") with { Action = "remember", SourceId = id };
        Assert.False((await bridge.Execute(Remember("output:not-listed"), "host")).Ok);
        Assert.False((await bridge.Execute(Remember("input:mic"), "host")).Ok);
        var app = await bridge.Execute(Remember("app:1234"), "host");
        Assert.False(app.Ok); Assert.Contains("Choose an output", app.Error);
        Assert.False((await bridge.Execute(Cmd("mimicSources") with { Action = "other" }, "host")).Ok);
        Assert.False(File.Exists(Path.Combine(folder, "mimic-settings.json")));
        var ok = await bridge.Execute(Remember(MimicFixture.Source.Id), "host");
        Assert.True(ok.Ok, ok.Error);
        Assert.False(capture.Running); Assert.False(bridge.Snapshot.Mimic!.Active);
        Assert.Null(bridge.History); // nothing was recorded
        using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "mimic-settings.json"))))
        {
            Assert.Equal(MimicFixture.Source.Id, settings.RootElement.GetProperty("sourceId").GetString());
            Assert.Equal(1, settings.RootElement.GetProperty("format").GetInt32());
        }
        // mimicSources offers it again, and setupGet reports it.
        Assert.Equal(MimicFixture.Source.Id, Data(await bridge.Execute(Cmd("mimicSources"), "host")).GetProperty("remembered").GetString());
        Assert.Equal(MimicFixture.Source.Id, Data(await bridge.Execute(Cmd("setupGet"), "host")).GetProperty("remembered").GetString());
    }
}
