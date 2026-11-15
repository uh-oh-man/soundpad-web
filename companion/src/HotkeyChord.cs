namespace SoundpadWeb;

/// <summary>
/// A host-wide hotkey (2.44, gate hostHotkeys) such as "Ctrl+Alt+F9": modifiers in the order Ctrl, Alt, Shift, Win,
/// then one key. Only keys that are safe to take from every app on the PC are accepted: F1–F24 except F12, A–Z, 0–9,
/// the numeric keypad, Pause, ScrollLock, Insert, Home, End, PageUp and PageDown. A letter, digit or navigation key
/// needs Ctrl, Alt or Win (Shift alone would still take it from typing); F-keys, Pause and ScrollLock may be bare.
/// Windows' own combinations (Alt+F4, Win+D/E/R/L/G/I/X) are refused.
/// </summary>
public sealed record HotkeyChord(bool Ctrl, bool Alt, bool Shift, bool Win, string Key, uint Vk)
{
    public const int MaxLength = 40;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;
    private static readonly Dictionary<string, (string Name, uint Vk)> keys = BuildKeys();
    private static Dictionary<string, (string, uint)> BuildKeys()
    {
        var all = new Dictionary<string, (string, uint)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= 24; i++) if (i != 12) all["F" + i] = ("F" + i, (uint)(0x70 + i - 1));
        for (char c = 'A'; c <= 'Z'; c++) all[c.ToString()] = (c.ToString(), c);
        for (char c = '0'; c <= '9'; c++) all[c.ToString()] = (c.ToString(), c);
        for (int i = 0; i <= 9; i++) all["Num" + i] = ("Num" + i, (uint)(0x60 + i));
        foreach (var (name, vk) in new (string, uint)[] { ("NumMultiply", 0x6A), ("NumAdd", 0x6B), ("NumSubtract", 0x6D), ("NumDecimal", 0x6E), ("NumDivide", 0x6F),
            ("Pause", 0x13), ("ScrollLock", 0x91), ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22) })
            all[name] = (name, vk);
        return all;
    }
    private static readonly HashSet<string> windowsKeys = new(["D", "E", "R", "L", "G", "I", "X"], StringComparer.Ordinal);

    /// <summary>The flags RegisterHotKey takes for these modifiers (MOD_NOREPEAT is added by WindowsHotkey).</summary>
    public uint Modifiers => (Alt ? ModAlt : 0) | (Ctrl ? ModControl : 0) | (Shift ? ModShift : 0) | (Win ? ModWin : 0);
    public bool FunctionKey => Key.Length >= 2 && Key[0] == 'F' && char.IsDigit(Key[1]);
    /// <summary>An F-key with no modifier, as the Mimic hotkey uses (F6–F10).</summary>
    public bool BareFunctionKey => FunctionKey && !Ctrl && !Alt && !Shift && !Win;
    public string Text => string.Join("+", new[] { Ctrl ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, Win ? "Win" : null, Key }.Where(p => p != null));
    public override string ToString() => Text;

    /// <summary>The chord <paramref name="text"/> names, or an InvalidDataException that says (to the host) why not.</summary>
    public static HotkeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) throw new InvalidDataException("Press a key combination such as Ctrl+Alt+F9.");
        bool ctrl = false, alt = false, shift = false, win = false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0)) throw new InvalidDataException($"\"{text}\" is not a key combination. Use one such as Ctrl+Alt+F9.");
        foreach (var part in parts[..^1])
        {
            ref bool flag = ref ctrl;
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": flag = ref ctrl; break;
                case "alt": flag = ref alt; break;
                case "shift": flag = ref shift; break;
                case "win" or "windows": flag = ref win; break;
                default: throw new InvalidDataException($"\"{part}\" is not a modifier. Use Ctrl, Alt, Shift or Win before the key.");
            }
            if (flag) throw new InvalidDataException($"{part} appears twice in \"{text}\".");
            flag = true;
        }
        var last = parts[^1];
        if (last.Equals("F12", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("F12 is reserved by Windows. Choose another key.");
        if (!keys.TryGetValue(last, out var key)) throw new InvalidDataException($"{last} can't be a host-wide hotkey. Use F1–F24 (not F12), a letter, a digit, a keypad key, Pause, ScrollLock, Insert, Home, End, PageUp or PageDown.");
        var chord = new HotkeyChord(ctrl, alt, shift, win, key.Name, key.Vk);
        bool bareAllowed = chord.FunctionKey || key.Name is "Pause" or "ScrollLock";
        if (!bareAllowed && !ctrl && !alt && !win) throw new InvalidDataException($"Add Ctrl, Alt or Win to {chord.Text}: without them every app on this PC would lose that key while typing.");
        if (alt && key.Name == "F4" && !ctrl && !win) throw new InvalidDataException("Alt+F4 closes windows. Choose another key.");
        if (win && windowsKeys.Contains(key.Name)) throw new InvalidDataException($"Windows uses {chord.Text}. Choose another key.");
        return chord;
    }

    public static bool TryParse(string? text, out HotkeyChord? chord)
    {
        try { chord = Parse(text); return true; }
        catch (InvalidDataException) { chord = null; return false; }
    }
}
