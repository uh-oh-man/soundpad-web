using System.Text.Json;
using System.Text.Json.Nodes;
namespace SoundpadWeb;

// Backup settings (2.44, gate backups). See docs/BACKUPS.md. They live in <data>\backup-settings.v1.json, a new
// versioned file that older companions do not know and ignore. Backups are off until the host chooses a folder.

/// <summary>How the last backup run ended (shown in Settings).</summary>
public sealed record BackupLastResult(long AtUtcMs, bool Ok, long Bytes, int Snapshots, string Message);

/// <summary>The host's backup choices. <see cref="Folder"/> null means backups are off.</summary>
public sealed record BackupSettings(string? Folder = null, int KeepCount = 14, int KeepDays = 30, int IntervalHours = 24, int AfterChangeMinutes = 10, bool IncludeNativeFiles = true, BackupLastResult? LastResult = null)
{
    public const int MinKeepCount = 1, MaxKeepCount = 200, MinKeepDays = 1, MaxKeepDays = 3650, MinIntervalHours = 1, MaxIntervalHours = 168, MaxAfterChangeMinutes = 1440;
    public bool Enabled => Folder != null;
}

/// <summary>
/// <c>backup-settings.v1.json</c>: at most 64 KiB, every known field present and in range, unknown fields kept as they
/// are. A file that fails any of that means backups are off: it is left untouched and its problem is reported, and
/// nothing is written over it until the host removes or repairs it.
/// </summary>
public sealed class BackupSettingsStore(string path)
{
    public const int MaxBytes = 64 * 1024;
    private static readonly string[] Known = ["format", "folder", "keepCount", "keepDays", "intervalHours", "afterChangeMinutes", "includeNativeFiles", "lastResult"];
    private readonly object sync = new();
    private JsonObject? raw;
    private BackupSettings? current;
    public string PathName => path;
    /// <summary>Why the file could not be used (it is then left as it is), or null.</summary>
    public string? Problem { get; private set; }

    public BackupSettings Current { get { lock (sync) { Load(); return current!; } } }

    private void Load()
    {
        if (current != null) return;
        current = new(); raw = null; Problem = null;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("it is larger than 64 KiB");
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("it is not an object");
            current = Parse(node); raw = node;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            current = new();
            Problem = $"The backup settings file could not be read ({(ex is InvalidDataException ? ex.Message : ex.GetType().Name)}). Backups are off; the file was left untouched: {path}";
        }
    }

    private static T Field<T>(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var node)) throw new InvalidDataException("a field is missing: " + name);
        try { return node == null ? default! : node.GetValue<T>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new InvalidDataException("a field has the wrong type: " + name); }
    }

    /// <summary>Strict: every known field present, typed and in range.</summary>
    public static BackupSettings Parse(JsonObject o)
    {
        void Require(bool ok, string what) { if (!ok) throw new InvalidDataException(what); }
        Require(o.TryGetPropertyValue("format", out var format) && format is JsonValue && format.GetValueKind() == JsonValueKind.Number && format.GetValue<int>() == 1, "unknown format");
        if (!o.TryGetPropertyValue("folder", out var f)) throw new InvalidDataException("a field is missing: folder");
        string? folder = f == null ? null : f.GetValueKind() == JsonValueKind.String ? f.GetValue<string>() : throw new InvalidDataException("a field has the wrong type: folder");
        Require(folder == null || folder.Length is > 0 and <= 1024 && Path.IsPathFullyQualified(folder) && !folder.StartsWith(@"\\", StringComparison.Ordinal) && !folder.Any(char.IsControl), "bad folder");
        int keepCount = Field<int>(o, "keepCount"), keepDays = Field<int>(o, "keepDays"), interval = Field<int>(o, "intervalHours"), after = Field<int>(o, "afterChangeMinutes");
        Require(keepCount is >= BackupSettings.MinKeepCount and <= BackupSettings.MaxKeepCount && keepDays is >= BackupSettings.MinKeepDays and <= BackupSettings.MaxKeepDays && interval is >= BackupSettings.MinIntervalHours and <= BackupSettings.MaxIntervalHours && after is >= 0 and <= BackupSettings.MaxAfterChangeMinutes, "a value is out of range");
        if (!o.TryGetPropertyValue("includeNativeFiles", out var native) || native?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("a field is missing: includeNativeFiles");
        BackupLastResult? last = null;
        if (!o.TryGetPropertyValue("lastResult", out var lastNode)) throw new InvalidDataException("a field is missing: lastResult");
        if (lastNode != null)
        {
            var l = lastNode as JsonObject ?? throw new InvalidDataException("bad last result");
            var message = Field<string>(l, "message");
            last = new(Field<long>(l, "atUtcMs"), Field<bool>(l, "ok"), Field<long>(l, "bytes"), Field<int>(l, "snapshots"), message ?? "");
            Require(last.AtUtcMs >= 0 && last.Bytes >= 0 && last.Snapshots >= 0 && message is { Length: <= 600 } && !message.Any(char.IsControl), "bad last result");
            Require(l.Count == 5, "bad last result");
        }
        return new(folder, keepCount, keepDays, interval, after, native!.GetValue<bool>(), last);
    }

    /// <summary>Writes the settings (temporary file, flush, rename), keeping fields this version does not know.</summary>
    public void Save(BackupSettings next)
    {
        lock (sync)
        {
            Load();
            if (Problem != null) throw new InvalidDataException(Problem);
            var o = raw != null ? (JsonObject)raw.DeepClone() : new JsonObject();
            foreach (var key in Known) o.Remove(key);
            var fresh = new JsonObject
            {
                ["format"] = 1, ["folder"] = next.Folder, ["keepCount"] = next.KeepCount, ["keepDays"] = next.KeepDays, ["intervalHours"] = next.IntervalHours,
                ["afterChangeMinutes"] = next.AfterChangeMinutes, ["includeNativeFiles"] = next.IncludeNativeFiles,
                ["lastResult"] = next.LastResult == null ? null : new JsonObject { ["atUtcMs"] = next.LastResult.AtUtcMs, ["ok"] = next.LastResult.Ok, ["bytes"] = next.LastResult.Bytes, ["snapshots"] = next.LastResult.Snapshots, ["message"] = next.LastResult.Message.Length > 600 ? next.LastResult.Message[..600] : next.LastResult.Message },
            };
            foreach (var (key, value) in o.ToArray()) { o.Remove(key); fresh[key] = value; }
            Parse(fresh);
            var bytes = System.Text.Encoding.UTF8.GetBytes(fresh.ToJsonString());
            if (bytes.Length > MaxBytes) throw new InvalidDataException("The backup settings are too large.");
            DurableFile.Write(path, bytes);
            raw = fresh; current = next;
        }
    }
}

