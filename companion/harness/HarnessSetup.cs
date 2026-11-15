using SoundpadWeb;
namespace SoundpadWeb.Testing;

/// <summary>
/// Harness hooks of Stage H's setup and hotkeys package (2.44). Never registers a real Windows hotkey and never asks
/// whether the real Soundpad runs:
/// <list type="bullet">
/// <item>without <c>--first-run</c> the data folder gets a finished setup.json (if it has none), so other browser
/// tests never see the setup guide;</item>
/// <item>host-wide hotkeys are fixtures, and Ctrl+Alt+F9 plays a key another app owns;</item>
/// <item>verbs: <c>soundpad-offline</c> / <c>soundpad-online</c> (the mock stops answering, as if Soundpad were
/// closed, or comes back) and <c>press-hotkey capture|panic|stop</c> (a press of that host-wide key).</item>
/// </list>
/// </summary>
public static class HarnessSetup
{
    public const string TakenChord = "Ctrl+Alt+F9";
    private static volatile bool soundpadRunning = true;
    private static readonly Dictionary<string, HotkeyFixture> keys = new(StringComparer.OrdinalIgnoreCase);
    private static IGlobalHotkey Fixture(HotkeyChord chord)
    {
        var key = new HotkeyFixture(chord.Text) { Taken = string.Equals(chord.Text, TakenChord, StringComparison.OrdinalIgnoreCase) };
        lock (keys) keys[chord.Text] = key;
        return key;
    }

    public static void BeforeBridge(string[] args, string dataDir)
    {
        HostHotkeys.ForbidWindowsHotkeys = true;
        var setup = Path.Combine(dataDir, "setup.json");
        if (!args.Contains("--first-run") && !File.Exists(setup))
            new SetupStore(setup).Set("done", "Harness", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server)
    {
        bridge.UseHostHotkeys(Fixture);
        bridge.UseSoundpadRunning(() => soundpadRunning);
        // The harness never runs StartupCleanup; the stored host-wide keys are registered as the companion does after it.
        bridge.StartSetupForHarness();
        HarnessVerbs.Register("soundpad-offline", (b, _) =>
        {
            soundpadRunning = false;
            if (HarnessStageH.Mock is { } mock) mock.RejectPrefix = "";
            b.Poll(true).GetAwaiter().GetResult();
        });
        HarnessVerbs.Register("soundpad-online", (_, _) =>
        {
            if (HarnessStageH.Mock is { } mock) mock.RejectPrefix = null;
            soundpadRunning = true;
        });
        HarnessVerbs.Register("press-hotkey", (b, action) =>
        {
            if (!HostHotkeys.Actions.Contains(action)) throw new InvalidDataException("press-hotkey capture|panic|stop");
            // The bound key's fixture when there is one (as Windows would deliver it), else the action directly.
            var chord = b.HostHotkeyBinding(action);
            HotkeyFixture? key = null;
            if (chord != null) lock (keys) keys.TryGetValue(chord, out key);
            if (key is { Active: true }) key.Press(); else _ = b.HostHotkeyPressed(action);
            b.LastHostPress.GetAwaiter().GetResult();
        });
    }

    /// <summary>--make-rollback-fixture: a finished setup.json and a hotkeys.json with two keys, for older companions to ignore.</summary>
    public static void RollbackFixture(string dir, Bridge b)
    {
        HostHotkeys.ForbidWindowsHotkeys = true;
        b.UseHostHotkeys(Fixture);
        Command C(string op, string body) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op) { Body = System.Text.Json.JsonDocument.Parse(body).RootElement.Clone() };
        foreach (var (op, body) in new[] { ("setupSet", "{\"action\":\"done\"}"), ("hotkeysSet", "{\"capture\":\"Ctrl+Alt+F8\",\"panic\":\"Ctrl+Shift+F11\"}") })
        {
            var reply = b.Execute(C(op, body), "host").GetAwaiter().GetResult();
            if (!reply.Ok) throw new InvalidOperationException(op + ": " + reply.Error);
        }
        if (!File.Exists(Path.Combine(dir, "setup.json")) || !File.Exists(Path.Combine(dir, "hotkeys.json"))) throw new InvalidOperationException("setup.json or hotkeys.json missing");
    }
}
