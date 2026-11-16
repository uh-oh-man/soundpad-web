using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

/// <summary>
/// A Stage H command's body (2.44), read strictly: an object with only the fields the command knows, each of its type
/// and range. Anything else refuses the command with the reason.
/// </summary>
internal sealed class CommandBody
{
    private readonly JsonElement body;
    private CommandBody(JsonElement body) => this.body = body;
    public static CommandBody Of(Command c, params string[] allowed)
    {
        if (c.Body is not { ValueKind: JsonValueKind.Object } body) throw new InvalidDataException("Send the request's details.");
        foreach (var p in body.EnumerateObject()) if (!allowed.Contains(p.Name)) throw new InvalidDataException("That request has a field this companion does not know: " + (p.Name.Length > 40 ? p.Name[..40] : p.Name) + ".");
        return new(body);
    }
    public bool Has(string name) => body.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;
    public string? Str(string name, int max)
    {
        if (!body.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} must be text.");
        var s = v.GetString()!;
        if (s.Length > max || s.Any(char.IsControl)) throw new InvalidDataException($"{name} is too long or has control characters.");
        return s;
    }
    public string? Enum(string name, params string[] values) { var s = Str(name, 40); if (s != null && !values.Contains(s)) throw new InvalidDataException($"{name} must be one of {string.Join(", ", values)}."); return s; }
    public int? Int(string name, int min, int max)
    {
        if (!body.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max) throw new InvalidDataException($"{name} must be a whole number from {min} to {max}.");
        return n;
    }
    public bool? Bool(string name)
    {
        if (!body.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException($"{name} must be true or false.") };
    }
    public string[]? Ids(string name, int max, Regex pattern)
    {
        if (!body.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > max) throw new InvalidDataException($"Choose at most {max} items.");
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } id || !pattern.IsMatch(id)) throw new InvalidDataException("An item is not valid.");
            list.Add(id);
        }
        return [.. list.Distinct(StringComparer.Ordinal)];
    }
    public JsonElement? Raw(string name) => body.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;
}

// Backups and restore: the commands (2.44, gate backups). Host only: a guest or a relayed command is refused here, in the
// companion, whatever the page did. Guests never see backup paths, the list or the settings.
public sealed partial class Bridge
{
    private static readonly string[] BackupOps = ["backupSettings", "backupNow", "backupList", "backupDetail", "backupRestore", "backupAttach", "backupLayout"];
    /// <summary>Guest commands that change the voice library or sentences: refused while the host restores the whole library.</summary>
    private static bool LibraryWrite(Command c) => c.Op is "libraryEdit" or "libraryRemove" or "libraryUndo" or "libraryVoice" or "libraryWanted" or "libraryAdd" or "libraryKeep" or "libraryRetry" or "speakerName" or "speakerLink"
        or "sentenceCreate" or "sentenceText" or "sentenceEdit" or "sentenceDiscard" or "sentenceKeep" or "paletteMark" or "performance" or "performanceSets" || c.Op == "draftKeep" && c.Action == "library";
    private const string WorkspaceFile = "backup-workspace.v1.json";
    private const int MaxWorkspaceBytes = 2 * 1024 * 1024, MaxWorkspaceFileBytes = 5 * 1024 * 1024;

