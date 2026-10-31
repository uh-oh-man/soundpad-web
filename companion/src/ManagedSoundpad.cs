using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
namespace SoundpadWeb;

internal static class ManagedSoundpad
{
    private static XDocument Xml(string input, string root)
    {
        using var reader = XmlReader.Create(new StringReader(input), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != root) throw new InvalidDataException("Soundpad returned unavailable or invalid library metadata.");
        return document;
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static async Task<NativeCatalog> Read(ISoundpad pipe)
    {
        var raw = await pipe.Request("GetSoundlist()");
        var all = Xml(raw, "Soundlist"); var categories = Xml(await pipe.Request("GetCategories(true, false)"), "Categories");
        var entries = all.Root!.Elements("Sound").Select(e => new NativeEntry((int?)e.Attribute("index") ?? -1, (string?)e.Attribute("url") ?? "")).ToArray();
        if (entries.Any(e => e.Index < 1 || string.IsNullOrWhiteSpace(e.Path)) || entries.Select(e => e.Index).Distinct().Count() != entries.Length) throw new InvalidDataException("Soundpad's global mapping is ambiguous.");
        var cats = categories.Descendants("Category").Select(e => new NativeCategory((int?)e.Attribute("index") ?? -1, (string?)e.Attribute("name") ?? "", (string?)e.Attribute("type"), e.Elements("Sound").Select(s => (string?)s.Attribute("url") ?? "").ToArray())).ToArray();
        if (cats.Any(c => c.Index < 0) || cats.Select(c => c.Index).Distinct().Count() != cats.Length) throw new InvalidDataException("Soundpad's category mapping is ambiguous.");
        return new(entries, cats, Hash(raw));
    }
    public static string Quote(string path)
    {
        if (path.Any(c => c == '"' || char.IsControl(c))) throw new InvalidDataException("The selected path contains unsupported characters.");
        return "\"" + path + "\"";
    }
    private static async Task Ok(ISoundpad pipe, string command)
    {
        if (!(await pipe.Request(command)).StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("Soundpad rejected this managed-audio action. Check that its full version is open, then retry.");
    }
    public static async Task<int> EnsureCategory(ISoundpad pipe, OwnedCategory reference, Action checkpoint)
    {
        var catalog = await Read(pipe);
        if (reference.NativeId is int index && reference.State == "created" && catalog.Categories.Any(c => c.Index == index && c.Name == reference.Name)) return index;
        if (reference.State == "creating")
        {
            // This unique nonce name was durably recorded as absent immediately before creation.
            var recovered = catalog.Categories.Where(c => c.Name == reference.Name).ToArray();
            if (recovered.Length == 1) { reference.NativeId = recovered[0].Index; reference.State = "created"; checkpoint(); return recovered[0].Index; }
            if (recovered.Length > 1) throw new InvalidDataException("The app-owned category is ambiguous. Preserve both categories and resolve the conflict in Soundpad.");
        }
        // Never adopt a category just because its display name matches.
        if (catalog.Categories.Any(c => c.Name == reference.Name)) throw new InvalidDataException("The planned category already exists without a verified creation record. Choose a new managed category.");
        reference.NativeId = null; reference.State = "creating"; checkpoint();
        await Ok(pipe, $"DoAddCategory({Quote(reference.Name)}, -1)");
        var after = await Read(pipe); var candidates = after.Categories.Where(c => c.Name == reference.Name && !catalog.Categories.Any(old => old.Index == c.Index)).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("Soundpad did not confirm a unique managed category. No existing category was claimed.");
        reference.NativeId = candidates[0].Index; reference.State = "created"; checkpoint(); return candidates[0].Index;
    }
    public static async Task<NativeAudioReference> Import(ISoundpad pipe, string path, int category, string expectedName)
    {
        var before = await Read(pipe);
        var target = before.Categories.SingleOrDefault(c => c.Index == category && c.Name == expectedName) ?? throw new InvalidDataException("The destination category changed. Refresh and choose it again.");
        var existing = before.Entries.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (existing.Length > 1) throw new InvalidDataException("Multiple native entries reference this managed file. Resolve duplicates in Soundpad before retrying.");
        if (existing.Length == 0) await Ok(pipe, $"DoAddSound({Quote(path)}, {category}, 1)");
        var after = await Read(pipe); var matches = after.Entries.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !after.Categories.Any(c => c.Index == target.Index && c.Name == target.Name && c.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))) throw new InvalidDataException("Soundpad has not confirmed the file in its intended category. The source is preserved; check the category and retry.");
        return new() { Path = path, ObservedIndex = matches[0].Index, CategoryId = category, LibraryFingerprint = after.Fingerprint, VerifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
    }
}
