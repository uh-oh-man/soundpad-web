using System.Runtime.CompilerServices;
using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Runs before any test: no test can register a real Windows hotkey on this PC (2.44).</summary>
internal static class NoRealHotkeys
{
    [ModuleInitializer]
    internal static void Forbid() => HostHotkeys.ForbidWindowsHotkeys = true;
}

/// <summary>
/// 2.44: host-wide hotkeys (Capture, Panic, Stop playback). Every key is a fixture; the Windows factory refuses
/// under test (see NoRealHotkeys), and a test that reached it would fail with "not registered in tests".
/// </summary>
public sealed class HostHotkeysTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-hotkeys-test-" + Guid.NewGuid().ToString("N"));
    public HostHotkeysTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private string HotkeysFile => Path.Combine(folder, "hotkeys.json");
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static Command Set(string body) => Cmd("hotkeysSet") with { Body = JsonDocument.Parse(body).RootElement.Clone() };
    private static JsonElement Data(Reply reply) => JsonSerializer.SerializeToElement(reply.Data ?? new { }, Json.Options);
    private static string State(Reply reply, string action) => Data(reply).GetProperty("status").GetProperty(action).GetProperty("state").GetString()!;
    private static string? Message(Reply reply, string action) => Data(reply).GetProperty("status").GetProperty(action).GetProperty("message").GetString();
    private static string? Bound(Reply reply, string action) => Data(reply).GetProperty("bindings").GetProperty(action).GetString();

    /// <summary>A bridge whose host-wide keys are fixtures (one per chord); <paramref name="taken"/> plays keys another app owns.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly MockSoundpad Mock = new() { DurationMs = 60000 };
        public readonly SoundpadPipe Pipe;
        public readonly Bridge Bridge;
        public readonly Dictionary<string, HotkeyFixture> Keys = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, HotkeyFixture> MimicKeys = [];
        public readonly HashSet<string> Taken = new(StringComparer.OrdinalIgnoreCase);
        public int Made;
        public readonly MimicFixture Capture = new();
        public Rig(string folder, bool noHotkeys = false)
        {
            Pipe = new(Mock.Name, true);
            Bridge = new(Pipe, new StateStore(folder));
            Bridge.UseHostHotkeys(chord => { Made++; return Keys[chord.Text] = new HotkeyFixture(chord.Text) { Taken = Taken.Contains(chord.Text) }; });
            Bridge.UseHotkeys(name => MimicKeys[name] = new HotkeyFixture(name));
            Bridge.UseMimicCapture(Capture); Bridge.UseTranscriptEngine(new TranscriptFixture());
            Bridge.UseNoHotkeys(noHotkeys);
        }
        public async Task Start() { await Bridge.Poll(true); await Bridge.StartupCleanup(); }
        public async ValueTask DisposeAsync() { Bridge.Dispose(); Pipe.Dispose(); await Mock.DisposeAsync(); }
    }

    [Fact]
    public void ChordsParseFormatAndMapToRegisterHotKeyFlags()
    {
        var chord = HotkeyChord.Parse(" alt + ctrl + f9 ");
        Assert.Equal("Ctrl+Alt+F9", chord.Text); Assert.Equal(0x3u, chord.Modifiers); Assert.Equal(0x78u, chord.Vk);
        Assert.Equal(("Shift+Win+Num5", 0xCu, 0x65u), (HotkeyChord.Parse("Win+Shift+Num5").Text, HotkeyChord.Parse("Win+Shift+Num5").Modifiers, HotkeyChord.Parse("Win+Shift+Num5").Vk));
        Assert.Equal(0x41u, HotkeyChord.Parse("Ctrl+a").Vk); Assert.Equal("Ctrl+A", HotkeyChord.Parse("Ctrl+a").Text);
        Assert.Equal(0x87u, HotkeyChord.Parse("F24").Vk);
        Assert.True(HotkeyChord.Parse("F7").BareFunctionKey); Assert.False(HotkeyChord.Parse("Ctrl+F7").BareFunctionKey);
        foreach (var bare in new[] { "Pause", "ScrollLock", "F1", "Ctrl+PageDown", "Alt+Home", "Win+Num0", "Ctrl+Alt+F4" }) Assert.True(HotkeyChord.TryParse(bare, out _), bare);
        foreach (var refused in new[] { "", " ", "A", "Shift+A", "7", "Home", "Shift+Insert", "F12", "Ctrl+F12", "Alt+F4", "Win+L", "Win+D", "Win+X", "Escape", "Space", "Enter", "Tab",
            "Ctrl+Escape", "Ctrl+Ctrl+A", "Ctrl+", "+A", "Hyper+A", "Ctrl+Delete", new string('x', 41) })
            Assert.False(HotkeyChord.TryParse(refused, out _), refused);
        Assert.Contains("Add Ctrl, Alt or Win", Assert.Throws<InvalidDataException>(() => HotkeyChord.Parse("Shift+A")).Message);
    }

    [Fact]
    public async Task DefaultsRegisterNothingAndTheWindowsFactoryNeverRunsUnderTest()
    {
        Assert.True(HostHotkeys.ForbidWindowsHotkeys);
        Assert.Throws<InvalidOperationException>(() => HostHotkeys.WindowsFactory(HotkeyChord.Parse("Ctrl+Alt+F9")));
        await using var rig = new Rig(folder); await rig.Start();
        Assert.Equal(0, rig.Made);
        var get = await rig.Bridge.Execute(Cmd("hotkeysGet"), "host");
        Assert.True(get.Ok, get.Error);
        foreach (var action in HostHotkeys.Actions) { Assert.Null(Bound(get, action)); Assert.Equal("off", State(get, action)); }
        Assert.Equal("F6", Data(get).GetProperty("mimicKey").GetString());
        Assert.False(File.Exists(HotkeysFile));
        // A bridge that never had a fixture factory set uses the Windows one, which refuses under test.
        Directory.CreateDirectory(Path.Combine(folder, "plain"));
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var plain = new Bridge(pipe, new StateStore(Path.Combine(folder, "plain")));
        await plain.Poll(true);
        var refused = await plain.Execute(Set("{\"panic\":\"Ctrl+Alt+P\"}"), "host");
        Assert.True(refused.Ok); Assert.Equal("invalid", State(refused, "panic")); Assert.Contains("not registered in tests", Message(refused, "panic"));
        Assert.False(File.Exists(Path.Combine(folder, "plain", "hotkeys.json")));
    }

    [Fact]
    public async Task SettingAKeyRegistersAndPersistsAndARestartRegistersItAgain()
    {
        await using (var rig = new Rig(folder))
        {
            await rig.Start();
            var set = await rig.Bridge.Execute(Set("{\"capture\":\"ctrl+alt+c\"}"), "host");
            Assert.True(set.Ok, set.Error);
            Assert.Equal("Ctrl+Alt+C", Bound(set, "capture")); Assert.Equal("on", State(set, "capture"));
            Assert.True(rig.Keys["Ctrl+Alt+C"].Active);
            Assert.True(rig.Bridge.Snapshot.Hotkeys > 0);
            using var file = JsonDocument.Parse(File.ReadAllText(HotkeysFile));
            Assert.Equal(1, file.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("Ctrl+Alt+C", file.RootElement.GetProperty("capture").GetString());
            Assert.Equal(JsonValueKind.Null, file.RootElement.GetProperty("panic").ValueKind);
            // Turning it off releases it.
            var off = await rig.Bridge.Execute(Set("{\"capture\":null,\"stop\":\"Ctrl+Alt+S\"}"), "host");
            Assert.True(off.Ok, off.Error); Assert.False(rig.Keys["Ctrl+Alt+C"].Active); Assert.Equal("off", State(off, "capture"));
            Assert.True(rig.Keys["Ctrl+Alt+S"].Active);
        }
        Assert.Contains("\"stop\":\"Ctrl+Alt+S\"", File.ReadAllText(HotkeysFile));
        var restarted = new Rig(folder); await restarted.Start();
        Assert.True(restarted.Keys["Ctrl+Alt+S"].Active);
        Assert.Equal("on", State(await restarted.Bridge.Execute(Cmd("hotkeysGet"), "host"), "stop"));
        // Quitting releases every key.
        await restarted.DisposeAsync();
        Assert.False(restarted.Keys["Ctrl+Alt+S"].Active);
    }

    [Fact]
    public async Task ATakenKeyKeepsThePreviousBindingAndTheFile()
    {
        await using var rig = new Rig(folder); await rig.Start();
        Assert.True((await rig.Bridge.Execute(Set("{\"panic\":\"Ctrl+Alt+P\"}"), "host")).Ok);
        var before = File.ReadAllText(HotkeysFile);
        rig.Taken.Add("Ctrl+Alt+F9");
        var taken = await rig.Bridge.Execute(Set("{\"panic\":\"Ctrl+Alt+F9\"}"), "host");
        Assert.True(taken.Ok, taken.Error);
        Assert.Equal("taken", State(taken, "panic"));
        Assert.Contains("Ctrl+Alt+F9 is already used by another app", Message(taken, "panic"));
        Assert.Contains("Ctrl+Alt+P, still works", Message(taken, "panic"));
        Assert.Equal("Ctrl+Alt+P", Bound(taken, "panic"));
        Assert.True(rig.Keys["Ctrl+Alt+P"].Active); Assert.False(rig.Keys["Ctrl+Alt+F9"].Active);
        Assert.Equal(before, File.ReadAllText(HotkeysFile));
        // A key taken at startup is reported, and kept in the file for the next launch.
        File.WriteAllText(HotkeysFile, "{\"version\":1,\"capture\":\"Ctrl+Alt+F9\"}");
        await using var next = new Rig(Path.Combine(folder)); next.Taken.Add("Ctrl+Alt+F9");
        await next.Start();
        var get = await next.Bridge.Execute(Cmd("hotkeysGet"), "host");
        Assert.Equal("taken", State(get, "capture")); Assert.Equal("Ctrl+Alt+F9", Bound(get, "capture"));
        Assert.Equal("{\"version\":1,\"capture\":\"Ctrl+Alt+F9\"}", File.ReadAllText(HotkeysFile));
    }

    [Fact]
    public async Task OneKeyForTwoActionsAndInvalidChordsAreRefusedAndChangeNothing()
    {
        await using var rig = new Rig(folder); await rig.Start();
        Assert.True((await rig.Bridge.Execute(Set("{\"panic\":\"Shift+Pause\"}"), "host")).Ok);
        var twice = await rig.Bridge.Execute(Set("{\"capture\":\"shift+pause\"}"), "host");
        Assert.False(twice.Ok); Assert.Equal("Shift+Pause is already Panic.", twice.Error);
        var both = await rig.Bridge.Execute(Set("{\"capture\":\"Ctrl+Alt+K\",\"stop\":\"Ctrl+Alt+K\"}"), "host");
        Assert.False(both.Ok); Assert.Contains("Ctrl+Alt+K is already", both.Error);
        var invalid = await rig.Bridge.Execute(Set("{\"capture\":\"Ctrl+Alt+K\",\"stop\":\"Win+L\"}"), "host");
        Assert.False(invalid.Ok); Assert.StartsWith("Stop playback: Windows uses Win+L", invalid.Error);
        Assert.False(rig.Keys.ContainsKey("Ctrl+Alt+K"));
        foreach (var bad in new[] { "{\"other\":\"Ctrl+Alt+K\"}", "{\"capture\":5}", "[]", "{}", "{\"capture\":\"Ctrl+Alt+K\",\"capture\":\"Ctrl+Alt+J\"}" })
            Assert.False((await rig.Bridge.Execute(Set(bad), "host")).Ok, bad);
        Assert.False((await rig.Bridge.Execute(Cmd("hotkeysSet"), "host")).Ok);
        Assert.Contains("\"panic\":\"Shift+Pause\"", File.ReadAllText(HotkeysFile));
    }

    [Fact]
    public async Task TheMimicKeyAndAHostWideKeyNeverCollideEitherWay()
    {
        await using var rig = new Rig(folder); await rig.Start();
        Assert.True((await rig.Bridge.Execute(Cmd("beginSession"), "host")).Ok);
        // F6 is the Mimic key (selected, not even on): refused for host-wide keys.
        var clash = await rig.Bridge.Execute(Set("{\"capture\":\"F6\"}"), "host");
        Assert.False(clash.Ok); Assert.Contains("F6 is the Mimic hotkey's key", clash.Error);
        Assert.True((await rig.Bridge.Execute(Set("{\"capture\":\"Ctrl+F6\",\"panic\":\"F8\"}"), "host")).Ok); // with a modifier it is another key
        // The reverse: Mimic can't move to a host-wide key.
        var reverse = await rig.Bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F8" }, "host");
        Assert.False(reverse.Ok); Assert.Contains("F8 is one of your host-wide hotkeys", reverse.Error);
        Assert.Equal("F6", rig.Bridge.Snapshot.Mimic!.HotkeyKey);
        Assert.True((await rig.Bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F7" }, "host")).Ok);
        // A stored key equal to the Mimic key at startup stays off until Mimic moves.
        Assert.True((await rig.Bridge.Execute(Set("{\"panic\":\"F6\"}"), "host")).Ok); // Mimic is on F7 now
        await using var next = new Rig(Path.Combine(folder)); await next.Start();
        Assert.Equal("conflict", State(await next.Bridge.Execute(Cmd("hotkeysGet"), "host"), "panic"));
        Assert.False(next.Keys.ContainsKey("F6"));
        Assert.True((await next.Bridge.Execute(Cmd("beginSession"), "host")).Ok);
        Assert.True((await next.Bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F9" }, "host")).Ok);
        Assert.Equal("on", State(await next.Bridge.Execute(Cmd("hotkeysGet"), "host"), "panic"));
        Assert.True(next.Keys["F6"].Active);
    }

    [Fact]
    public async Task GuestsCanNeitherReadNorChangeSetupOrHotkeys()
    {
        await using var rig = new Rig(folder); await rig.Start();
        Assert.True((await rig.Bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host")).Ok);
        var guest = new Participant(Guid.NewGuid().ToString(), "Alice");
        foreach (var command in new[] { Set("{\"panic\":\"Ctrl+Alt+P\"}"), Cmd("hotkeysGet"), Cmd("setupGet"), Cmd("setupSet") with { Body = JsonDocument.Parse("{\"action\":\"done\"}").RootElement.Clone() },
            Cmd("mimicSources") with { Action = "remember", SourceId = MimicFixture.Source.Id } })
        {
            var reply = await rig.Bridge.Execute(command, "guest", guest, "peer-alice");
            Assert.False(reply.Ok, command.Op); Assert.NotNull(reply.Error);
        }
        Assert.False(File.Exists(HotkeysFile)); Assert.False(File.Exists(Path.Combine(folder, "setup.json")));
        Assert.False(File.Exists(Path.Combine(folder, "mimic-settings.json")));
    }

    [Fact]
    public async Task CaptureStartsOnTheRememberedOutputAndStopsAgain()
    {
        await using var rig = new Rig(folder); await rig.Start();
        Assert.True((await rig.Bridge.Execute(Set("{\"capture\":\"Ctrl+Alt+C\"}"), "host")).Ok);
        // Nothing remembered yet: the press says so and starts nothing.
        rig.Keys["Ctrl+Alt+C"].Press(); await rig.Bridge.LastHostPress;
        Assert.False(rig.Capture.Running);
        var status = Data(await rig.Bridge.Execute(Cmd("hotkeysGet"), "host")).GetProperty("status").GetProperty("capture").GetProperty("last").GetString();
        Assert.StartsWith("Choose an output in Setup or Mimic first.", status);
        Assert.True((await rig.Bridge.Execute(Cmd("mimicSources") with { Action = "remember", SourceId = MimicFixture.Source.Id }, "host")).Ok);
        Assert.False(rig.Capture.Running);
        await Task.Delay(320); // beyond the 300 ms repeat guard
        rig.Keys["Ctrl+Alt+C"].Press(); await rig.Bridge.LastHostPress;
        Assert.True(rig.Capture.Running); Assert.Equal(MimicFixture.Source.Name, rig.Bridge.Snapshot.Mimic!.Source);
        // A second press within 300 ms is ignored; after it, the key stops listening.
        rig.Keys["Ctrl+Alt+C"].Press(); await rig.Bridge.LastHostPress;
        Assert.True(rig.Capture.Running);
        await Task.Delay(320);
        rig.Keys["Ctrl+Alt+C"].Press(); await rig.Bridge.LastHostPress;
        Assert.False(rig.Capture.Running);
    }

    [Fact]
    public async Task PanicKeyStopsAndHoldsGuestsAndStopKeyStops()
    {
        await using var rig = new Rig(folder); await rig.Start();
        string audience = "fixture-audience";
        Assert.True((await rig.Bridge.Execute(Cmd("beginSession") with { AudienceId = audience }, "host")).Ok);
        Assert.True((await rig.Bridge.Execute(Set("{\"panic\":\"Ctrl+Shift+F11\",\"stop\":\"Ctrl+Shift+F10\"}"), "host")).Ok);
        var sounds = rig.Bridge.Snapshot.Library.Sounds;
        Task<Reply> Guest(Command c) => rig.Bridge.Execute(c, "guest", new(Guid.NewGuid().ToString(), "Alice"), "peer-alice", audience);
        Assert.True((await Guest(Cmd("play") with { SoundId = sounds[0].Id })).Ok);
        Assert.Equal("PLAYING", rig.Mock.Status);
        rig.Keys["Ctrl+Shift+F11"].Press(); await rig.Bridge.LastHostPress;
        Assert.Equal("STOPPED", rig.Mock.Status); Assert.True(rig.Bridge.Snapshot.PlaybackSuspended);
        var held = JsonSerializer.SerializeToElement((await Guest(Cmd("play") with { SoundId = sounds[1].Id })).Data, Json.Options);
        Assert.Equal("suspended", held.GetProperty("reason").GetString());
        Assert.Equal("STOPPED", rig.Mock.Status);
        // Resume stays in the page; the host still plays, and the Stop key stops it.
        Assert.True((await rig.Bridge.Execute(Cmd("play") with { SoundId = sounds[2].Id }, "host")).Ok);
        Assert.Equal("PLAYING", rig.Mock.Status);
        rig.Keys["Ctrl+Shift+F10"].Press(); await rig.Bridge.LastHostPress;
        Assert.Equal("STOPPED", rig.Mock.Status); Assert.True(rig.Bridge.Snapshot.PlaybackSuspended);
    }

    [Fact]
    public async Task ADamagedOrOversizeFileTurnsEverythingOffAndIsLeftUntouchedAndUnknownFieldsSurvive()
    {
        foreach (var damaged in new[] { "{ not json", "{\"version\":2,\"panic\":\"Ctrl+Alt+P\"}", "{\"version\":1,\"panic\":\"Win+L\"}", "{\"version\":1,\"panic\":7}",
            "{\"version\":1,\"panic\":\"Ctrl+Alt+P\",\"stop\":\"ctrl+alt+p\"}", "{\"version\":1,\"panic\":\"Ctrl+Alt+P\",\"x\":\"" + new string('a', 5000) + "\"}" })
        {
            File.WriteAllText(HotkeysFile, damaged);
            await using var rig = new Rig(folder); await rig.Start();
            var get = await rig.Bridge.Execute(Cmd("hotkeysGet"), "host");
            foreach (var action in HostHotkeys.Actions) Assert.Null(Bound(get, action));
            Assert.Contains("left as it is", Data(get).GetProperty("problem").GetString());
            Assert.Equal(0, rig.Made);
            Assert.Equal(damaged, File.ReadAllText(HotkeysFile));
        }
        File.WriteAllText(HotkeysFile, "{\"version\":1,\"panic\":\"Ctrl+Alt+P\",\"fromANewerSite\":{\"a\":[1,2]}}");
        await using (var rig = new Rig(folder))
        {
            await rig.Start();
            Assert.True(rig.Keys["Ctrl+Alt+P"].Active);
            Assert.True((await rig.Bridge.Execute(Set("{\"stop\":\"Ctrl+Alt+S\"}"), "host")).Ok);
        }
        using var file = JsonDocument.Parse(File.ReadAllText(HotkeysFile));
        Assert.Equal("[1,2]", file.RootElement.GetProperty("fromANewerSite").GetProperty("a").GetRawText());
        Assert.Equal("Ctrl+Alt+S", file.RootElement.GetProperty("stop").GetString());
        Assert.Equal("Ctrl+Alt+P", file.RootElement.GetProperty("panic").GetString());
    }

    [Fact]
    public async Task EndHostingKeepsHostWideKeysAndStillReleasesTheMimicKey()
    {
        await using var rig = new Rig(folder); await rig.Start();
        Assert.True((await rig.Bridge.Execute(Cmd("beginSession"), "host")).Ok);
        Assert.True((await rig.Bridge.Execute(Set("{\"panic\":\"Ctrl+Alt+P\"}"), "host")).Ok);
        Assert.True((await rig.Bridge.Execute(Cmd("mimicHotkey") with { Action = "enable" }, "host")).Ok);
        Assert.True(rig.MimicKeys["F6"].Active);
        Assert.True((await rig.Bridge.Execute(Cmd("endSession"), "host")).Ok);
        Assert.False(rig.MimicKeys["F6"].Active);
        Assert.True(rig.Keys["Ctrl+Alt+P"].Active);
    }

    [Fact]
    public async Task NoHotkeysRegistersNothingButReportsWhy()
    {
        File.WriteAllText(HotkeysFile, "{\"version\":1,\"panic\":\"Ctrl+Alt+P\"}");
        await using var rig = new Rig(folder, noHotkeys: true); await rig.Start();
        Assert.Equal(0, rig.Made);
        var get = await rig.Bridge.Execute(Cmd("hotkeysGet"), "host");
        Assert.Equal("disabled", State(get, "panic")); Assert.True(Data(get).GetProperty("disabled").GetBoolean());
        var set = await rig.Bridge.Execute(Set("{\"stop\":\"Ctrl+Alt+S\"}"), "host");
        Assert.True(set.Ok); Assert.Equal("disabled", State(set, "stop"));
        Assert.Equal(0, rig.Made);
        Assert.Equal("{\"version\":1,\"panic\":\"Ctrl+Alt+P\"}", File.ReadAllText(HotkeysFile));
    }
}
