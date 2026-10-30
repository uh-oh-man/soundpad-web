using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
namespace SoundpadWeb;

public record ParsedLibrary(Library Public, Dictionary<string, int> Indexes, Dictionary<string, string> Paths);
public static class LibraryParser
{
    private static XDocument Xml(string text, string root)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name.LocalName != root) throw new InvalidDataException("Unsupported Soundpad XML response.");
        return doc;
    }
    private static string A(XElement e, string name) => e.Attribute(name)?.Value ?? "";
    public static long? ParseDuration(string text) { var parts = text.Split(':'); if (parts.Length is not (2 or 3)) return null; double total = 0; foreach (var part in parts) { if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < 0) return null; total = total * 60 + n; } return total <= 3600000 ? (long)(total * 1000) : null; }
    public static ParsedLibrary Parse(string allXml, string categoriesXml, DiskState state, Dictionary<string, string>? icons = null, NativeStatistics? native = null)
    {
        var all = Xml(allXml, "Soundlist");
        var cats = Xml(categoriesXml, "Categories");
        var sounds = new List<Sound>(); var indexes = new Dictionary<string, int>(); var paths = new Dictionary<string, string>();
        var byIndex = new Dictionary<int, string>(); var occurrence = new Dictionary<string, int>();
        foreach (var e in all.Root!.Elements("Sound"))
        {
            if (!int.TryParse(A(e, "index"), out int index) || index < 1 || byIndex.ContainsKey(index)) throw new InvalidDataException("Invalid global sound index.");
            var path = A(e, "url");
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Soundpad did not provide a sound URL.");
            var normalized = path.Replace('/', '\\').ToUpperInvariant();
            var n = occurrence.GetValueOrDefault(normalized); occurrence[normalized] = n + 1;
            var id = Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(state.IdentityKey), Encoding.UTF8.GetBytes(normalized + "\n" + n))).ToLowerInvariant();
            var title = A(e, "title");
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(path);
            long? duration = ParseDuration(A(e, "duration"));
            var nativeStat = native?.Find(path, A(e, "hash"));
            sounds.Add(new(id, title, (icons ?? state.Icons).GetValueOrDefault(id), duration, nativeStat?.Count, nativeStat?.LastPlayed)); indexes[id] = index; paths[id] = path; byIndex[index] = id;
        }
        var catIds = new HashSet<string>();
        Category ParseCategory(XElement e, int depth)
        {
            if (depth > 16) throw new InvalidDataException("Category tree too deep.");
            var id = A(e, "index");
            if (!int.TryParse(id, out _) || !catIds.Add(id)) throw new InvalidDataException("Invalid category index.");
            var members = new List<string>();
            foreach (var s in e.Elements("Sound"))
            {
                if (!int.TryParse(A(s, "index"), out int i) || !byIndex.TryGetValue(i, out var soundId)) throw new InvalidDataException("Category references unknown sound.");
                // Check metadata too: the two vendor snapshots are not an atomic transaction.
                if (A(s, "url") is { Length: > 0 } url && !string.Equals(url, paths[soundId], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Library changed during refresh. Retry.");
                members.Add(soundId);
            }
            string? icon = null; var raw = A(e, "icon");
            if (raw.Length is > 0 and < 180000) { try { icon = IconData.Normalize("data:image/png;base64," + raw); } catch (Exception) { /* Native icon falls back safely. */ } }
            return new(id, A(e, "name"), icon, members.ToArray(), e.Elements("Category").Select(c => ParseCategory(c, depth + 1)).ToArray());
        }
        var categories = cats.Root!.Elements("Category").Select(c => ParseCategory(c, 0)).ToArray();
        var content = Json.Write(new { sounds, categories, indexes });
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..24].ToLowerInvariant();
        return new(new(version, sounds.ToArray(), categories), indexes, paths);
    }
}
