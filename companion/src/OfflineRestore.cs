using System.Text.Json;
using System.Text.Json.Nodes;
namespace SoundpadWeb;

/// <summary>
/// <c>SoundpadWeb.exe --restore-state "&lt;snapshot folder&gt;" [--data-dir &lt;folder&gt;]</c> (2.44): puts a backup's
/// state.json back while the companion is not running, for a state.json that became unreadable. It checks the
/// snapshot's manifest, the file's hash and the state's own rules first, refuses while another companion holds the
/// data folder's lock, keeps the current state.json as state.before-restore-&lt;utc&gt;.json, then writes the backup's
/// copy durably. It also puts back icons the state names that are missing. No window, no Soundpad pipe, no network.
/// Exit code 0 when restored, 1 otherwise; one result line either way.
/// </summary>
public static class OfflineRestore
{
    public static int Run(string[] args)
    {
        string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var snapshot = Arg("--restore-state");
        var data = Arg("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb");
        try
        {
            var message = Restore(snapshot, data);
            Console.WriteLine("Restored: " + message);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Console.WriteLine("Not restored: " + (ex is InvalidDataException ? ex.Message : ex.GetType().Name + ": " + ex.Message));
            return 1;
        }
    }

    /// <summary>The restore itself (tests call it directly). Returns the result line; throws the reason it refused.</summary>
    public static string Restore(string? snapshotFolder, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(snapshotFolder) || !Path.IsPathFullyQualified(snapshotFolder)) throw new InvalidDataException("Give the backup's folder, such as \"D:\\Backups\\Soundpad Web Backups\\…\\snapshots\\20261114T120000Z-1a2b3c4d\".");
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshotFolder));
        var name = Path.GetFileName(folder);
        var manifestPath = Path.Combine(folder, BackupManifest.FileName);
        if (!File.Exists(manifestPath)) throw new InvalidDataException("That folder is not a backup (it has no " + BackupManifest.FileName + ").");
        if (new FileInfo(manifestPath).Length > 32L * 1024 * 1024) throw new InvalidDataException("The backup's manifest is too large.");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllBytes(manifestPath), Json.Options) ?? throw new InvalidDataException("The backup's manifest is empty.");
        BackupManifest.Validate(manifest, name);
        var doc = manifest.Files.FirstOrDefault(f => f.Role == "state") ?? throw new InvalidDataException("That backup has no state.json.");
        var bytes = File.ReadAllBytes(Path.Combine(folder, "data", doc.Path));
        if (bytes.Length != doc.Bytes || BackupRepository.Sha(bytes) != doc.Sha256) throw new InvalidDataException("The backup's state.json does not match its manifest; it is damaged.");
        var state = Bridge.ParseBackupState(bytes);
        Directory.CreateDirectory(dataDirectory);
        FileStream held;
        try { held = new FileStream(Path.Combine(dataDirectory, "companion.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidDataException("Soundpad Web is running. Quit it first, then run this again."); }
        using (held)
        {
            var target = Path.Combine(dataDirectory, "state.json");
            string kept = "";
            if (File.Exists(target))
            {
                kept = Path.Combine(dataDirectory, $"state.before-restore-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
                File.Copy(target, kept, false);
            }
            DurableFile.Write(target, bytes);
            // Icons the state names that are missing, from the backup's blobs (never over an existing file).
            int icons = 0;
            var blobs = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!, "blobs");
            foreach (var file in state.IconFiles.Values.Distinct(StringComparer.Ordinal))
            {
                var path = Path.Combine(dataDirectory, "icons", file);
                if (Path.GetFileName(file) != file || File.Exists(path)) continue;
                if (manifest.Blobs.FirstOrDefault(b => b.Role == "icon" && b.Original == file) is not { } blob) continue;
                var source = Path.Combine(blobs, blob.Sha256[..2], blob.Sha256 + "." + blob.Ext);
                if (!File.Exists(source)) continue;
                var icon = File.ReadAllBytes(source);
                if (BackupRepository.Sha(icon) != blob.Sha256) continue;
                DurableFile.Write(path, icon); icons++;
            }
            return $"state.json from the backup of {DateTimeOffset.FromUnixTimeMilliseconds(manifest.CreatedUtcMs).UtcDateTime:yyyy-MM-dd HH:mm} UTC" + (kept.Length > 0 ? $"; the previous one is kept as {Path.GetFileName(kept)}" : "") + (icons > 0 ? $"; {icons} icon(s) put back" : "") + ".";
        }
    }
}

/// <summary>
/// Finds the newest backup's copy of state.json for the message shown when the state is unreadable (2.44). Reads only;
/// never throws.
/// </summary>
public static class BackupLocator
{
    /// <summary>The snapshot folder of the newest backup with a valid state.json, or null.</summary>
    public static string? NewestState(string dataDirectory)
    {
        try
        {
            var settingsPath = Path.Combine(dataDirectory, "backup-settings.v1.json");
            if (!File.Exists(settingsPath) || new FileInfo(settingsPath).Length > BackupSettingsStore.MaxBytes) return null;
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject o || BackupSettingsStore.Parse(o).Folder is not { } folder) return null;
            var root = BackupFolders.Root(folder);
            if (!Directory.Exists(root)) return null;
            // The install's own folders first (by the kept-audio journal's app id, which is readable on its own).
            string? appId = null;
            try
            {
                var journal = Path.Combine(dataDirectory, "managed-audio", "journal.json");
                if (File.Exists(journal) && new FileInfo(journal).Length <= 4 * 1024 * 1024 && JsonNode.Parse(File.ReadAllText(journal))?["appId"]?.GetValue<string>() is { Length: >= 6 } id) appId = id[..6];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
            var candidates = new DirectoryInfo(root).EnumerateDirectories()
                .Where(d => System.Text.RegularExpressions.Regex.IsMatch(d.Name, "^[a-f0-9]{6}-[a-f0-9]{8}$") && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(d => appId != null && d.Name.StartsWith(appId, StringComparison.Ordinal));
            foreach (var install in candidates)
            {
                var repo = new BackupRepository(install.FullName, new BackupFiles());
                foreach (var name in repo.SnapshotFolders())
                {
                    if (repo.Read(name, out _) is not { } m || m.Files.FirstOrDefault(f => f.Role == "state") is not { } doc) continue;
                    try { repo.ReadDoc(name, doc); return repo.SnapshotPath(name); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return null;
    }

    /// <summary>The sentence the unreadable-state message gains when a backup has a copy.</summary>
    public static string Hint(string dataDirectory)
    {
        if (NewestState(dataDirectory) is not { } snapshot) return "";
        return $" A copy from your newest backup is at {Path.Combine(snapshot, "data")}\\state.json; quit, copy it over {Path.Combine(dataDirectory, "state.json")}, and start again. Or run SoundpadWeb.exe --restore-state \"{snapshot}\".";
    }
}
