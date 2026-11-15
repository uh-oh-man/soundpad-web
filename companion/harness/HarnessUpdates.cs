using SoundpadWeb;
namespace SoundpadWeb.Testing;

/// <summary>Harness hooks of Stage H's Updates work package (2.44). Phase 0 stub.</summary>
public static class HarnessUpdates
{
    public static void BeforeBridge(string[] args, string dataDir) { }
    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server) { }
    public static void RollbackFixture(string dir, Bridge b) { }
}
