using System.Diagnostics;
namespace SoundpadWeb.Testing;

/// <summary>
/// What every real-Soundpad probe shares (2.44 real-hardware proof): where its files go, the refusals, and the
/// careful wait before anything is removed from Soundpad's list.
/// </summary>
internal static class RealProbes
{
    /// <summary>
    /// Stopped, then still stopped this long later, before a probe removes a category. Soundpad 4.0.35 exited once when
    /// a category was removed right after one of its sounds played (2026-11-11); the companion's own rule is one second
    /// (<see cref="QuietRule"/>), and the probes wait twice that.
    /// </summary>
    public const int QuietMs = 2000;

    /// <summary>
    /// "--data-dir &lt;folder&gt;": the folder a probe keeps its files and throwaway companion data in (created; it must be
    /// inside the temp folder), in long form, because Soundpad lists every path in long form and Windows can hand out
    /// TEMP in its 8.3 short form. Without it, the temp folder itself, as before.
    /// </summary>
    public static string Root(string[] args)
    {
        int at = Array.IndexOf(args, "--data-dir");
        if (at < 0 || at + 1 >= args.Length) return SlotFiles.LongPath(Path.GetTempPath());
        var full = Path.GetFullPath(args[at + 1]);
        Directory.CreateDirectory(full);
        var longForm = SlotFiles.LongPath(full);
        var temp = SlotFiles.LongPath(Path.GetFullPath(Path.GetTempPath()));
        if (!longForm.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("--data-dir must be inside the temp folder (" + temp + ").");
        return longForm;
    }

    /// <summary>The probe's refusals: never while the companion or VRChat runs, or Soundpad is closed, unreachable or busy.</summary>
    public static async Task<string?> Refusal(ISoundpad pipe)
    {
        if (Process.GetProcessesByName("SoundpadWeb").Length > 0) return "the companion is running. Close it first.";
        if (Process.GetProcessesByName("VRChat").Length > 0) return "VRChat is running; this probe waits for a quiet moment.";
        if (Process.GetProcessesByName("Soundpad").Length == 0) return "Soundpad is not running.";
        string status;
        try { status = await pipe.Request("GetPlayStatus()"); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { return "Soundpad is not reachable (" + ex.GetType().Name + ")."; }
        return status == "STOPPED" ? null : "Soundpad is busy (" + status + ").";
    }

    /// <summary>Stops Soundpad if needed, then waits until it has stayed stopped for <see cref="QuietMs"/>. False when it never did.</summary>
    public static async Task<bool> StoppedAndQuiet(ISoundpad pipe, int quietMs = QuietMs)
    {
        for (int i = 0; i < 20; i++)
        {
            if (await pipe.Request("GetPlayStatus()") != "STOPPED") { await pipe.Request("DoStopSound()"); await Task.Delay(300); continue; }
            await Task.Delay(quietMs);
            if (await pipe.Request("GetPlayStatus()") == "STOPPED") return true;
        }
        return false;
    }

    /// <summary>Soundpad's list as the probes compare it: every entry's path and the categories' names in order.</summary>
    public static bool Same(NativeCatalog a, NativeCatalog b) =>
        a.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).SequenceEqual(b.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
        && a.Categories.OrderBy(c => c.Index).Select(c => c.Name).SequenceEqual(b.Categories.OrderBy(c => c.Index).Select(c => c.Name));

    /// <summary>Deletes a probe's folder, retrying while Soundpad or a decoder still holds a file in it.</summary>
    public static async Task Delete(string folder, TextWriter log)
    {
        for (int i = 0; i < 40 && Directory.Exists(folder); i++) { try { Directory.Delete(folder, true); } catch (IOException) { await Task.Delay(150); } catch (UnauthorizedAccessException) { await Task.Delay(150); } }
        if (Directory.Exists(folder)) log.WriteLine("probe folder still in use: " + folder);
    }
}
