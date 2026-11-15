namespace SoundpadWeb.Testing;

/// <summary>
/// Stage H (2.44) harness hook points (spec section 3.2.8). Phase 0 owns this file; this is the WP3 subset, written
/// to the Phase 0 names. Verbs are lines on the harness's stdin; any other line ends the harness, as before.
/// </summary>
public static class HarnessVerbs
{
    private static readonly Dictionary<string, Action<Bridge, string>> verbs = new(StringComparer.Ordinal);
    /// <summary>Registers <paramref name="verb"/>: a line equal to it, or starting with it and a space, runs <paramref name="run"/> with the rest.</summary>
    public static void Register(string verb, Action<Bridge, string> run) => verbs[verb] = run;
    /// <summary>Runs a known verb and returns true; false for any other line (the harness then quits).</summary>
    public static bool Run(string line, Bridge bridge)
    {
        int space = line.IndexOf(' ');
        var verb = space < 0 ? line : line[..space];
        if (!verbs.TryGetValue(verb, out var run)) return false;
        try { run(bridge, space < 0 ? "" : line[(space + 1)..]); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine("TEST_VERB_FAILED " + verb + ": " + ex.Message); }
        return true;
    }
}

public static class HarnessStageH
{
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
    /// <summary>Before the Bridge is built, with the data folder.</summary>
    public static void BeforeBridge(string[] args, string dataDir) { }
    /// <summary>Once the mock Soundpad's library is set up, before the Bridge is built (WP3: Organize fixtures).</summary>
    public static void MockReady(string[] args, MockSoundpad mock, string dataDir) => HarnessOrganize.MockReady(args, mock, dataDir);
    /// <summary>After the local server started, before TEST_READY.</summary>
    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server) => HarnessOrganize.AfterBridge(args, bridge);
    /// <summary>At the end of --make-rollback-fixture, with the fixture's Bridge.</summary>
    public static Task RollbackFixture(string dir, Bridge b) => HarnessOrganize.RollbackFixture(dir, b);
}
