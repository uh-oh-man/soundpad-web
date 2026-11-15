namespace SoundpadWeb.Testing;

/// <summary>
/// The harness's stdin verbs (Stage H, 2.44): each line names a verb and its argument; a line no verb knows ends the
/// harness, as any line did before. Work packages register their own verbs.
/// </summary>
public static class HarnessVerbs
{
    private static readonly Dictionary<string, Action<Bridge, string>> verbs = new(StringComparer.Ordinal);
    public static void Register(string verb, Action<Bridge, string> run) { lock (verbs) verbs[verb] = run; }
    /// <summary>Runs the line's verb; false when no verb knows it (the harness then quits).</summary>
    public static bool Run(string line, Bridge bridge)
    {
        var space = line.IndexOf(' ');
        var verb = space < 0 ? line : line[..space];
        Action<Bridge, string>? run;
        lock (verbs) if (!verbs.TryGetValue(verb, out run)) return false;
        try { run(bridge, space < 0 ? "" : line[(space + 1)..]); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine("TEST_LOG verb " + verb + " failed: " + ex.Message); }
        return true;
    }
}

/// <summary>
/// Stage H hooks of the harness (2.44). This copy was made by WP1 (backups) on a base without Phase 0; it calls only
/// the backups hooks. The integrator's Phase 0 version (all four work packages) replaces it.
/// </summary>
public static class HarnessStageH
{
    static HarnessStageH()
    {
        // 2.41: the recording history is replaced by a new one at once (the same as the companion restarting Mimic).
        HarnessVerbs.Register("restart-history", (bridge, _) =>
        {
            bridge.CloseHistory();
            bridge.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "mimicStart") with { SourceId = MimicFixture.Source.Id }, "host").GetAwaiter().GetResult();
        });
    }
    /// <summary>Before the Bridge is made, with the data folder it will use.</summary>
    public static void BeforeBridge(string[] args, string dataDir) => HarnessBackups.BeforeBridge(args, dataDir);
    /// <summary>After the loopback server started, before TEST_READY.</summary>
    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server) => HarnessBackups.AfterBridge(args, bridge, server);
    /// <summary>At the end of --make-rollback-fixture: every Stage H file a rollback must survive.</summary>
    public static Task RollbackFixture(string dir, Bridge bridge) => HarnessBackups.RollbackFixture(dir, bridge);
}
