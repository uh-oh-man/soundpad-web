using System.Text.Json;
using System.Text.Json.Nodes;
namespace SoundpadWeb;

/// <summary>How one host-wide hotkey stands (2.44): on, off, taken by another app, refused, or turned off for this run.</summary>
public sealed record HostHotkeyStatus(string State, string? Message = null, string? Last = null);

/// <summary>
/// The host-wide hotkeys (2.44, gate hostHotkeys): Capture (start or stop listening on the remembered output), Panic
/// and Stop playback, each off until the host records a key. They live in <c>hotkeys.json</c> next to state.json
/// (<c>{version:1, capture, panic, stop}</c>, each a chord such as "Ctrl+Alt+F9" or null, at most 4 KiB), which older
/// companions never read. Known fields are checked strictly: a file that is too large, not JSON, of another version or
/// with an invalid value turns every hotkey off and is left on disk untouched until the host sets one. Unknown fields
/// of a readable file are kept when it is written again.
/// <para>
/// Changing a key registers the new one before the old one is released, so a key another app owns leaves the working
/// one in place (the file then keeps the old key too). Registration goes through <c>factory</c>: Windows in the
/// companion, a fixture in tests and the harness (see <see cref="ForbidWindowsHotkeys"/>).
/// </para>
/// </summary>
public sealed class HostHotkeys : IDisposable
{
    public static readonly string[] Actions = ["capture", "panic", "stop"];
    public const int MaxBytes = 4096;
    /// <summary>A local file the host may open: "Ctrl+Alt+F9", not "Ctrl+Alt+F9".</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly Dictionary<string, string> Labels = new() { ["capture"] = "Capture", ["panic"] = "Panic", ["stop"] = "Stop playback" };
    /// <summary>
    /// Set by the test assembly and the harness: the Windows factory then refuses, so no test can ever register a
    /// real key on this PC (the owner games on it while tests run).
    /// </summary>
    public static volatile bool ForbidWindowsHotkeys;
    /// <summary>How the companion registers a chord with Windows.</summary>
    public static IGlobalHotkey WindowsFactory(HotkeyChord chord)
    {
        if (ForbidWindowsHotkeys) throw new InvalidOperationException("Real Windows hotkeys are not registered in tests.");
        return new WindowsHotkey(chord.Modifiers, chord.Vk, chord.Text);
    }

    private sealed class Slot
    {
        public string? Chord;               // what hotkeys.json holds for this action
        public IGlobalHotkey? Registered;   // the key Windows (or the fixture) holds for it now
        public HostHotkeyStatus Status = new("off");
    }
    private readonly string path;
    private readonly Func<HotkeyChord, IGlobalHotkey> factory;
    private readonly Action<string> pressed;
    private readonly bool disabled;
    private readonly object sync = new();
    private readonly Dictionary<string, Slot> slots = Actions.ToDictionary(a => a, _ => new Slot());
    private JsonObject? stored;     // the readable file, unknown fields included; null when missing or damaged
    private bool loaded;
    private string? damage;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    public bool Disabled => disabled;

    public HostHotkeys(string path, Func<HotkeyChord, IGlobalHotkey> factory, Action<string> pressed, bool disabled = false)
    {
        this.path = path; this.factory = factory; this.pressed = pressed; this.disabled = disabled;
    }

    /// <summary>The chords as stored, by action (null = off).</summary>
    public Dictionary<string, string?> Bindings { get { lock (sync) { Load(); return Actions.ToDictionary(a => a, a => slots[a].Chord); } } }
    public Dictionary<string, HostHotkeyStatus> Statuses { get { lock (sync) { Load(); return Actions.ToDictionary(a => a, a => slots[a].Status); } } }
    /// <summary>Why hotkeys.json could not be used, if it could not.</summary>
    public string? Damage { get { lock (sync) { Load(); return damage; } } }
    /// <summary>True when <paramref name="chord"/> is one of the host-wide hotkeys (the Mimic hotkey must not take it).</summary>
    public bool Uses(string chord) { lock (sync) { Load(); return Actions.Any(a => string.Equals(slots[a].Chord, chord, StringComparison.OrdinalIgnoreCase)); } }

