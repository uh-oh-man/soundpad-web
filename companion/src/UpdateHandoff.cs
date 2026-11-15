using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace SoundpadWeb;

/// <summary>
/// update-handoff.v1.bin (2.44): the hand-over from the companion that updates to the one that continues its session.
/// UTF-8 JSON of <see cref="UpdateHandoff"/>, at most 1 MiB, protected with DPAPI for the current Windows user (it
/// carries the loopback token and the invitation's private key). It lives a few seconds: the new process renames it to
/// .consumed when it reads it and deletes that once it is running, and every normal start deletes any it finds.
/// Older companions never read it.
/// </summary>
public static class UpdateHandoffFile
{
    public const string Name = "update-handoff.v1.bin";
    public const int MaxJsonBytes = 1024 * 1024;
    /// <summary>DPAPI adds a header; anything much larger than the JSON limit is not ours.</summary>
    public const int MaxFileBytes = MaxJsonBytes + 64 * 1024;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SoundpadWeb update hand-over v1");

    public static byte[] Protect(UpdateHandoff handoff)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(handoff, Json.Options);
        if (json.Length > MaxJsonBytes) throw new InvalidDataException("The session is too large to hand over.");
        return ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
    }

    public static void Write(string dataDir, UpdateHandoff handoff) => UpdateFiles.WriteAtomic(Path.Combine(dataDir, Name), Protect(handoff));

    /// <summary>
    /// Reads and checks the hand-over for <paramref name="id"/>: format 1, at most 5 minutes old, written for this data
    /// folder, and by a process that has ended or is the one waiting for this start (<paramref name="waitPid"/>, the old
    /// companion's watchdog). A good one is renamed to .consumed; anything else is deleted and null returned, with why.
    /// </summary>
    public static UpdateHandoff? TryConsume(string dataDir, string? id, int? waitPid, Func<long> utcNow, Func<int, bool> running, out string? problem)
    {
        var path = Path.Combine(dataDir, Name);
        problem = null;
        UpdateHandoff? handoff = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { problem = "There is no update hand-over."; return null; }
            if (info.Length > MaxFileBytes) problem = "The update hand-over is too large.";
            else
            {
                byte[] json;
                try { json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser); }
                catch (CryptographicException) { problem = "The update hand-over could not be read (another Windows user, or damaged)."; json = []; }
                if (problem == null)
                {
                    if (json.Length > MaxJsonBytes) problem = "The update hand-over is too large.";
                    else
                    {
                        using (var doc = JsonDocument.Parse(json))
                            if (!doc.RootElement.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.Number || f.GetInt32() != 1) problem = "The update hand-over has an unknown format.";
                        if (problem == null) handoff = JsonSerializer.Deserialize<UpdateHandoff>(json, Json.Options);
                    }
                }
            }
            long now = utcNow();
            if (problem == null && handoff == null) problem = "The update hand-over is empty.";
            else if (problem == null && (handoff!.Format != 1)) problem = "The update hand-over has an unknown format.";
            else if (problem == null && (now - handoff!.CreatedUtcMs > (long)MaxAge.TotalMilliseconds || handoff.CreatedUtcMs > now + 60000)) problem = "The update hand-over is too old.";
            else if (problem == null && !SameFolder(handoff!.DataDir, dataDir)) problem = "The update hand-over belongs to another data folder.";
            else if (problem == null && id != null && handoff!.Id != id) problem = "The update hand-over belongs to another update.";
            else if (problem == null && handoff!.OldPid != waitPid && running(handoff.OldPid)) problem = "The companion that wrote the update hand-over is still running.";
            else if (problem == null && (handoff!.Server is null || handoff.Bridge is null || handoff.Server.Token.Length != 64 || !handoff.Server.Token.All(Uri.IsHexDigit) || handoff.Server.Port is < 1 or > 65535)) problem = "The update hand-over is incomplete.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            problem = "The update hand-over could not be read: " + ex.Message;
        }
        if (problem != null)
        {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }
        try { File.Move(path, path + ".consumed", true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { try { File.Delete(path); } catch (Exception) { } }
        return handoff;
    }

    /// <summary>The new process is running: the consumed hand-over (the token and key it carried) goes.</summary>
    public static void Finish(string dataDir)
    {
        var path = Path.Combine(dataDir, Name + ".consumed");
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(25); }
        }
    }

    public static bool SameFolder(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>True while a process with this id exists.</summary>
    public static bool Running(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
