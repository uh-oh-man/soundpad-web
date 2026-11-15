using SoundpadWeb;
namespace SoundpadWeb.Testing;

/// <summary>
/// The harness's stdin verbs (2.44). Each line is one verb and its argument; a line no verb knows ends the
/// harness, as any line did before. Registered by HarnessStageH and the work packages' Harness*.cs files.
/// </summary>
public static class HarnessVerbs
{
    private static readonly Dictionary<string, Action<Bridge, string>> verbs = new(StringComparer.Ordinal);
    public static void Register(string verb, Action<Bridge, string> run) => verbs[verb] = run;
    /// <summary>Runs the verb of <paramref name="line"/>; false for an unknown line (the harness then quits).</summary>
    public static bool Run(string line, Bridge bridge)
    {
        int space = line.IndexOf(' ');
        var (verb, argument) = space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
        if (!verbs.TryGetValue(verb, out var run)) return false;
        try { run(bridge, argument); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine($"TEST_LOG harness verb {verb} failed: {ex.Message}"); }
        return true;
    }
}

/// <summary>
/// Stage H (2.44) harness wiring. Integrator-owned and frozen; each work package fills in its own Harness*.cs.
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

    /// <summary>Before the e2e harness builds its Bridge on <paramref name="dataDir"/>.</summary>
    public static void BeforeBridge(string[] args, string dataDir)
    {
        HarnessBackups.BeforeBridge(args, dataDir); HarnessUpdates.BeforeBridge(args, dataDir);
        HarnessOrganize.BeforeBridge(args, dataDir); HarnessSetup.BeforeBridge(args, dataDir);
    }

    /// <summary>After the e2e harness's server started, before TEST_READY.</summary>
    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server)
    {
        HarnessBackups.AfterBridge(args, bridge, server); HarnessUpdates.AfterBridge(args, bridge, server);
        HarnessOrganize.AfterBridge(args, bridge, server); HarnessSetup.AfterBridge(args, bridge, server);
    }

    /// <summary>At the end of --make-rollback-fixture: each package writes its persisted files into <paramref name="dir"/>.</summary>
    public static void RollbackFixture(string dir, Bridge b)
    {
        HarnessBackups.RollbackFixture(dir, b); HarnessUpdates.RollbackFixture(dir, b);
        HarnessOrganize.RollbackFixture(dir, b); HarnessSetup.RollbackFixture(dir, b);
    }
}