    private void Load()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > MaxBytes) { Damaged("is larger than 4 KiB"); return; }
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject read) { Damaged("is not a settings object"); return; }
            if (read["version"] is not JsonValue v || !v.TryGetValue(out int version) || version != 1) { Damaged("has a version this companion does not know"); return; }
            var chords = new Dictionary<string, string?>();
            foreach (var action in Actions)
            {
                var node = read[action];
                if (node == null) { chords[action] = null; continue; }
                if (node is not JsonValue value || !value.TryGetValue(out string? text) || !HotkeyChord.TryParse(text, out var chord)) { Damaged($"has an invalid {action} key"); return; }
                chords[action] = chord!.Text;
            }
            var distinct = chords.Values.Where(c => c != null).ToArray();
            if (distinct.Length != distinct.Distinct(StringComparer.OrdinalIgnoreCase).Count()) { Damaged("gives one key to two actions"); return; }
            stored = read;
            foreach (var action in Actions) slots[action].Chord = chords[action];
        }
        // ArgumentException: a key written twice, which JsonObject refuses when it is read.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { Damaged("could not be read"); }
    }
    private void Damaged(string why)
    {
        damage = $"hotkeys.json {why}, so the host-wide hotkeys are off. The file was left as it is; setting a key replaces it.";
        stored = null;
        foreach (var slot in slots.Values) slot.Chord = null;
    }

    /// <summary>
    /// Registers every stored key (at startup, and again after an update resume). A key equal to the Mimic hotkey's
    /// key is left off, as is everything with --no-hotkeys.
    /// </summary>
    public void RegisterAll(string? mimicKey)
    {
        lock (sync)
        {
            Load();
            foreach (var action in Actions)
            {
                var slot = slots[action];
                if (slot.Chord == null || slot.Registered != null) continue;
                slot.Status = Attempt(action, HotkeyChord.Parse(slot.Chord), mimicKey, out var key) ?? new("on");
                slot.Registered = key;
            }
            Interlocked.Increment(ref revision);
        }
    }

    /// <summary>Keys left off because they matched the Mimic hotkey's key are tried again once it moved.</summary>
    public bool RetryConflicts(string? mimicKey)
    {
        lock (sync)
        {
            bool changed = false;
            foreach (var action in Actions)
            {
                var slot = slots[action];
                if (slot.Chord == null || slot.Registered != null || slot.Status.State != "conflict") continue;
                slot.Status = Attempt(action, HotkeyChord.Parse(slot.Chord), mimicKey, out var key) ?? new("on");
                slot.Registered = key; changed = true;
            }
            if (changed) Interlocked.Increment(ref revision);
            return changed;
        }
    }

    /// <summary>Registers <paramref name="chord"/> for <paramref name="action"/>; null when it worked, else the status to show.</summary>
    private HostHotkeyStatus? Attempt(string action, HotkeyChord chord, string? mimicKey, out IGlobalHotkey? registered)
    {
        registered = null;
        if (disabled) return new("disabled", "Host-wide hotkeys are off for this run (--no-hotkeys).");
        if (chord.BareFunctionKey && string.Equals(chord.Text, mimicKey, StringComparison.OrdinalIgnoreCase))
            return new("conflict", $"{chord.Text} is the Mimic hotkey's key. Choose another key here, or another one for Mimic.");
        IGlobalHotkey? key = null;
        try
        {
            key = factory(chord);
            key.Register(() => pressed(action));
            registered = key;
            return null;
        }
        catch (InvalidDataException ex) when (HotkeyTaken.Is(ex)) { key?.Dispose(); return new("taken", $"{chord.Text} is already used by another app."); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            key?.Dispose();
            return new("invalid", ex is InvalidOperationException ? ex.Message : $"{chord.Text} could not be registered: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a hotkeysSet: <paramref name="changes"/> holds only the actions to change (null turns one off). Every
    /// chord is checked first (valid, no key for two actions, not the Mimic key), and nothing changes when one fails.
    /// Then each action registers its new key before releasing the old; a key another app owns keeps the old one.
    /// hotkeys.json is written with what succeeded.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string?> changes, string? mimicKey)
    {
        lock (sync)
        {
            Load();
            var parsed = new Dictionary<string, HotkeyChord?>();
            foreach (var (action, text) in changes)
            {
                if (!slots.ContainsKey(action)) throw new InvalidDataException("Unknown hotkey action.");
                HotkeyChord? chord;
                try { chord = text == null ? null : HotkeyChord.Parse(text); }
                catch (InvalidDataException ex) { throw new InvalidDataException($"{Labels[action]}: {ex.Message}"); }
                if (chord is { BareFunctionKey: true } && string.Equals(chord.Text, mimicKey, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{chord.Text} is the Mimic hotkey's key (it saves the last seconds). Choose another key for {Labels[action]}, or move the Mimic hotkey first.");
                parsed[action] = chord;
            }
            var after = Actions.ToDictionary(a => a, a => parsed.TryGetValue(a, out var c) ? c?.Text : slots[a].Chord);
            foreach (var group in after.Where(p => p.Value != null).GroupBy(p => p.Value!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var names = group.Select(p => p.Key).ToArray();
                var changed = names.FirstOrDefault(parsed.ContainsKey) ?? names[0];
                var other = names.First(n => n != changed);
                throw new InvalidDataException($"{group.Key} is already {Labels[other]}.");
            }
            bool write = false;
            foreach (var (action, chord) in parsed)
            {
                var slot = slots[action];
                if (chord == null)
                {
                    slot.Registered?.Dispose(); slot.Registered = null;
                    write |= slot.Chord != null; slot.Chord = null; slot.Status = new("off");
                    continue;
                }
                if (string.Equals(slot.Chord, chord.Text, StringComparison.Ordinal) && slot.Registered != null) continue;
                var failed = Attempt(action, chord, mimicKey, out var key);
                if (failed != null)
                {
                    // The previous key (if any) keeps working, and the file keeps it.
                    var previous = slot.Registered != null && slot.Chord != null ? $" Your previous key, {slot.Chord}, still works." : "";
                    slot.Status = failed with { Message = failed.Message + (failed.State == "taken" ? previous : "") };
                    continue;
                }
                slot.Registered?.Dispose();
                slot.Registered = key; slot.Chord = chord.Text; slot.Status = new("on"); write = true;
            }
            Interlocked.Increment(ref revision);
            if (write) Save();
        }
    }

    /// <summary>Writes the stored chords atomically, keeping unknown fields of a readable file.</summary>
    private void Save()
    {
        var next = (JsonObject?)stored?.DeepClone() ?? [];
        next["version"] = 1;
        foreach (var action in Actions) next[action] = slots[action].Chord;
        var text = next.ToJsonString(Readable);
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new IOException("hotkeys.json would be larger than 4 KiB.");
            var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                file.Write(bytes); file.Flush(true);
            }
            File.Move(temporary, path, true);
            stored = next; damage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var slot in slots.Values.Where(s => s.Status.State == "on"))
                slot.Status = slot.Status with { Message = "Works until the companion closes: hotkeys.json could not be saved." };
        }
    }

    /// <summary>What the last press of <paramref name="action"/> did, for the host's status line.</summary>
    public void NotePress(string action, string text)
    {
        lock (sync) { var slot = slots[action]; slot.Status = slot.Status with { Last = text }; Interlocked.Increment(ref revision); }
    }

    public void Dispose()
    {
        lock (sync)
            foreach (var slot in slots.Values) { slot.Registered?.Dispose(); slot.Registered = null; }
    }
}
