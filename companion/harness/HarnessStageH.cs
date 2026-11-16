using SoundpadWeb;
namespace SoundpadWeb.Testing;

/// <summary>
/// The harness's stdin verbs (Stage H, 2.44): each line names a verb and its argument; a line no verb knows ends the
/// harness, as any line did before. HarnessStageH and the work packages' Harness*.cs files register them.
/// </summary>
public static class HarnessVerbs
{
    private static readonly Dictionary<string, Func<Bridge, string, bool>> verbs = new(StringComparer.Ordinal);
    /// <summary>Registers <paramref name="verb"/>: the line is "verb" or "verb arguments".</summary>
    public static void Register(string verb, Action<Bridge, string> run) { lock (verbs) verbs[verb] = (b, rest) => { run(b, rest); return true; }; }
    /// <summary>A verb that may end the harness: it returns false to quit after it ran (WP2's update-exit).</summary>
    public static void RegisterFinal(string verb, Func<Bridge, string, bool> run) { lock (verbs) verbs[verb] = run; }
    /// <summary>Runs the line's verb. False for an unknown line (the harness quits) or a verb that ends it.</summary>
    public static bool Run(string line, Bridge bridge)
    {
        line = line.TrimStart('﻿'); // Windows PowerShell 5.1 starts what it writes to a child's input with a byte order mark
        int space = line.IndexOf(' ');
        var verb = space < 0 ? line : line[..space];
        Func<Bridge, string, bool>? run;
        lock (verbs) if (!verbs.TryGetValue(verb, out run)) return false;
        try { return run(bridge, space < 0 ? "" : line[(space + 1)..]); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine("TEST_LOG harness verb " + verb + " failed: " + ex.Message); return true; }
    }
}

/// <summary>
/// Stage H (2.44) harness wiring: backups (HarnessBackups.cs), the updater (HarnessUpdates.cs), Organize mode
/// (HarnessOrganize.cs) and setup with host-wide hotkeys (HarnessSetup.cs).
/// </summary>
public static class HarnessStageH
{
    /// <summary>The mock Soundpad of the e2e harness, for verbs that change what it answers.</summary>
    public static MockSoundpad? Mock { get; set; }

    static HarnessStageH()
    {
        // 2.41: the recording history is replaced by a new one at once (the same as the companion restarting Mimic),
        // so browser tests can check that nothing of the old one carries over.
        HarnessVerbs.Register("restart-history", (bridge, _) =>
        {
            bridge.CloseHistory();
            bridge.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "mimicStart") with { SourceId = MimicFixture.Source.Id }, "host").GetAwaiter().GetResult();
        });
    }

    /// <summary>Once the mock Soundpad's library is set up, before the Bridge is built (Organize fixtures).</summary>
    public static void MockReady(string[] args, MockSoundpad mock, string dataDir)
    {
        Mock = mock;
        HarnessOrganize.MockReady(args, mock, dataDir);
    }

    /// <summary>Before the e2e harness builds its Bridge on <paramref name="dataDir"/>.</summary>
    public static void BeforeBridge(string[] args, string dataDir)
    {
        HarnessBackups.BeforeBridge(args, dataDir);
        HarnessUpdates.BeforeBridge(args, dataDir);
        HarnessSetup.BeforeBridge(args, dataDir);
    }

    /// <summary>Right after the Bridge is built: test seams that must be in place before Stage H starts (fixture hotkeys, file checks).</summary>
    public static void BridgeMade(string[] args, Bridge bridge)
    {
        HarnessOrganize.BridgeMade(args, bridge);
        HarnessSetup.BridgeMade(args, bridge);
        // Feedback (2.45): never the real desktop. --feedback-dir <folder>, or a folder of this process in TEMP.
        int at = Array.IndexOf(args, "--feedback-dir");
        bridge.FeedbackFolder = at >= 0 && at + 1 < args.Length
            ? Path.GetFullPath(args[at + 1])
            : Path.Combine(Path.GetTempPath(), "soundpad-web-harness-feedback", Environment.ProcessId.ToString());
    }

    /// <summary>After the Bridge is set up and before the server starts; the server's resume values after an update (WP2).</summary>
    public static Task<ServerHandoff?> BeforeServer(string[] args, Bridge bridge) => HarnessUpdates.BeforeServer(args, bridge);

    /// <summary>After the e2e harness's server started, before TEST_READY.</summary>
    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server)
    {
        HarnessBackups.AfterBridge(args, bridge, server);
        HarnessUpdates.AfterBridge(args, bridge, server);
        HarnessOrganize.AfterBridge(args, bridge);
        HarnessSetup.AfterBridge(args, bridge, server);
        // The real companion starts Stage H's services in StartupCleanup (or ResumeFromHandoff); the harness has
        // neither cleanup, so here, once every package's seams are set (a resumed harness has started them already).
        bridge.StageHStarted();
    }

    /// <summary>At the end of --make-rollback-fixture, before the session ends: each package writes its persisted files into <paramref name="dir"/>.</summary>
    public static async Task RollbackFixture(string dir, Bridge b)
    {
        await HarnessBackups.RollbackFixture(dir, b);
        HarnessUpdates.RollbackFixture(dir, b);
        await HarnessOrganize.RollbackFixture(dir, b);
        HarnessSetup.RollbackFixture(dir, b);
    }
}