    private Task<Reply>? ExecuteBackups(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        if (restoringLibrary && actor != "host" && LibraryWrite(c)) return Task.FromResult(new Reply("ack", c.Id, false, "The host is restoring the library. Try again in a moment."));
        if (!BackupOps.Contains(c.Op)) return null;
        // Off the socket's receive loop at once: every one of them reads files or waits for a gate.
        return Task.Run(async () =>
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
                if (actor != "host") throw new InvalidDataException("Host permission required.");
                return c.Op switch
                {
                    "backupSettings" => new Reply("ack", c.Id, true, Data: BackupSettingsCommand(c)),
                    "backupNow" => new Reply("ack", c.Id, true, Data: BackupNow(c)),
                    "backupList" => new Reply("ack", c.Id, true, Data: BackupList(c)),
                    "backupDetail" => new Reply("ack", c.Id, true, Data: BackupDetail(c)),
                    "backupRestore" => new Reply("ack", c.Id, true, Data: await BackupRestore(c)),
                    "backupAttach" => new Reply("ack", c.Id, true, Data: BackupAttach(c)),
                    "backupLayout" => new Reply("ack", c.Id, true, Data: BackupLayout(c)),
                    _ => throw new InvalidDataException("Unsupported command."),
                };
            }
            catch (InvalidDataException ex) { return new Reply("ack", c.Id, false, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Reply("ack", c.Id, false, "The backup folder could not be used: " + ex.Message); }
        });
    }

    // ---- Settings and status -----------------------------------------------------------------------------------------

    private object BackupSettingsCommand(Command c)
    {
        var body = CommandBody.Of(c, "action", "folder", "useSuggested", "keepCount", "keepDays", "intervalHours", "afterChangeMinutes", "includeNativeFiles");
        var action = body.Enum("action", "get", "set") ?? throw new InvalidDataException("Get or set the backup settings.");
        if (action == "set")
        {
            if (BackupStore.Problem is { } problem) throw new InvalidDataException(problem + " Remove or repair it, then try again.");
            var current = BackupStore.Current;
            string? folder = current.Folder;
            if (body.Bool("useSuggested") == true) folder = CheckBackupFolder(suggestedBackupFolder);
            else if (body.Str("folder", 1024) is { } typed) folder = typed.Trim().Length == 0 ? null : CheckBackupFolder(typed);
            var next = current with
            {
                Folder = folder,
                KeepCount = body.Int("keepCount", BackupSettings.MinKeepCount, BackupSettings.MaxKeepCount) ?? current.KeepCount,
                KeepDays = body.Int("keepDays", BackupSettings.MinKeepDays, BackupSettings.MaxKeepDays) ?? current.KeepDays,
                IntervalHours = body.Int("intervalHours", BackupSettings.MinIntervalHours, BackupSettings.MaxIntervalHours) ?? current.IntervalHours,
                AfterChangeMinutes = body.Int("afterChangeMinutes", 0, BackupSettings.MaxAfterChangeMinutes) ?? current.AfterChangeMinutes,
                IncludeNativeFiles = body.Bool("includeNativeFiles") ?? current.IncludeNativeFiles,
            };
            BackupStore.Save(next);
            if (next.Folder != current.Folder) Log(next.Folder == null ? "Backups turned off" : "Backups go to the chosen folder now");
            BackupsChanged();
        }
        return BackupStatus();
    }

    private object BackupStatus()
    {
        var s = BackupStore.Current;
        long free = s.Folder is { } folder && Directory.Exists(folder) ? backupFiles.FreeBytes(folder) : -1;
        return new
        {
            settings = new { folder = s.Folder, s.KeepCount, s.KeepDays, s.IntervalHours, s.AfterChangeMinutes, s.IncludeNativeFiles, suggested = suggestedBackupFolder, nativeBytes = NativeLibraryBytes(), problem = BackupStore.Problem },
            status = new { running = backupRunning, lastResult = s.LastResult, totalBytes = s.LastResult?.Bytes ?? 0, snapshots = s.LastResult?.Snapshots ?? 0, freeBytes = free < 0 ? (long?)null : free },
        };
    }

    /// <summary>The size of the Soundpad (and Voice Replay Studio) files the voice library uses, which "Include Soundpad files" backs up.</summary>
    private long NativeLibraryBytes()
    {
        long total = 0;
        try
        {
            foreach (var r in Voices.CopyFile().Recordings.Where(r => r.Source.Kind is "soundpad" or "vrs").DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
                try { var f = new FileInfo(r.Path); if (f.Exists) total += f.Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        catch (InvalidDataException) { }
        return total;
    }

    private object BackupNow(Command c)
    {
        CommandBody.Of(c);
        var s = BackupStore.Current;
        if (BackupStore.Problem is { } problem) throw new InvalidDataException(problem);
        if (!s.Enabled) throw new InvalidDataException("Choose a backup folder first.");
        if (!backupRun.Wait(0)) return new { started = false };
        backupRunning = true; BackupsChanged();
        _ = Task.Run(() => RunAcquired("manual", default));
        return new { started = true };
    }

    private static readonly Regex SnapshotId = BackupManifest.SnapshotName;

    private object BackupList(Command c)
    {
        var body = CommandBody.Of(c, "limit", "before");
        int limit = body.Int("limit", 1, 100) ?? 50;
        var before = body.Str("before", 40);
        if (before != null && !SnapshotId.IsMatch(before)) throw new InvalidDataException("Choose a backup.");
        var s = BackupStore.Current;
        var list = new List<object>();
        if (s.Folder is { } folder)
        {
            var names = BackupRepositories(folder).SelectMany(r => r.SnapshotFolders().Select(n => (Repo: r, Name: n)))
                .Where(x => before == null || string.CompareOrdinal(x.Name, before) < 0).OrderByDescending(x => x.Name, StringComparer.Ordinal).Take(limit);
            foreach (var (repo, name) in names)
            {
                bool other = !OwnRepository(repo);
                if (repo.Read(name, out var problem) is { } m) list.Add(new { id = m.Id, kind = m.Kind, createdUtcMs = m.CreatedUtcMs, bytes = m.Bytes, fuzzy = m.Fuzzy, warnings = m.Warnings.Take(20).ToArray(), other });
                else list.Add(new { id = name, kind = "unreadable", createdUtcMs = NameTime(name), bytes = 0L, fuzzy = false, warnings = new[] { "This backup cannot be used: " + problem + "." }, other });
            }
        }
        return new { snapshots = list };
    }

    private static long NameTime(string name) => DateTimeOffset.TryParseExact(name[..16], "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeMilliseconds() : 0;

    // ---- Layouts -----------------------------------------------------------------------------------------------------

    private string WorkspacePath => Path.Combine(store.DirectoryPath, WorkspaceFile);
    private readonly object workspaceGate = new();

    /// <summary>backup-workspace.v1.json: {format:1, pages:{window?, browser?}}, each {savedUtcMs, workspace}. Strict; null when absent or damaged.</summary>
    internal static JsonObject? ParseWorkspaceFile(byte[] bytes)
    {
        if (bytes.Length > MaxWorkspaceFileBytes) return null;
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject o || o.Count != 2 || o["format"]?.GetValueKind() != JsonValueKind.Number || o["format"]!.GetValue<int>() != 1 || o["pages"] is not JsonObject pages) return null;
            foreach (var (key, value) in pages)
                if (key is not ("window" or "browser") || value is not JsonObject page || page.Count != 2 || page["savedUtcMs"]?.GetValueKind() != JsonValueKind.Number || page["workspace"] is not JsonObject) return null;
            return o;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private object BackupAttach(Command c)
    {
        var body = CommandBody.Of(c, "source", "workspace");
        var source = body.Enum("source", "window", "browser") ?? throw new InvalidDataException("Say which page's layout this is.");
        if (body.Raw("workspace") is not { ValueKind: JsonValueKind.Object } workspace) throw new InvalidDataException("Send the layout.");
        var text = workspace.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxWorkspaceBytes) throw new InvalidDataException("The layout is larger than 2 MiB.");
        if (!workspace.TryGetProperty("hostId", out var host) || host.ValueKind != JsonValueKind.String || host.GetString() != HostIdNow) throw new InvalidDataException("That layout belongs to a different host.");
        lock (workspaceGate)
        {
            JsonObject file = new() { ["format"] = 1, ["pages"] = new JsonObject() };
            if (File.Exists(WorkspacePath))
            {
                var parsed = new FileInfo(WorkspacePath).Length <= MaxWorkspaceFileBytes ? ParseWorkspaceFile(File.ReadAllBytes(WorkspacePath)) : null;
                // A damaged copy is set aside once (never deleted), and a fresh one started: it is only a copy of the pages' own layouts.
                if (parsed == null) File.Move(WorkspacePath, Path.Combine(store.DirectoryPath, $"backup-workspace.v1.damaged-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json"), true);
                else file = parsed;
            }
            ((JsonObject)file["pages"]!)[source] = new JsonObject { ["savedUtcMs"] = backupClock().ToUnixTimeMilliseconds(), ["workspace"] = JsonNode.Parse(text) };
            var bytes = System.Text.Encoding.UTF8.GetBytes(file.ToJsonString());
            if (bytes.Length > MaxWorkspaceFileBytes) throw new InvalidDataException("The layouts together are larger than 5 MiB.");
            DurableFile.Write(WorkspacePath, bytes);
        }
        return new { };
    }

    private object BackupLayout(Command c)
    {
        var body = CommandBody.Of(c, "snapshot", "source");
        var id = body.Str("snapshot", 40) ?? throw new InvalidDataException("Choose a backup.");
        var source = body.Enum("source", "window", "browser") ?? throw new InvalidDataException("Choose the window's or the browser's layout.");
        var b = FindBackup(id);
        var bytes = DocBytes(b, "workspace");
        var file = bytes == null ? null : ParseWorkspaceFile(bytes);
        if (file?["pages"]?[source] is not JsonObject page) throw new InvalidDataException(source == "window" ? "That backup has no layout from the companion window." : "That backup has no layout from a browser.");
        return new { workspace = page["workspace"], savedUtcMs = page["savedUtcMs"]!.GetValue<long>() };
    }
}
