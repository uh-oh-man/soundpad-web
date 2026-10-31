namespace SoundpadWeb.Testing;

/// <summary>Test-only hotkey: never touches Windows; <see cref="Press"/> simulates the key. Can be told to be "taken".</summary>
public sealed class HotkeyFixture : IGlobalHotkey
{
    private Action? pressed;
    public string Name => "F6";
    public bool Taken { get; set; }
    public bool Active => pressed != null;
    public void Register(Action action)
    {
        if (Taken) throw new InvalidDataException("F6 is already used by another app (fixture).");
        pressed = action;
    }
    public void Press() => pressed?.Invoke();
    public void Unregister() => pressed = null;
    public void Dispose() => Unregister();
}
