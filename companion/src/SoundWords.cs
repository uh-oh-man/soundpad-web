using System.Text.Json;
using System.Text.Json.Serialization;
namespace SoundpadWeb;

/// <summary>
/// Words of Soundpad sounds in the categories the host chose to index (2.41), recognised locally in the background.
/// Kept in &lt;data&gt;\library\sound-words.v1.jsonl, one line per file: its path, length and last-write time (the
/// "already indexed" test) and its words, or why it could not be read. The sound files themselves are only read.
/// A file whose length or time changed is indexed again; lines for the same path replace earlier ones.
/// </summary>
public sealed class SoundWords
{
    public sealed record Entry(
        [property: JsonPropertyName("p")] string Path,
        [property: JsonPropertyName("z")] long Size,
        [property: JsonPropertyName("m")] long Modified,
        [property: JsonPropertyName("w")] LibraryWord[]? Words,
        [property: JsonPropertyName("x")] string? Error = null,
        [property: JsonPropertyName("a")] long At = 0);

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly WordIndex index;
    private int obsolete;
    public string FilePath { get; }

    public SoundWords(string dataDirectory, WordIndex index)
    {
        FilePath = System.IO.Path.Combine(dataDirectory, "library", "sound-words.v1.jsonl");
        this.index = index;
        Load();
    }

    /// <summary>The key of a file: its full path in upper case, as Soundpad's ids treat paths.</summary>
    public static string Key(string path) => path.Replace('/', '\\').ToUpperInvariant();
    public static string DocKey(string path) => "s:" + Key(path);
    public static bool IsSoundKey(string key) => key.StartsWith("s:", StringComparison.Ordinal);

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            int lines = 0;
            foreach (var text in File.ReadLines(FilePath))
            {
                lines++;
                try
                {
                    var e = JsonSerializer.Deserialize<Entry>(text, Json.Options);
                    if (e == null || e.Path is not { Length: > 0 and <= 1024 } || !System.IO.Path.IsPathFullyQualified(e.Path) || e.Size < 0) continue;
                    if (e.Words != null && (e.Words.Length > VoiceLibrary.MaxWords || e.Words.Any(w => w == null || w.EndMs <= w.StartMs || w.StartMs < 0 || w.Text is not { Length: > 0 and <= 200 }))) continue;
                    entries[Key(e.Path)] = e;
                }
                catch (JsonException) { }
            }
            obsolete = Math.Max(0, lines - entries.Count);
            foreach (var e in entries.Values) if (e.Words is { Length: > 0 } list) index.Set(DocKey(e.Path), list.Select(w => w.Text).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>True when this file, at this length and time, was already indexed (or already failed).</summary>
    public bool Done(string path, long size, long modified)
    {
        lock (gate) return entries.TryGetValue(Key(path), out var e) && e.Size == size && e.Modified == modified;
    }
    public bool Has(string path) { lock (gate) return entries.ContainsKey(Key(path)); }
    public Entry? Get(string path) { lock (gate) return entries.GetValueOrDefault(Key(path)); }
    public int Count { get { lock (gate) return entries.Count; } }

    public void Store(Entry entry)
    {
        lock (gate)
        {
            if (entries.ContainsKey(Key(entry.Path))) obsolete++;
            entries[Key(entry.Path)] = entry;
            if (entry.Words is { Length: > 0 } list) index.Set(DocKey(entry.Path), list.Select(w => w.Text).ToArray());
            else index.Remove(DocKey(entry.Path));
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                if (obsolete > Math.Max(200, entries.Count))
                {
                    var temporary = FilePath + ".tmp";
                    File.WriteAllLines(temporary, entries.Values.Select(e => Json.Write(e)));
                    File.Move(temporary, FilePath, true);
                    obsolete = 0;
                }
                else File.AppendAllLines(FilePath, [Json.Write(entry)]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* kept in memory; indexed again next launch */ }
        }
    }

    /// <summary>The words of an indexed file, by its document key ("s:..."), with its path (host-only).</summary>
    public Entry? ByDocKey(string key)
    {
        if (!IsSoundKey(key)) return null;
        lock (gate) return entries.GetValueOrDefault(key[2..]);
    }
}
