using System.Xml;
using System.Xml.Linq;
namespace SoundpadWeb;

public record NativeStat(long Count, string? LastPlayed);
// Read-only, schema-validated mapping; vendor hashes are opaque and never reconstructed.
public sealed class NativeStatistics(string? directory = null)
{
    private readonly string folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Leppsoft");
    private Dictionary<string, NativeStat> stats = [];
    private Dictionary<string, string?> hashes = new(StringComparer.OrdinalIgnoreCase);
    private static XDocument Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }); return XDocument.Load(reader);
    }
    public void Refresh()
    {
        var next = new Dictionary<string, NativeStat>(); var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>();
        try
        {
            var doc = Read(Path.Combine(folder, "stats.spl")); if (doc.Root?.Name != "Stats") { stats = []; hashes = []; return; }
            foreach (var e in doc.Root.Elements("Sound"))
            {
                var hash = (string?)e.Attribute("hash"); var date = (string?)e.Attribute("lastPlayedOn");
                if (string.IsNullOrEmpty(hash) || hash.Length > 512 || !long.TryParse((string?)e.Attribute("playCount"), out long count) || count < 0 || ambiguous.Contains(hash)) continue;
                if (next.ContainsKey(hash)) { next.Remove(hash); ambiguous.Add(hash); continue; }
                next[hash] = new(count, DateOnly.TryParseExact(date, "yyyy-MM-dd", out _) ? date : null);
            }
            foreach (var file in Directory.EnumerateFiles(folder, "*.spl").Take(32))
            {
                try
                {
                    var list = Read(file); if (list.Root?.Name != "Soundlist") continue;
                    foreach (var e in list.Root.Elements("Sound"))
                    {
                        var path = (string?)e.Attribute("url"); var hash = (string?)e.Attribute("hash"); if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash) || hash.Length > 512) continue;
                        if (map.TryGetValue(path, out var previous) && previous != hash) map[path] = null; else if (!map.ContainsKey(path)) map[path] = hash;
                    }
                }
                catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException) { }
            }
            stats = next; hashes = map;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException) { stats = []; hashes = []; }
    }
    public NativeStat? Find(string path, string apiHash) { var hash = string.IsNullOrEmpty(apiHash) ? hashes.GetValueOrDefault(path) : apiHash; return hash == null ? null : stats.GetValueOrDefault(hash); }
}
