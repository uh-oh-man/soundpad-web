using System.Text.Json;
namespace SoundpadWeb;

/// <summary>
/// Read-only view of Voice Replay Studio's clip library (the desktop app in mimic/), so the host can bring
/// a clip saved there into a session. Its files are never written, moved or locked against that app.
/// </summary>
public sealed class VoiceReplayLibrary(string? root = null)
{
    /// <summary>Voice Replay Studio's data folder; library.json lists its clips.</summary>
    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voice Replay Studio");
    /// <summary>Temp's per-sound input limit; larger clips are refused before reading them.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    public sealed record Entry(string Id, string Title, string Transcript, long CapturedAt, int DurationMs);
    // Field names as Voice Replay Studio's Clip class writes them.
    private sealed record Stored(Guid Id, string? AudioPath, string? Title, string? Transcript, DateTimeOffset CapturedAt, double Duration);

    public bool Exists => File.Exists(Path.Combine(Root, "library.json"));

    /// <summary>Newest clips whose audio file is still there, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<Entry> List(int max = 200) =>
        Load().Where(c => c.AudioPath is { } path && File.Exists(path)).OrderByDescending(c => c.CapturedAt).Take(max)
            .Select(c => new Entry(c.Id.ToString("D"), Clip(c.Title, 100, "Recording"), Clip(c.Transcript, 300, ""), c.CapturedAt.ToUnixTimeMilliseconds(), (int)Math.Round(Math.Clamp(c.Duration, 0, 3600) * 1000))).ToArray();

    /// <summary>The clip's WAV bytes and title. Only .wav files listed in the library are read.</summary>
    public (byte[] Wav, string Title) Read(string id)
    {
        if (!Guid.TryParse(id, out var guid)) throw new InvalidDataException("Unknown Voice Replay Studio clip.");
        var clip = Load().FirstOrDefault(c => c.Id == guid) ?? throw new InvalidDataException("That clip is no longer in Voice Replay Studio's library.");
        if (clip.AudioPath is not { } path || !File.Exists(path)) throw new InvalidDataException("That clip's audio file is missing.");
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only WAV clips can be imported.");
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("That clip is larger than Temp's 16 MiB limit.");
        return (ReadAtMost(path, MaxBytes, "That clip is larger than Temp's 16 MiB limit."), Clip(clip.Title, 100, "Recording"));
    }

    private List<Stored> Load()
    {
        var path = Path.Combine(Root, "library.json");
        if (!File.Exists(path)) return [];
        try
        {
            using var stream = new MemoryStream(ReadAtMost(path, MaxLibraryBytes, "Voice Replay Studio's library is unexpectedly large. It was left untouched."));
            return JsonSerializer.Deserialize<List<Stored>>(stream) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Voice Replay Studio's library could not be read. It was left untouched.");
        }
    }

    /// <summary>Generous for thousands of clips; a library.json bigger than this isn't one Voice Replay Studio wrote.</summary>
    private const long MaxLibraryBytes = 32L * 1024 * 1024;

    /// <summary>Reads a file without locking it, stopping at <paramref name="max"/> bytes even if it grows meanwhile.</summary>
    private static byte[] ReadAtMost(string path, long max, string tooLarge)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (copy.Length + n > max) throw new InvalidDataException(tooLarge);
            copy.Write(chunk, 0, n);
        }
        return copy.ToArray();
    }

    private static string Clip(string? text, int max, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        return value.Length > max ? value[..(max - 1)].TrimEnd() + "…" : value;
    }
}
