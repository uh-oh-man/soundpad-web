using SoundpadWeb;
namespace SoundpadWeb.Testing;

/// <summary>
/// The updater in the harness (2.44, WP2). Never touches a release or an EXE:
///  --handoff-out        an update this harness is asked to install (verb update-exit, or Update now in the page) ends it
///                       through the update path and writes the hand-over into its --data-dir, then the harness exits;
///  --resume-update      reads that hand-over from --data-dir and continues the session (same port, token, history,
///                       drafts, Temp, guests), as the new companion does after an update;
///  verb update-state S [V] [R]  shows update state S (version V, reason R) without any network ("forced").
/// </summary>
public static class HarnessUpdates
{
    private static string? dataDir;
    private static UpdateHandoff? resumed;
    private static string Current => typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void BeforeBridge(string[] args, string directory)
    {
        dataDir = directory;
        if (!args.Contains("--resume-update")) { UpdateFiles.CleanLeftovers(directory, null); return; }
        // The harness that wrote it has exited; nothing waits for this one.
        resumed = UpdateHandoffFile.TryConsume(directory, null, null, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UpdateHandoffFile.Running, out var problem);
        Console.WriteLine(resumed != null ? "TEST_LOG resuming the session of the harness that updated" : "TEST_LOG no update hand-over: " + problem);
    }

    public static async Task<ServerHandoff?> BeforeServer(string[] args, Bridge bridge)
    {
        if (resumed == null) return null;
        await bridge.ResumeFromHandoff(resumed.Bridge);
        return resumed.Server;
    }

    /// <summary>After the server started: the hand-over is consumed (as the real companion does once running).</summary>
    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server)
    {
        if (resumed != null && dataDir != null) UpdateHandoffFile.Finish(dataDir);
        HarnessVerbs.Register("update-state", (b, rest) =>
        {
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            string? version = parts.Length > 1 ? parts[1] : null, reason = parts.Length > 2 ? parts[2] : null;
            long? total = parts[0] is "downloading" or "verifying" or "ready" ? 80L * 1024 * 1024 : null;
            b.Updates.Force(new(parts[0], Current, version, version != null ? $"https://example.invalid/releases/tag/v{version}" : null,
                parts[0] == "downloading" ? 20L * 1024 * 1024 : total, total, reason != null ? "Forced failure: " + reason : null, reason));
        });
        if (!args.Contains("--handoff-out") || dataDir == null) return;
        var directory = dataDir;
        async Task Write(string version, BridgeHandoff part)
        {
            await server.NotifyUpdating(version);
            UpdateHandoffFile.Write(directory, new UpdateHandoff
            {
                Id = Guid.NewGuid().ToString("N"), CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DataDir = directory, OldPid = Environment.ProcessId,
                FromVersion = Current, ToVersion = version, Server = server.Export(), Bridge = part, Window = new(true, false),
            });
            Console.WriteLine("TEST_UPDATE_EXIT " + version);
        }
        // Update now in the page: the harness leaves as the companion would, then ends (its data folder is kept).
        bridge.UpdateExitRequested += async exit =>
        {
            await Write(exit.Version, exit.Bridge);
            Console.Out.Flush();
            Environment.Exit(0);
        };
        HarnessVerbs.RegisterFinal("update-exit", (b, _) =>
        {
            var part = b.EnterUpdateExit().GetAwaiter().GetResult();
            Write(Current, part).GetAwaiter().GetResult();
            return false; // the harness ends; Dispose keeps the history (UpdateExiting)
        });
    }

    /// <summary>--make-rollback-fixture: the updater's files, which older companions must ignore.</summary>
    public static void RollbackFixture(string dir, Bridge b)
    {
        new UpdateSettingsFile(Path.Combine(dir, "update-settings.v1.json")).Save(false);
        // A hand-over left by an interrupted update, 10 minutes old.
        UpdateHandoffFile.Write(dir, new UpdateHandoff
        {
            Id = Guid.NewGuid().ToString("N"), CreatedUtcMs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds(), DataDir = dir, OldPid = 0,
            FromVersion = Current, ToVersion = Current, Server = new(new string('a', 64), 1, null, null, null, false, null), Bridge = new(), Window = new(true, false),
        });
        Console.WriteLine("update files: update-settings.v1.json, " + UpdateHandoffFile.Name);
    }
}
