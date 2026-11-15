using SoundpadWeb;
namespace SoundpadWeb.Testing;

/// <summary>
/// Stage H (2.44) harness verbs: a line on standard input runs its verb; any other line ends the harness, as before.
/// NOTE for the integrator: WP2's copy of the Phase 0 registry (the packages were branched without Phase 0).
/// </summary>
public static class HarnessVerbs
{
    private static readonly Dictionary<string, Func<Bridge, string, bool>> verbs = new(StringComparer.Ordinal);
    static HarnessVerbs()
    {
        // 2.41: the recording history is replaced by a new one at once (the same as the companion restarting Mimic), so
        // browser tests can check that nothing of the old one carries over.
        Register("restart-history", (bridge, _) =>
        {
            bridge.CloseHistory();
            bridge.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "mimicStart") with { SourceId = MimicFixture.Source.Id }, "host").GetAwaiter().GetResult();
        });
    }
    /// <summary>Registers <paramref name="verb"/>: the line is "verb" or "verb arguments".</summary>
    public static void Register(string verb, Action<Bridge, string> run) => verbs[verb] = (b, rest) => { run(b, rest); return true; };
    /// <summary>A verb that may end the harness: it returns false to quit after it ran.</summary>
    public static void RegisterFinal(string verb, Func<Bridge, string, bool> run) => verbs[verb] = run;
    /// <summary>Runs the line's verb. False for an unknown line (the harness quits) or a verb that ends it.</summary>
    public static bool Run(string line, Bridge bridge)
    {
        line = line.TrimStart('\uFEFF'); // Windows PowerShell 5.1 starts what it writes to a child's input with a byte order mark
        int space = line.IndexOf(' ');
        var verb = space < 0 ? line : line[..space];
        return verbs.TryGetValue(verb, out var run) && run(bridge, space < 0 ? "" : line[(space + 1)..]);
    }
}

/// <summary>Where the Stage H packages hook into the harness's startup (Phase 0 shape, WP2's members only).</summary>
public static class HarnessStageH
{
    public static void BeforeBridge(string[] args, string dataDir) => HarnessUpdates.BeforeBridge(args, dataDir);
    /// <summary>2.44 (WP2): after the Bridge is set up and before the server starts; returns the server's resume values after an update.</summary>
    public static Task<ServerHandoff?> BeforeServer(string[] args, Bridge bridge) => HarnessUpdates.BeforeServer(args, bridge);
    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server) => HarnessUpdates.AfterBridge(args, bridge, server);
    public static void RollbackFixture(string dir, Bridge b) => HarnessUpdates.RollbackFixture(dir, b);
}
