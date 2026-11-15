namespace SoundpadWeb;

/// <summary>
/// Why Soundpad can't be reached (2.44, gate setup), for the setup guide and the connection banner:
/// <list type="bullet">
/// <item><c>accessDenied</c>: Windows refused the pipe, as when one of the two apps runs as administrator;</item>
/// <item><c>oldVersion</c>: Soundpad answered but does not know categories (R-404);</item>
/// <item><c>notRunning</c>: no Soundpad process exists;</item>
/// <item><c>noAnswer</c>: Soundpad runs but the pipe timed out, closed or answered something unreadable.</item>
/// </list>
/// Whether Soundpad runs is asked only when it matters, at most every 2 s, and only by listing process names
/// (read-only; tests inject the answer). The snapshot's <c>error</c> text keeps the strings of older versions.
/// </summary>
public sealed class SoundpadDiagnosis(Func<bool>? soundpadRunning = null)
{
    public const string AccessDenied = "accessDenied", OldVersion = "oldVersion", NotRunning = "notRunning", NoAnswer = "noAnswer";
    /// <summary>The message RefreshInner throws for an R-404 on categories.</summary>
    public const string OldVersionMessage = "This Soundpad version does not support categories. Update Soundpad.";
    private readonly Func<bool> running = soundpadRunning ?? DefaultRunning;
    private long checkedAt = long.MinValue / 2;
    private bool lastRunning;

    /// <summary>Read-only: true when a process named Soundpad exists in this session's view.</summary>
    public static bool DefaultRunning()
    {
        var found = System.Diagnostics.Process.GetProcessesByName("Soundpad");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    /// <summary>The problem a failed Soundpad request shows.</summary>
    public string Classify(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return AccessDenied;
        if (ex is InvalidDataException && ex.Message == OldVersionMessage) return OldVersion;
        return Running() ? NoAnswer : NotRunning;
    }

    private bool Running()
    {
        if (soundpadRunning != null) return soundpadRunning(); // injected (tests, the harness): asked every time
        long now = Environment.TickCount64;
        if (now - checkedAt < 2000) return lastRunning;
        try { lastRunning = running(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { lastRunning = false; }
        checkedAt = now;
        return lastRunning;
    }

    /// <summary>True when this companion runs elevated (as administrator).</summary>
    public static bool Elevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }
}
