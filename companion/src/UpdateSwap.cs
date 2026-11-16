using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace SoundpadWeb;

/// <summary>The file operations of the swap (2.44), so tests can fail any one of them.</summary>
public interface IUpdateFiles
{
    bool Exists(string path);
    void Move(string from, string to, bool overwrite);
    void Delete(string path);
    void WriteAtomic(string path, byte[] bytes);
    string? ReadText(string path);
}

/// <summary>A process the swap started (the new version, or the old one again).</summary>
public interface IUpdateChild : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    void Kill();
}

/// <summary>Starts the relaunched companion: no window of its own and never activated (see <see cref="WindowsLauncher"/>).</summary>
public interface IProcessLauncher
{
    /// <param name="log">Standard output and errors go to this file (update-child.log).</param>
    /// <param name="console">The old companion had its own console window (no WebView2, or --no-window started by hand):
    /// the new one gets one too, minimized and not activated, instead of output to <paramref name="log"/>.</param>
    IUpdateChild Start(string exe, IReadOnlyList<string> args, string log, bool console);
}

/// <summary>Real files, with the brief retries a virus scanner holding a new file needs (as Persistence.cs does).</summary>
public sealed class UpdateFiles : IUpdateFiles
{
    public static readonly UpdateFiles Instance = new();
    public bool Exists(string path) => File.Exists(path);
    public void Move(string from, string to, bool overwrite) => Retry(() => File.Move(from, to, overwrite));
    public void Delete(string path) => Retry(() => { if (File.Exists(path)) File.Delete(path); });
    public string? ReadText(string path) { try { return File.Exists(path) && new FileInfo(path).Length <= 4096 ? File.ReadAllText(path) : null; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } }
    void IUpdateFiles.WriteAtomic(string path, byte[] bytes) => WriteAtomic(path, bytes);

    /// <summary>Writes <paramref name="path"/> through a .tmp file that is flushed to disk, then moved over it.</summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        Retry(() =>
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        });
    }

    private static void Retry(Action work)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { work(); return; }
            catch (Exception ex) when (attempt < 40 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(25); }
        }
    }

    /// <summary>
    /// A normal start (no --resume-update) removes what an update leaves behind (2.44): hand-overs (the private key
    /// never outlives an update), ready files and the child's log in the data folder; unfinished downloads and EXEs that
    /// failed to start beside the EXE. Only these exact names are touched.
    /// </summary>
    public static void CleanLeftovers(string dataDir, string? exePath)
    {
        void Remove(string folder, string pattern, Func<string, bool> matches)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder, pattern))
                    if (matches(Path.GetFileName(file))) { try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Remove(dataDir, "update-handoff.v1.bin*", _ => true);
        Remove(dataDir, "update-ready.*", n => System.Text.RegularExpressions.Regex.IsMatch(n, "^update-ready\\.[a-f0-9]{32}(\\.tmp)?$"));
        Remove(dataDir, "update-child.log", _ => true);
        if (exePath == null) return;
        var folder = Path.GetDirectoryName(exePath)!; var stem = Path.GetFileNameWithoutExtension(exePath);
        Remove(folder, "." + stem + ".*.download", n => System.Text.RegularExpressions.Regex.IsMatch(n, "^\\." + System.Text.RegularExpressions.Regex.Escape(stem) + "\\.\\d+\\.\\d+\\.\\d+\\.download$"));
        Remove(folder, stem + ".failed-*.exe", n => System.Text.RegularExpressions.Regex.IsMatch(n, "^" + System.Text.RegularExpressions.Regex.Escape(stem) + "\\.failed-\\d+\\.\\d+\\.\\d+\\.exe$"));
    }
}

/// <summary>What the old process does once its own server, pipe and lock are gone.</summary>
/// <param name="Args">The relaunch arguments without --resume-update and --wait-pid (added here).</param>
/// <param name="Handoff">Writes the hand-over again (from memory, with a fresh time) for a rollback relaunch.</param>
public sealed record SwapPlan(string ExePath, string DownloadPath, string Version, string CurrentVersion, string DataDir, string Id, IReadOnlyList<string> Args, Func<byte[]> Handoff, bool Console)
{
    public string PreviousPath => Path.Combine(Path.GetDirectoryName(ExePath)!, Path.GetFileNameWithoutExtension(ExePath) + ".previous.exe");
    public string FailedPath => Path.Combine(Path.GetDirectoryName(ExePath)!, Path.GetFileNameWithoutExtension(ExePath) + ".failed-" + Version + ".exe");
    public string HandoffPath => Path.Combine(DataDir, UpdateHandoffFile.Name);
    public string ReadyPath => Path.Combine(DataDir, "update-ready." + Id);
    public string ChildLog => Path.Combine(DataDir, "update-child.log");
}

