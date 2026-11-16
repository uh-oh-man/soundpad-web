using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

/// <summary>
/// One line of the host's Activity (2.45, gate diagnostics): a notice a host page showed (with its technical details
/// for errors) or a guest joining, leaving or waiting. <see cref="Source"/> is "notice", "join", "leave" or "lobby".
/// </summary>
public sealed record ActivityEntry(string Id, string Kind, string Source, string Text, long At, string? Details);

/// <summary>The tail of companion.log (2.45): the last lines, oldest first.</summary>
public sealed record LogTail(bool Exists, string File, long Size, bool Redacted, string[] Lines);

// Diagnostics (2.45, gate diagnostics), host only:
//  - activityAdd / activityList: the host pages' Activity, kept here so it survives a reload of the page. The newest
//    200 entries, in memory until the companion quits (never written to disk, so nothing persisted changes).
//  - diagnosticsLog: the tail of companion.log for Settings, optionally with file paths and the Windows user name
//    hidden (for sharing a screenshot). Guests are refused both; the log stays on the host's PC.
public sealed partial class Bridge
{
    public const int ActivityLimit = 200;
    /// <summary>Most entries one activityAdd may carry.</summary>
    public const int ActivityBatch = 20;
    public const int ActivityTextLimit = 500, ActivityDetailsLimit = 4000;
    /// <summary>A join, leave or waiting line that two host pages both report within this time is kept once.</summary>
    private const long SharedEventWindowMs = 5000;
    /// <summary>How much of the end of companion.log is read: enough for 500 ordinary lines.</summary>
    private const int LogTailBytes = 128 * 1024;
    public const int LogLinesDefault = 200, LogLinesLimit = 500, LogLineLimit = 2000;

    private readonly object activitySync = new();
    /// <summary>Newest first.</summary>
    private readonly List<ActivityEntry> activity = [];
    private static readonly Regex ActivityId = new("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant);

    private static bool IsDiagnosticsOp(string op) => op is "activityAdd" or "activityList" or "diagnosticsLog";

    private Reply? ExecuteDiagnostics(Command c, string actor)
    {
        if (!IsDiagnosticsOp(c.Op)) return null;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            if (actor != "host") throw new InvalidDataException("Host permission required.");
            var body = c.Body is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } b ? b : (JsonElement?)null;
            return c.Op switch
            {
                "activityAdd" => new("ack", c.Id, true, Data: new { added = AddActivity(body) }),
                "activityList" => new("ack", c.Id, true, Data: new { entries = ListActivity(body) }),
                _ => new("ack", c.Id, true, Data: ReadLogTail(body)),
            };
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
    }

