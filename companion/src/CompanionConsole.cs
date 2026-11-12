using System.Runtime.InteropServices;
using System.Text;
namespace SoundpadWeb;

/// <summary>
/// The companion is a desktop app since 2.36, so Windows gives it no console of its own. Diagnostic flags and scripts
/// still need one: redirected handles (scripts, CI) are used as they are, a terminal that started the EXE is joined,
/// and --no-window (or the fallback when WebView2 is missing) opens the same console window 2.35 had.
/// </summary>
internal static class ConsoleLink
{
    private const int StdInput = -10, StdOutput = -11, StdError = -12, AttachParent = -1;
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(int process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int which, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>Standard input came from whoever started the EXE (a script or test): Enter there stops the companion.</summary>
    public static bool InputRedirected { get; private set; }
    /// <summary>Standard output goes somewhere (a pipe, a file or a console).</summary>
    public static bool HasOutput { get; private set; }
    /// <summary>This process opened its own console window (Enter or the window's X stops it, as in 2.35).</summary>
    public static bool OwnConsole { get; private set; }
    /// <summary>Enter on standard input stops the companion: a script's pipe or our own console, never a shell's keyboard.</summary>
    public static bool ReadsEnter => InputRedirected || OwnConsole;

    private static bool Usable(int which) { var handle = GetStdHandle(which); return handle != 0 && handle != -1 && GetFileType(handle) != 0; }

    /// <summary>Runs first, before anything touches Console.</summary>
    public static void Start()
    {
        InputRedirected = Usable(StdInput);
        bool output = Usable(StdOutput), error = Usable(StdError);
        // Started from a terminal: print there. Its keyboard stays the shell's, so Enter is not read from it.
        if ((!output || !error) && AttachConsole(AttachParent)) Adopt(!output, !error, false);
        HasOutput = Usable(StdOutput);
    }

    /// <summary>Gives a process that has no console the window 2.35 always had (--no-window, or the WebView2 fallback).</summary>
    public static void EnsureWindow()
    {
        if (HasOutput || InputRedirected || !AllocConsole()) return;
        OwnConsole = true; HasOutput = true;
        Adopt(true, true, true);
    }

    private static void Adopt(bool output, bool error, bool input)
    {
        const uint Read = 0x80000000, Write = 0x40000000, ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
        if (output || error)
        {
            var screen = CreateFile("CONOUT$", Read | Write, ShareRead | ShareWrite, 0, OpenExisting, 0, 0);
            if (screen == -1) return;
            TextWriter? o = null, e = null;
            if (output) { SetStdHandle(StdOutput, screen); o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }; }
            if (error) { SetStdHandle(StdError, screen); e = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true }; }
            CompanionLog.UseConsole(o, e);
        }
        if (input)
        {
            var keyboard = CreateFile("CONIN$", Read | Write, ShareRead | ShareWrite, 0, OpenExisting, 0, 0);
            if (keyboard != -1) { SetStdHandle(StdInput, keyboard); Console.SetIn(new StreamReader(Console.OpenStandardInput())); }
        }
    }
}

/// <summary>
/// Everything the companion prints also goes to &lt;data&gt;\companion.log (2.36), since a desktop app usually has no
/// console to read. The file stays small: past 1 MiB it moves to companion.1.log and starts again. It never holds the
/// loopback token or the window's owner key, because nothing prints them.
/// </summary>
internal sealed class CompanionLog : TextWriter
{
    public const long Limit = 1 << 20;
    private static readonly object fileLock = new();
    private static string? path;
    private static TextWriter? output, errors;
    private readonly bool error;
    private readonly StringBuilder line = new();
    private CompanionLog(bool error) { this.error = error; }
    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Tees Console.Out and Console.Error into companion.log in <paramref name="directory"/>.</summary>
    public static void Start(string directory)
    {
        if (path != null) return;
        output ??= Console.Out; errors ??= Console.Error;
        path = Path.Combine(directory, "companion.log");
        Console.SetOut(Synchronized(new CompanionLog(false)));
        Console.SetError(Synchronized(new CompanionLog(true)));
        Append(false, $"--- Soundpad Web {typeof(CompanionLog).Assembly.GetName().Version?.ToString(3)} started ---");
    }

    /// <summary>A console that appeared after startup (attached or opened) takes over the console side.</summary>
    public static void UseConsole(TextWriter? newOutput, TextWriter? newErrors)
    {
        if (path == null) { if (newOutput != null) Console.SetOut(newOutput); if (newErrors != null) Console.SetError(newErrors); }
        if (newOutput != null) output = newOutput;
        if (newErrors != null) errors = newErrors;
    }

    public override void Write(char value) => Write(value.ToString());
    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try { (error ? errors : output)?.Write(value); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        foreach (var c in value)
        {
            if (c != '\n') { line.Append(c); continue; }
            var text = line.ToString().TrimEnd('\r'); line.Clear(); Append(error, text);
        }
    }
    public override void Flush() { try { (error ? errors : output)?.Flush(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }

    private static void Append(bool error, string text)
    {
        if (path == null) return;
        lock (fileLock)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > Limit) File.Move(path, Path.Combine(info.DirectoryName!, "companion.1.log"), true);
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                file.Write(Encoding.UTF8.GetBytes($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {(error ? "error: " : "")}{text}{Environment.NewLine}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the log is a convenience; never fail the companion over it */ }
        }
    }
}
