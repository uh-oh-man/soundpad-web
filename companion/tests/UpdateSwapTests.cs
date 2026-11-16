using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>The EXE swap and its watchdog (2.44, WP2), on fake files and a fake launcher: every failure rolls back.</summary>
public sealed class UpdateSwapTests
{
    private const string Exe = @"C:\App\SoundpadWeb.exe", Download = @"C:\App\.SoundpadWeb.2.44.1.download", Data = @"C:\Data";
    private static readonly string Id = new('a', 32);

    /// <summary>Files as a dictionary of names to contents; any step can be made to fail.</summary>
    private sealed class Files : IUpdateFiles
    {
        public readonly Dictionary<string, string> All = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Log = [];
        public Func<string, string, bool>? FailMove;
        public bool Exists(string path) => All.ContainsKey(path);
        public void Move(string from, string to, bool overwrite)
        {
            Log.Add($"move {Path.GetFileName(from)} -> {Path.GetFileName(to)}");
            if (FailMove?.Invoke(from, to) == true) throw new IOException("refused (test)");
            if (!All.Remove(from, out var content)) throw new FileNotFoundException(from);
            if (!overwrite && All.ContainsKey(to)) { All[from] = content; throw new IOException("exists"); }
            All[to] = content;
        }
        public void Delete(string path) => All.Remove(path);
        public void WriteAtomic(string path, byte[] bytes) { Log.Add("write " + Path.GetFileName(path)); All[path] = System.Text.Encoding.UTF8.GetString(bytes); }
        public string? ReadText(string path) => All.GetValueOrDefault(path);
    }

    private sealed class Child(int id) : IUpdateChild
    {
        public int Id => id;
        public bool HasExited { get; set; }
        public int? ExitCode { get; set; }
        public bool Killed;
        public void Dispose() { }
        public void Kill() { Killed = true; HasExited = true; }
    }

    /// <summary>Each start runs one scripted behaviour: what the started version does with the ready file.</summary>
    private sealed class Launcher(Files files, params Func<Child, string, IReadOnlyList<string>, bool>[] behaviours) : IProcessLauncher
    {
        public readonly List<(string Exe, string Contents, string Args, Child Child)> Started = [];
        public Exception? Throw;
        public IUpdateChild Start(string exe, IReadOnlyList<string> args, string log, bool console)
        {
            if (Throw is { } ex && Started.Count == 0) { Started.Add((exe, "", "", new(0))); throw ex; }
            var child = new Child(1000 + Started.Count);
            Started.Add((exe, files.All.GetValueOrDefault(exe) ?? "", string.Join(' ', args), child));
            var behave = behaviours[Math.Min(Started.Count - 1, behaviours.Length - 1)];
            behave(child, files.All.GetValueOrDefault(exe) ?? "", args);
            return child;
        }
    }

    private static Func<Child, string, IReadOnlyList<string>, bool> Ready(Files files, string version) => (_, _, _) => { files.All[Path.Combine(Data, "update-ready." + Id)] = $"{{\"version\":\"{version}\",\"pid\":1}}"; return true; };
    private static Func<Child, string, IReadOnlyList<string>, bool> Crash() => (c, _, _) => { c.HasExited = true; c.ExitCode = -1073741819; return true; };
    private static Func<Child, string, IReadOnlyList<string>, bool> Hang() => (_, _, _) => true;

    private static (Files Files, SwapPlan Plan) Setup()
    {
        var files = new Files();
        files.All[Exe] = "old"; files.All[Download] = "new";
        int written = 0;
        var plan = new SwapPlan(Exe, Download, "2.44.1", "2.44.0", Data, Id, ["--data-dir", Data, "--no-browser"], () => System.Text.Encoding.UTF8.GetBytes("handoff " + ++written), false);
        return (files, plan);
    }
    private static UpdateSwap Swap(Files files, Launcher launcher) => new(files, launcher, _ => { }) { ReadyTimeout = TimeSpan.FromSeconds(2), PollInterval = TimeSpan.FromMilliseconds(1), Sleep = _ => Thread.Sleep(1), OwnPid = 4242 };

