using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

/// <summary>
/// The host's shared style and active Mimic draft (2.43, gate uiSync), in <c>ui.json</c> next to state.json. The
/// host's browser and the companion window both read it, so a style chosen in one shows in the other. Older
/// companions never read the file, so state.json and the managed-audio journal stay exactly as they were.
/// Known fields are checked strictly (types and enums); anything else in the file is kept as it is and written back,
/// so a newer site's settings survive an older one's write. A file that cannot be read (too large, not JSON) gives
/// the defaults and is left on disk untouched until the host changes a setting.
/// </summary>
public sealed partial class UiPrefsStore(string path)
{
    public const int MaxBytes = 4096;
    private static readonly string[] NavStyles = ["sidebar", "tabs"], Themes = ["red-blue", "purple", "ocean"], Motions = ["system", "reduce"], Contrasts = ["standard", "high"], HeaderAnimations = ["on", "still", "off"];
    /// <summary>Every known field with its default (activeDraft: none).</summary>
    private static JsonObject Defaults() => new() { ["navStyle"] = "tabs", ["theme"] = "red-blue", ["motion"] = "system", ["contrast"] = "standard", ["headerAnimation"] = "on", ["activeDraft"] = null };
    private static readonly HashSet<string> Known = ["version", "navStyle", "theme", "motion", "contrast", "headerAnimation", "activeDraft"];
    private readonly object sync = new();
    private JsonObject? current;
    private bool saved;

    [GeneratedRegex("^[a-f0-9]{32}$")] private static partial Regex DraftIdPattern();

    /// <summary>The prefs with every known field filled in, and whether ui.json holds them (false before the first save).</summary>
    public (JsonObject Prefs, bool Saved) Read() { lock (sync) { Load(); return ((JsonObject)Filled(current!).DeepClone(), saved); } }

    /// <summary>
    /// Applies a uiPrefsSet: any subset of the known fields, nothing else, each valid, and writes ui.json atomically.
    /// Throws InvalidDataException (shown to the host) for anything invalid; nothing changes then.
    /// </summary>
    public JsonObject Set(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Send the style settings to change.");
        lock (sync)
        {
            Load();
            var next = (JsonObject)current!.DeepClone();
            foreach (var field in input.EnumerateObject())
            {
                if (field.Name == "version" || !Known.Contains(field.Name)) throw new InvalidDataException("Unknown style setting.");
                if (!Valid(field.Name, field.Value, out var value)) throw new InvalidDataException("Invalid style setting.");
                next[field.Name] = value;
            }
            next["version"] = 1;
            var text = next.ToJsonString();
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidDataException("The shared style settings are too large.");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, true);
            current = next; saved = true;
            return (JsonObject)Filled(next).DeepClone();
        }
    }

    private void Load()
    {
        if (current != null) return;
        current = new JsonObject { ["version"] = 1 }; saved = false;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject read) return;
            foreach (var (name, value) in read.ToArray())
            {
                if (name == "version") continue;
                // A known field with a value this version does not accept falls back to its default; others are kept.
                if (Known.Contains(name))
                {
                    using var parsed = JsonDocument.Parse(value?.ToJsonString() ?? "null");
                    if (Valid(name, parsed.RootElement, out var ok)) current[name] = ok;
                }
                else current[name] = value?.DeepClone();
            }
            saved = true;
        }
        // ArgumentException: a key written twice ({"theme":…,"theme":…}), which JsonObject refuses when it is enumerated.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { current = new JsonObject { ["version"] = 1 }; saved = false; }
    }

    /// <summary>Known fields missing from the file take their defaults; unknown ones stay.</summary>
    private static JsonObject Filled(JsonObject stored)
    {
        var all = new JsonObject { ["version"] = 1 };
        foreach (var (name, value) in Defaults()) all[name] = stored.ContainsKey(name) ? stored[name]?.DeepClone() : value?.DeepClone();
        foreach (var (name, value) in stored) if (!all.ContainsKey(name)) all[name] = value?.DeepClone();
        return all;
    }

    /// <summary>Whether <paramref name="value"/> is valid for the known field <paramref name="name"/>, and the node to store for it.</summary>
    private static bool Valid(string name, JsonElement value, out JsonNode? stored)
    {
        stored = null;
        static bool OneOf(JsonElement v, string[] allowed, out JsonNode? node)
        {
            node = v.ValueKind == JsonValueKind.String && allowed.Contains(v.GetString()) ? JsonValue.Create(v.GetString()) : null;
            return node != null;
        }
        switch (name)
        {
            case "navStyle": return OneOf(value, NavStyles, out stored);
            case "theme": return OneOf(value, Themes, out stored);
            case "motion": return OneOf(value, Motions, out stored);
            case "contrast": return OneOf(value, Contrasts, out stored);
            case "headerAnimation": return OneOf(value, HeaderAnimations, out stored);
            case "activeDraft":
                // null: no draft is open.
                if (value.ValueKind == JsonValueKind.Null) return true;
                if (value.ValueKind != JsonValueKind.Object) return false;
                string? session = null, draft = null;
                foreach (var f in value.EnumerateObject())
                {
                    if (f.Name == "sessionId" && session == null && f.Value.ValueKind == JsonValueKind.String && f.Value.GetString() is { Length: <= 64 } s) session = s;
                    else if (f.Name == "draftId" && draft == null && f.Value.ValueKind == JsonValueKind.String && f.Value.GetString() is { } d && DraftIdPattern().IsMatch(d)) draft = d;
                    else return false;
                }
                if (session == null || draft == null) return false;
                stored = new JsonObject { ["sessionId"] = session, ["draftId"] = draft };
                return true;
            default: return false;
        }
    }
}