public enum SwapResult { Updated, RolledBack, Failed }

/// <summary>
/// The file swap and watchdog (2.44). The running EXE is renamed to &lt;stem&gt;.previous.exe (Windows allows renaming a
/// running program on NTFS), the verified download takes its name, and the new version is started with
/// --resume-update. The old process then waits up to 90 s for the new one's ready file. If anything fails (a move, the
/// start, a crash, the time limit, a ready file of another version), the new one is stopped, the previous EXE goes back
/// to its name (the failed one becomes &lt;stem&gt;.failed-&lt;ver&gt;.exe) and the old version is started again with the
/// hand-over written again. If that fails too, the hand-over is deleted: the next start takes the crash path.
/// </summary>
public sealed class UpdateSwap(IUpdateFiles files, IProcessLauncher launcher, Action<string> log)
{
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public Func<long> Clock { get; init; } = () => Environment.TickCount64;
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;
    public int OwnPid { get; init; } = Environment.ProcessId;
    /// <summary>What went wrong, in words, after a rollback or failure.</summary>
    public string? Problem { get; private set; }

    public SwapResult Run(SwapPlan plan)
    {
        bool movedOld = false, movedNew = false;
        try
        {
            files.Delete(plan.ReadyPath);
            files.WriteAtomic(plan.HandoffPath, plan.Handoff());
            files.Move(plan.ExePath, plan.PreviousPath, true); movedOld = true;
            try { files.Move(plan.DownloadPath, plan.ExePath, false); movedNew = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files.Move(plan.PreviousPath, plan.ExePath, false); movedOld = false;
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = "The new version could not be put in place: " + ex.Message;
            log(Problem);
            return Rollback(plan, movedOld, movedNew);
        }
        IUpdateChild? child = null;
        try { child = launcher.Start(plan.ExePath, Arguments(plan), plan.ChildLog, plan.Console); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Problem = "Windows did not start the new version (" + ex.Message + ").";
            log(Problem);
            return Rollback(plan, true, true);
        }
        using var started = child;
        var waited = WaitReady(plan, started, plan.Version);
        if (waited == null) { log($"Version {plan.Version} started and took over."); files.Delete(plan.ReadyPath); return SwapResult.Updated; }
        Problem = waited;
        log(Problem);
        Stop(started);
        return Rollback(plan, true, true);
    }

    /// <summary>Null when the ready file of <paramref name="version"/> appeared, else what went wrong.</summary>
    private string? WaitReady(SwapPlan plan, IUpdateChild child, string version)
    {
        long deadline = Clock() + (long)ReadyTimeout.TotalMilliseconds;
        while (true)
        {
            if (files.ReadText(plan.ReadyPath) is { } text)
            {
                UpdateReady? ready = null;
                try { ready = JsonSerializer.Deserialize<UpdateReady>(text, Json.Options); } catch (JsonException) { }
                if (ready?.Version == version) return null;
                if (ready != null) return $"The started program reported version {ready.Version}, not {version}.";
            }
            if (child.HasExited) return $"Version {version} stopped while starting (exit code {child.ExitCode?.ToString() ?? "unknown"}). Windows or an antivirus may have blocked it.";
            if (Clock() >= deadline) return $"Version {version} did not finish starting within {ReadyTimeout.TotalSeconds:0} seconds.";
            Sleep(PollInterval);
        }
    }

    private static void Stop(IUpdateChild child)
    {
        try { if (!child.HasExited) child.Kill(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private IReadOnlyList<string> Arguments(SwapPlan plan) => [.. plan.Args, "--resume-update", plan.Id, "--wait-pid", OwnPid.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary>Puts the old EXE back (when it was moved) and starts it again with the hand-over.</summary>
    private SwapResult Rollback(SwapPlan plan, bool movedOld, bool movedNew)
    {
        try
        {
            if (movedNew) files.Move(plan.ExePath, plan.FailedPath, true);
            if (movedOld) files.Move(plan.PreviousPath, plan.ExePath, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("The previous version could not be put back: " + ex.Message + $" Rename {Path.GetFileName(plan.PreviousPath)} to {Path.GetFileName(plan.ExePath)} by hand.");
            Abandon(plan);
            return SwapResult.Failed;
        }
        try
        {
            files.Delete(plan.ReadyPath);
            files.Delete(plan.HandoffPath + ".consumed");
            files.WriteAtomic(plan.HandoffPath, plan.Handoff());
            using var again = launcher.Start(plan.ExePath, Arguments(plan), plan.ChildLog, plan.Console);
            var waited = WaitReady(plan, again, plan.CurrentVersion);
            if (waited == null) { log($"Back on version {plan.CurrentVersion}; the session continues."); files.Delete(plan.ReadyPath); return SwapResult.RolledBack; }
            log(waited);
            Stop(again);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException) { log("The previous version did not start again: " + ex.Message); }
        Abandon(plan);
        return SwapResult.Failed;
    }

    /// <summary>Two failed starts: the hand-over goes, so the next start takes the crash path (Recover or Discard).</summary>
    private void Abandon(SwapPlan plan)
    {
        try { files.Delete(plan.HandoffPath); files.Delete(plan.HandoffPath + ".consumed"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        log("The update could not continue the session. Start Soundpad Web again; it offers to recover Temp.");
    }
}

/// <summary>
/// Starts the relaunched companion with CreateProcess (2.44): no console window, standard output and errors in a file,
/// standard input from NUL, so nothing appears and nothing takes the keyboard. A companion that had its own console gets
/// a new console that starts minimized and is never activated.
/// </summary>
public sealed class WindowsLauncher : IProcessLauncher
{
    public IUpdateChild Start(string exe, IReadOnlyList<string> args, string log, bool console)
    {
        var line = new StringBuilder(Quote(exe));
        foreach (var arg in args) line.Append(' ').Append(Quote(arg));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        SafeFileHandle? output = null, input = null;
        uint flags = CreateUnicodeEnvironment;
        bool inherit = false;
        try
        {
            if (console)
            {
                flags |= CreateNewConsole;
                startup.Flags = UseShowWindow; startup.ShowWindow = ShowMinNoActive;
            }
            else
            {
                var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
                output = CreateFile(log, GenericWrite, FileShareRead | FileShareWrite | FileShareDelete, ref security, CreateAlways, 0, 0);
                input = CreateFile("NUL", GenericRead, FileShareRead | FileShareWrite, ref security, OpenExisting, 0, 0);
                if (output.IsInvalid || input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                startup.Flags = UseStdHandles | UseShowWindow; startup.ShowWindow = ShowNoActivate;
                startup.StdInput = input.DangerousGetHandle(); startup.StdOutput = output.DangerousGetHandle(); startup.StdError = output.DangerousGetHandle();
                flags |= CreateNoWindow; inherit = true;
                // Only the two handles above go to the child: this process's own standard handles (a script's pipes,
                // say) are not passed on, so whoever started the old companion is not kept waiting by the new one.
                foreach (var which in new[] { -10, -11, -12 }) { var own = GetStdHandle(which); if (own != 0 && own != -1) SetHandleInformation(own, HandleFlagInherit, 0); }
            }
            if (!CreateProcess(null, line, 0, 0, inherit, flags, 0, Path.GetDirectoryName(exe), ref startup, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.Thread);
            // The child keeps CreateProcess's own handle: its exit code stays readable after it ended (a new version
            // that stopped while starting was logged as "exit code unknown" before; 2.44 real-hardware proof).
            return new Child(new SafeProcessHandle(info.Process, true), info.ProcessId);
        }
        finally { output?.Dispose(); input?.Dispose(); }
    }

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;
        var quoted = new StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { quoted.Append('\\', slashes * 2); break; }
            if (arg[i] == '"') quoted.Append('\\', slashes * 2 + 1).Append('"');
            else quoted.Append('\\', slashes).Append(arg[i]);
        }
        return quoted.Append('"').ToString();
    }

    private sealed class Child(SafeProcessHandle handle, int id) : IUpdateChild
    {
        public int Id => id;
        public void Dispose() => handle.Dispose();
        public bool HasExited => WaitForSingleObject(handle, 0) == WaitObject0;
        public int? ExitCode => HasExited && GetExitCodeProcess(handle, out uint code) ? unchecked((int)code) : null;
        public void Kill()
        {
            if (HasExited) return;
            // The whole tree (the new version's WebView2 processes too).
            try { using var process = Process.GetProcessById(id); process.Kill(true); process.WaitForExit(10000); }
            catch (ArgumentException) { } // it ended meanwhile
        }
    }

    private const uint CreateNoWindow = 0x08000000, CreateNewConsole = 0x00000010, CreateUnicodeEnvironment = 0x00000400;
    private const int UseShowWindow = 0x1, UseStdHandles = 0x100;
    private const short ShowNoActivate = 4, ShowMinNoActive = 7;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, FileShareDelete = 4, CreateAlways = 2, OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public nint Reserved3, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, nint processSecurity, nint threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int which);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    private const uint HandleFlagInherit = 1, WaitObject0 = 0;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);
}

/// <summary>The swap Program.cs runs after Run returned (2.44): nothing of this process holds the data folder any more.</summary>
internal sealed class PendingUpdate(SwapPlan plan)
{
    public int Finish()
    {
        var swap = new UpdateSwap(UpdateFiles.Instance, new WindowsLauncher(), line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Update: {line}"));
        var result = swap.Run(plan);
        return result == SwapResult.Failed ? 1 : 0;
    }
}