    [Fact]
    public void ASuccessfulSwapKeepsThePreviousExeAndStartsTheNewOneWithTheHandOver()
    {
        var (files, plan) = Setup();
        var launcher = new Launcher(files, Ready(files, "2.44.1"));
        Assert.Equal(SwapResult.Updated, Swap(files, launcher).Run(plan));
        Assert.Equal(("new", "old"), (files.All[Exe], files.All[plan.PreviousPath]));
        Assert.False(files.Exists(Download));
        var started = Assert.Single(launcher.Started);
        Assert.Equal(("new", $"--data-dir {Data} --no-browser --resume-update {Id} --wait-pid 4242"), (started.Contents, started.Args));
        Assert.Equal("handoff 1", files.All[plan.HandoffPath]); // written before the start
        Assert.False(files.Exists(plan.ReadyPath));
        Assert.Equal(["write update-handoff.v1.bin", "move SoundpadWeb.exe -> SoundpadWeb.previous.exe", "move .SoundpadWeb.2.44.1.download -> SoundpadWeb.exe"], files.Log);
    }

    public static TheoryData<string> Failures => ["move1", "move2", "launch", "timeout", "wrongVersion", "crash"];

    [Theory]
    [MemberData(nameof(Failures))]
    public void EveryFailureRestoresThePreviousExeAndStartsItAgainWithAFreshHandOver(string failure)
    {
        var (files, plan) = Setup();
        files.All[plan.PreviousPath] = "an older previous";
        var first = failure switch { "timeout" => Hang(), "wrongVersion" => Ready(files, "2.43.0"), "crash" => Crash(), _ => Ready(files, "2.44.1") };
        // After a failure of the new version the old one comes back and reports its own version.
        var launcher = new Launcher(files, failure is "move1" or "move2" or "launch" ? Ready(files, "2.44.0") : first, Ready(files, "2.44.0"));
        if (failure == "move1") files.FailMove = (from, to) => from == Exe && to == plan.PreviousPath;
        if (failure == "move2") files.FailMove = (from, _) => from == Download;
        if (failure == "launch") launcher.Throw = new System.ComponentModel.Win32Exception(5, "Access is denied (test)");
        var swap = Swap(files, launcher);
        Assert.Equal(SwapResult.RolledBack, swap.Run(plan));
        Assert.NotNull(swap.Problem);
        Assert.Equal("old", files.All[Exe]); // the running version is back under its own name
        var last = launcher.Started[^1];
        Assert.Equal("old", last.Contents);
        Assert.Contains("--resume-update " + Id, last.Args);
        Assert.Equal("handoff 2", files.All[plan.HandoffPath]); // written again, from memory
        if (failure is "timeout" or "wrongVersion" or "crash" or "launch")
        {
            Assert.Equal("new", files.All[plan.FailedPath]); // kept aside for a look, removed by the next normal start
            Assert.False(files.Exists(plan.PreviousPath));
        }
        if (failure is "timeout" or "wrongVersion") Assert.True(launcher.Started[0].Child.Killed); // the new one is stopped first
        if (failure == "move1") Assert.Equal("an older previous", files.All[plan.PreviousPath]);
        if (failure == "move2") Assert.Equal("new", files.All[Download]);
    }

    [Fact]
    public void WhenTheOldVersionDoesNotComeBackEitherTheHandOverIsDeletedForTheCrashPath()
    {
        var (files, plan) = Setup();
        var launcher = new Launcher(files, Crash(), Crash());
        Assert.Equal(SwapResult.Failed, Swap(files, launcher).Run(plan));
        Assert.Equal(2, launcher.Started.Count);
        Assert.False(files.Exists(plan.HandoffPath)); // never a key left behind; the next start offers Recover or Discard
        Assert.Equal("old", files.All[Exe]);
    }

    /// <summary>
    /// The real launcher keeps the started process's exit code (2.44 real-hardware proof, 2026-11-15): a new version
    /// that stopped while starting was logged as "exit code unknown", because the launcher closed its own handle to the
    /// child and later read it by process id only, once the process was gone.
    /// </summary>
    [Fact]
    public void TheWindowsLauncherReportsTheExitCodeOfAChildThatStopped()
    {
        var log = Path.Combine(Path.GetTempPath(), "soundpad-web-launcher-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            using var child = new WindowsLauncher().Start(cmd, ["/c", "exit 7"], log, false);
            for (int i = 0; i < 200 && !child.HasExited; i++) Thread.Sleep(50);
            Thread.Sleep(200); // the process id is no longer in use by then
            Assert.True(child.HasExited);
            Assert.Equal(7, child.ExitCode);
        }
        finally { try { File.Delete(log); } catch (IOException) { } }
    }
}