    private static void OnlyFields(JsonElement? body, params string[] allowed)
    {
        if (body is not { } b) return;
        if (b.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid diagnostics request.");
        foreach (var property in b.EnumerateObject()) if (!allowed.Contains(property.Name)) throw new InvalidDataException("Invalid diagnostics request.");
    }

    /// <summary>Stores the entries a host page sends; returns how many were new.</summary>
    private int AddActivity(JsonElement? body)
    {
        OnlyFields(body, "entries");
        if (body is not { } b || !b.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Send the Activity entries to keep.");
        if (list.GetArrayLength() is 0 or > ActivityBatch) throw new InvalidDataException($"Send 1 to {ActivityBatch} Activity entries at a time.");
        // Every entry is checked before any is kept: a bad batch changes nothing.
        var parsed = list.EnumerateArray().Select(ParseActivity).ToList();
        int added = 0;
        lock (activitySync)
        {
            foreach (var entry in parsed.OrderBy(e => e.At))
            {
                if (activity.Any(e => e.Id == entry.Id)) continue;
                if (entry.Source != "notice" && activity.Any(e => e.Source == entry.Source && e.Text == entry.Text && Math.Abs(e.At - entry.At) <= SharedEventWindowMs)) continue;
                // Kept newest first, whatever order pages report in.
                int at = activity.FindIndex(e => e.At < entry.At);
                activity.Insert(at < 0 ? activity.Count : at, entry);
                added++;
            }
            if (activity.Count > ActivityLimit) activity.RemoveRange(ActivityLimit, activity.Count - ActivityLimit);
        }
        return added;
    }

    private static ActivityEntry ParseActivity(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Activity entry.");
        foreach (var property in e.EnumerateObject())
            if (property.Name is not ("id" or "kind" or "source" or "text" or "at" or "details")) throw new InvalidDataException("Invalid Activity entry.");
        string Text(string name, int max, bool required)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                return required ? throw new InvalidDataException("Invalid Activity entry.") : "";
            if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid Activity entry.");
            var s = v.GetString()!;
            if (s.Length > max || required && s.Length == 0) throw new InvalidDataException("Invalid Activity entry.");
            return s;
        }
        var id = Text("id", 64, true);
        if (!ActivityId.IsMatch(id)) throw new InvalidDataException("Invalid Activity entry.");
        var kind = Text("kind", 16, true);
        var source = Text("source", 16, true);
        if (kind is not ("info" or "success" or "error") || source is not ("notice" or "join" or "leave" or "lobby")) throw new InvalidDataException("Invalid Activity entry.");
        var text = Text("text", ActivityTextLimit, true);
        var details = Text("details", ActivityDetailsLimit, false);
        if (!e.TryGetProperty("at", out var atValue) || atValue.ValueKind != JsonValueKind.Number || !atValue.TryGetInt64(out var at)) throw new InvalidDataException("Invalid Activity entry.");
        // A page's clock may be off a little; a time far from the companion's is not believable.
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - at) > 86_400_000) throw new InvalidDataException("Invalid Activity entry.");
        return new(id, kind, source, text, at, details.Length == 0 ? null : details);
    }

    private ActivityEntry[] ListActivity(JsonElement? body)
    {
        OnlyFields(body);
        lock (activitySync) return [.. activity];
    }

    /// <summary>companion.log's last lines (and companion.1.log's when the current file is short).</summary>
    private LogTail ReadLogTail(JsonElement? body)
    {
        OnlyFields(body, "lines", "redact");
        int count = LogLinesDefault; bool redact = false;
        if (body is { } b)
        {
            if (b.TryGetProperty("lines", out var n) && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out count) || count is < 1 or > LogLinesLimit)) throw new InvalidDataException($"Ask for 1 to {LogLinesLimit} log lines.");
            if (b.TryGetProperty("redact", out var r))
            {
                if (r.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Say whether to hide file paths.");
                redact = r.GetBoolean();
            }
        }
        var path = Path.Combine(store.DirectoryPath, "companion.log");
        var lines = new List<string>();
        long size = 0; bool exists = false;
        try
        {
            var info = new FileInfo(path);
            exists = info.Exists; size = exists ? info.Length : 0;
            if (exists) lines.AddRange(TailLines(path, count));
            if (lines.Count < count)
            {
                var older = Path.Combine(store.DirectoryPath, "companion.1.log");
                if (File.Exists(older)) { exists = true; lines.InsertRange(0, TailLines(older, count - lines.Count)); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException("The companion log could not be read."); }
        var shown = lines.Select(l => l.Length > LogLineLimit ? l[..LogLineLimit] + "…" : l).Select(l => redact ? RedactPaths(l) : l).ToArray();
        return new(exists, redact ? "companion.log" : path, size, redact, shown);
    }

    /// <summary>The last <paramref name="count"/> lines of a file the log writer may be appending to (shared read).</summary>
    internal static List<string> TailLines(string path, int count)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long from = Math.Max(0, file.Length - LogTailBytes);
        file.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[file.Length - from];
        int read = 0;
        while (read < buffer.Length) { int got = file.Read(buffer, read, buffer.Length - read); if (got == 0) break; read += got; }
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var all = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        // Started mid-file: the first piece is part of a line.
        if (from > 0 && all.Count > 0) all.RemoveAt(0);
        if (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
        return all.Skip(Math.Max(0, all.Count - count)).ToList();
    }

    // A drive or network path up to the end of its last folder (folder names never hold a colon); the file name after
    // it is kept. The website hides paths the same way (web/src/technical-details.ts).
    private static readonly Regex PathFolders = new(@"(?:[A-Za-z]:\\|\\\\[^\\\s""'<>|]+\\)(?:[^\\\r\n""'<>|:]*\\)*", RegexOptions.CultureInvariant);

    /// <summary>
    /// Hides where files are on this PC: each folder path becomes "…\" (the file name stays, it is what the line is
    /// about) and the Windows user name becomes "&lt;user&gt;" wherever else it appears.
    /// </summary>
    internal static string RedactPaths(string line, string? userName = null)
    {
        var redacted = PathFolders.Replace(line, "…\\");
        userName ??= Environment.UserName;
        // As a whole word only: a user called "Pat" must not turn "paths" into "<user>hs".
        if (userName.Length >= 2) redacted = Regex.Replace(redacted, $"(?<![A-Za-z0-9]){Regex.Escape(userName)}(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return redacted;
    }
}
