namespace VoiceReplay.Core;

public sealed record HotkeySpec(uint Modifiers, uint Key)
{
    public static HotkeySpec Parse(string value)
    {
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException("Choose a key.");
        uint mods = 0;
        foreach (var p in parts[..^1])
            mods |= p.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 2u,
                "ALT" => 1u,
                "SHIFT" => 4u,
                "WIN" => 8u,
                _ => throw new ArgumentException("Unknown modifier " + p)
            };
        string k = parts[^1].ToUpperInvariant();
        uint key;
        if (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]))
            key = k[0];
        else if (k.StartsWith('F') && uint.TryParse(k[1..], out uint fn) && fn >= 1 && fn <= 24)
            key = 0x70 + fn - 1;
        else
            key = k switch
            {
                "SPACE" => 0x20,
                "INSERT" => 0x2D,
                "DELETE" => 0x2E,
                "HOME" => 0x24,
                "END" => 0x23,
                "PAGEUP" => 0x21,
                "PAGEDOWN" => 0x22,
                "PAUSE" => 0x13,
                "ESCAPE" => 0x1B,
                "TAB" => 9,
                "BACK" => 8,
                "RETURN" => 13,
                "LEFT" => 0x25,
                "UP" => 0x26,
                "RIGHT" => 0x27,
                "DOWN" => 0x28,
                _ => throw new ArgumentException("Unsupported hotkey. Use letters, digits, F1–F24, or navigation keys.")
            };
        if (key == 0x7B || (mods == 3 && key == 0x2E) || (mods == 8 && (key == 'L' || key == 'U')))
            throw new ArgumentException("That shortcut is reserved by Windows.");
        return new(mods, key);
    }
}
