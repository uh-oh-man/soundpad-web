namespace SoundpadWeb;

// Schema 2 migration input only. New mutations use ManagedAudioStore.
public record ManagedAudio(string File, string Name, string By, bool Permanent = false, string? Source = null);