/// <summary>Small files written durably: temporary file, flush to disk, rename (with the virus-scanner retries of state.json).</summary>
internal static class DurableFile
{
    public static void Write(string path, ReadOnlySpan<byte> bytes) => Write(path, bytes.ToArray());
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
                break;
            }
            catch (IOException) when (attempt < 5) { temporary = path + "." + attempt + ".tmp"; Thread.Sleep(20); }
        }
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, path, true); break; }
            catch (Exception ex) when (attempt < 40 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(25); }
            catch { try { File.Delete(temporary); } catch (Exception) { } throw; }
        }
    }
}

/// <summary>Where backups may go (2.44): the host's folder is checked against these rules before it is saved and before every run.</summary>
public static class BackupFolders
{
    /// <summary>The folder "Use suggested folder" offers: Documents\Soundpad Web Backups.</summary>
    public static string Suggested() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Soundpad Web Backups");
    /// <summary>Soundpad's own folder, never written to (only compared with).</summary>
    public static string Leppsoft() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Leppsoft");
    /// <summary>The name of the folder the backups of every install go in, inside the chosen folder.</summary>
    public const string RootName = "Soundpad Web Backups";

    /// <summary>The chosen folder's backup root: the chosen folder itself when it is already called Soundpad Web Backups.</summary>
    public static string Root(string folder) => string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), RootName, StringComparison.OrdinalIgnoreCase) ? folder : Path.Combine(folder, RootName);

    private static bool Inside(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(full, parent, StringComparison.OrdinalIgnoreCase) || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the folder or one of its existing parents (below the drive root) is a link or junction.</summary>
    public static bool Linked(string folder)
    {
        for (var d = new DirectoryInfo(folder); d != null && d.Parent != null; d = d.Parent)
            if (d.Exists && (d.Attributes.HasFlag(FileAttributes.ReparsePoint) || d.LinkTarget != null)) return true;
        return false;
    }

    /// <summary>
    /// Checks the folder (fully qualified, local, an existing drive, not a link, not inside or around the data folder,
    /// Soundpad's folder or the Keep folder, writable) and returns it normalised. Throws the reason shown to the host.
    /// </summary>
    public static string Check(string input, string dataDirectory, string leppsoft, string? keepRoot, bool probe = true)
    {
        var folder = (input ?? "").Trim();
        if (folder.Length is 0 or > 1024 || folder.Any(char.IsControl) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new InvalidDataException("Type a folder such as D:\\Backups.");
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)) throw new InvalidDataException("Choose a folder on this PC, such as D:\\Backups (not a network path).");
        try { folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new InvalidDataException("Type a folder such as D:\\Backups."); }
        var root = Path.GetPathRoot(folder);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) throw new InvalidDataException($"The drive {root} is not there.");
        if (string.Equals(Path.TrimEndingDirectorySeparator(root), folder, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a folder, not a whole drive.");
        foreach (var (other, name) in new[] { (dataDirectory, "the companion's data folder"), (leppsoft, "Soundpad's own folder"), (keepRoot, "the Keep folder") })
        {
            if (string.IsNullOrEmpty(other)) continue;
            if (Inside(folder, other) || Inside(other, folder)) throw new InvalidDataException($"Choose a folder outside {name} (and not one that contains it).");
        }
        if (Linked(folder)) throw new InvalidDataException("Choose a real folder; links and junctions are not used for backups.");
        if (probe)
        {
            try
            {
                Directory.CreateDirectory(folder);
                if (Linked(folder)) throw new InvalidDataException("Choose a real folder; links and junctions are not used for backups.");
                var test = Path.Combine(folder, ".soundpad-web-probe-" + Guid.NewGuid().ToString("N"));
                using (var stream = new FileStream(test, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) stream.WriteByte(1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException("The companion cannot write to that folder. Choose another one."); }
        }
        return folder;
    }
}
