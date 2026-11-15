using System.Text.Json;
using System.Text.Json.Nodes;
namespace SoundpadWeb;

/// <summary>Whether the host finished or dismissed setup (2.44, gate setup).</summary>
public sealed record SetupRecord(long? CompletedAt, string? CompletedBy, bool Dismissed)
{
    /// <summary>Finished, or (an upgraded install) its "Check your setup" card was dismissed.</summary>
    public bool Done => CompletedAt != null || Dismissed;
}

/// <summary>
/// The guided setup's memory (2.44), in <c>setup.json</c> next to state.json: <c>{version:1, completedAt, completedBy,
/// dismissed}</c>, at most 4 KiB, which older companions never read. Known fields are checked strictly; a file that
/// is too large, not JSON, of another version or with a wrong value counts as "not done" and is left on disk untouched
/// until the host finishes or dismisses setup again. Unknown fields of a readable file are kept. Written atomically.
/// </summary>
public sealed class SetupStore(string path)
{
    public const int MaxBytes = 4096;
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object sync = new();
    private JsonObject? stored;
    private SetupRecord? current;
    private bool damaged;

    /// <summary>What setup.json says; "not done" when it is missing or damaged.</summary>
    public SetupRecord Read() { lock (sync) { Load(); return current!; } }
    /// <summary>True when setup.json exists but could not be used (it is left as it is).</summary>
    public bool Damaged { get { lock (sync) { Load(); return damaged; } } }

    private void Load()
    {
        if (current != null) return;
        current = new(null, null, false); stored = null; damaged = false;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > MaxBytes || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject read) { damaged = true; return; }
            if (read["version"] is not JsonValue v || !v.TryGetValue(out int version) || version != 1) { damaged = true; return; }
            long? at = null; string? by = null; bool dismissed = false;
            if (read["completedAt"] is { } a) { if (a is not JsonValue av || !av.TryGetValue(out long ms) || ms < 0) { damaged = true; return; } at = ms; }
            if (read["completedBy"] is { } b) { if (b is not JsonValue bv || !bv.TryGetValue(out string? name) || name.Length > 80) { damaged = true; return; } by = name; }
            if (read["dismissed"] is { } d) { if (d is not JsonValue dv || !dv.TryGetValue(out bool flag)) { damaged = true; return; } dismissed = flag; }
            current = new(at, by, dismissed); stored = read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { damaged = true; }
    }

    /// <summary>"done" (Finish), "dismiss" (the upgrade card's close) or "reset" (Run setup again).</summary>
    public SetupRecord Set(string action, string by, long nowMs)
    {
        lock (sync)
        {
            Load();
            var next = action switch
            {
                "done" => new SetupRecord(nowMs, by.Length > 80 ? by[..80] : by, current!.Dismissed),
                "dismiss" => current! with { Dismissed = true },
                "reset" => new SetupRecord(null, null, false),
                _ => throw new InvalidDataException("Unsupported setup action."),
            };
            var json = (JsonObject?)stored?.DeepClone() ?? [];
            json["version"] = 1;
            json["completedAt"] = next.CompletedAt;
            json["completedBy"] = next.CompletedBy;
            json["dismissed"] = next.Dismissed;
            var text = json.ToJsonString(Readable);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidDataException("setup.json would be larger than 4 KiB.");
            var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(System.Text.Encoding.UTF8.GetBytes(text)); file.Flush(true);
            }
            File.Move(temporary, path, true);
            stored = json; current = next; damaged = false;
            return next;
        }
    }

    /// <summary>
    /// True when this install already holds kept data (an upgrade, not a first run): Kick counts or web statistics,
    /// permanent kept clips, a voice library, Keep settings or the shared style. Read-only.
    /// </summary>
    public static bool Existing(string dataDirectory, DiskState disk, bool permanentAssets) =>
        disk.Counter > 0 || disk.Statistics.Count > 0 || permanentAssets
        || File.Exists(Path.Combine(dataDirectory, "library", "voice-library.v1.json"))
        || File.Exists(Path.Combine(dataDirectory, "keep-settings.json"))
        || File.Exists(Path.Combine(dataDirectory, "ui.json"));
}
