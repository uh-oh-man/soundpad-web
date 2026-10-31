using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceReplay.Core;

public sealed class Settings
{
    public string SourceId { get; set; } = "";
    public string SourceKind { get; set; } = "Output";
    public string ProcessName { get; set; } = "";
    public int CaptureSeconds { get; set; } = 10;
    public int BufferSeconds { get; set; } = 30;
    public int SampleRate { get; set; } = 48000;
    public string CaptureHotkey { get; set; } = "F6";
    public string StopHotkey { get; set; } = "Ctrl+F7";
    public string EditorHotkey { get; set; } = "Ctrl+F8";
    public string MonitorDeviceId { get; set; } = "";
    public bool AutoImport { get; set; } = true;
    public bool InstantImport
    {
        get; set;
    }
    public bool StartMinimized
    {
        get; set;
    }
    public bool Startup
    {
        get; set;
    }
    public string Language { get; set; } = "en";
    public string StorageDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voice Replay Studio", "Clips");
}
public sealed class Clip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AudioPath { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string Title { get; set; } = "Recording";
    public string Transcript { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public double Duration
    {
        get; set;
    }
    public string Source { get; set; } = "";
    public double TrimStart
    {
        get; set;
    }
    public double TrimEnd
    {
        get; set;
    }
    public int Version { get; set; } = 1;
    public string TranscriptionState { get; set; } = "Pending";
    public string ImportState { get; set; } = "Pending";
    public string? SoundpadIndex
    {
        get; set;
    }
    public string? ImportedPath
    {
        get; set;
    }
    public List<string> RetainedPaths { get; set; } = [];
    public string Error { get; set; } = "";
    public string Summary => $"{CapturedAt.LocalDateTime:g}  ·  {Duration:F2}s  ·  {Source}  ·  Speech: {TranscriptionState}  ·  Soundpad: {ImportState}";
}
public static class Names
{
    public static string Safe(string text)
    {
        var value = Regex.Replace(text.ToUpperInvariant(), @"[^\p{L}\p{Nd}]+", "_").Trim('_');
        if (value.Length > 80)
            value = value[..80].TrimEnd('_');
        if (string.IsNullOrEmpty(value))
            value = "RECORDING";
        if (Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$"))
            value = "CLIP_" + value;
        return value;
    }
    public static string FileName(Clip clip)
    {
        string title = Safe(clip.Title);
        if (title.Length > 60)
            title = title[..60].TrimEnd('_');
        return $"{title}_{clip.CapturedAt:yyyyMMdd_HHmmss}_{clip.Id.ToString("N")[..8]}_v{clip.Version}.wav";
    }
}
public sealed class Store
{
    public string Root
    {
        get;
    }
    public string ModelPath => Path.Combine(Root, "Models", "ggml-base.en.bin");
    public Settings Settings
    {
        get; private set;
    }
    public List<Clip> Clips
    {
        get; private set;
    }
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public Store(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voice Replay Studio");
        Directory.CreateDirectory(Root);
        Settings = Load<Settings>("settings.json") ?? new();
        Clips = Load<List<Clip>>("library.json") ?? [];
        foreach (var c in Clips)
        {
            if (c.TranscriptionState == "Working")
                c.TranscriptionState = "Pending";
            if (c.ImportState == "Working")
                c.ImportState = "Pending";
        }
    }
    private T? Load<T>(string name)
    {
        var path = Path.Combine(Root, name);
        if (!File.Exists(path))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception ex) { Log("Cannot load " + name, ex); throw new InvalidDataException($"Cannot read {path}. The file was preserved; restore or repair it before continuing.", ex); }
    }
    public void Save()
    {
        lock (gate)
        {
            AtomicJson(Path.Combine(Root, "settings.json"), Settings);
            AtomicJson(Path.Combine(Root, "library.json"), Clips);
        }
    }
    public void Add(Clip c)
    {
        lock (gate)
        {
            Clips.Insert(0, c);
            Save();
        }
    }
    public void Remove(Clip c)
    {
        lock (gate)
        {
            Clips.Remove(c);
            Save();
        }
    }
    public Clip[] Snapshot()
    {
        lock (gate)
            return Clips.ToArray();
    }
    public static void AtomicJson<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
    public void Log(string message, Exception? ex = null)
    {
        lock (gate)
        {
            try
            {
                var path = Path.Combine(Root, "diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message} {ex}\n");
            }
            catch { }
        }
    }
}
