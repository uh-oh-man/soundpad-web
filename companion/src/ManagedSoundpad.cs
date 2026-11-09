using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
namespace SoundpadWeb;

internal static class ManagedSoundpad
{
    /// <summary>Marks the error raised when Soundpad holds garbled copies of an app category (see <see cref="GarbledCopies"/>).</summary>
    public const string GarbledKey = "garbledCategories";
    private static InvalidDataException Garbled(int count)
    {
        var error = new InvalidDataException($"Soundpad has {count} cop{(count == 1 ? "y" : "ies")} of the Temp category with a garbled name, made by companions before 2.15.9. Use Remove duplicate categories in Temp, then retry.");
        error.Data[GarbledKey] = count;
        return error;
    }
    /// <summary>A name reduced to ASCII letters, digits, brackets and hyphens, so "Temp Â· Soundpad Web [x]" and "Temp · Soundpad Web [x]" compare equal.</summary>
    private static string Fold(string name) => new(name.Where(c => c < 128 && (char.IsLetterOrDigit(c) || c is '[' or ']' or '-')).ToArray());
    /// <summary>
    /// Categories that are this app category under a different (garbled) name. The random tag in the name
    /// (e.g. "[ed4a3e-ee037c]") makes a match ours; an exact-name match is the real one, not a copy.
    /// </summary>
    public static NativeCategory[] GarbledCopies(NativeCatalog catalog, OwnedCategory reference) =>
        catalog.Categories.Where(c => c.Name != reference.Name && c.Name.Length > 0 && Fold(c.Name) == Fold(reference.Name) && reference.Name.Contains('[')).ToArray();
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
        // Garbled copies mean an earlier companion's creations were never recognised: stop rather than add another.
        var garbled = GarbledCopies(catalog, reference);
        if (garbled.Length > 0) throw Garbled(garbled.Length);
        // Never adopt a category just because its display name matches.
        if (catalog.Categories.Any(c => c.Name == reference.Name)) throw new InvalidDataException("The planned category already exists without a verified creation record. Choose a new managed category.");
        reference.NativeId = null; reference.State = "creating"; checkpoint();
        await Ok(pipe, $"DoAddCategory({Quote(reference.Name)}, -1)");
        var after = await Read(pipe); var candidates = after.Categories.Where(c => c.Name == reference.Name && !catalog.Categories.Any(old => old.Index == c.Index)).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("Soundpad did not confirm a unique managed category. No existing category was claimed.");
        reference.NativeId = candidates[0].Index; reference.State = "created"; checkpoint(); return candidates[0].Index;
    }
    /// <summary>
    /// Removes the empty garbled copies of the given app categories. Copies holding any sound are kept.
    /// Works in batches from the highest index down, so a removal never shifts a later target, and re-reads
    /// Soundpad before each batch to confirm every target is still an empty garbled copy at that index.
    /// </summary>
    public static async Task<int> RemoveGarbledCopies(ISoundpad pipe, IEnumerable<OwnedCategory> references, Action<int>? progress = null)
    {
        var owned = references.ToArray(); int removed = 0;
        while (true)
        {
            var catalog = await Read(pipe);
            var targets = owned.SelectMany(r => GarbledCopies(catalog, r)).Where(c => c.Paths.Length == 0).OrderByDescending(c => c.Index).Take(50).ToArray();
            if (targets.Length == 0) return removed;
            foreach (var target in targets) { await Ok(pipe, $"DoRemoveCategory({target.Index})"); removed++; }
            // Every other category must be exactly as before, in order: only the batch may have gone.
            // Soundpad applies removals a moment later on its own thread, so re-read until it has caught up (on the
            // owner's PC a check right after a batch sometimes still saw the last few).
            var expected = catalog.Categories.Where(c => !targets.Contains(c)).OrderBy(c => c.Index).Select(c => c.Name).ToArray();
            bool settled = false;
            for (int attempt = 0; attempt < 20 && !settled; attempt++)
            {
                if (attempt > 0) await Task.Delay(150);
                settled = (await Read(pipe)).Categories.OrderBy(c => c.Index).Select(c => c.Name).SequenceEqual(expected);
            }
            if (!settled) throw new InvalidDataException("Soundpad's categories changed during cleanup, so it stopped. Retry when Soundpad is idle.");
            progress?.Invoke(removed);
        }
    }
    /// <summary>
    /// Removes one category and, as Soundpad does, every sound in it (measured on the owner's Soundpad 4.0.35:
    /// a removed category's sounds leave the main list too; files on disk are untouched). Then waits until
    /// Soundpad shows exactly that: the category and its sounds gone, everything else unchanged.
    /// </summary>
    public static async Task RemoveCategoryWithSounds(ISoundpad pipe, NativeCatalog before, NativeCategory category)
    {
        await Ok(pipe, $"DoRemoveCategory({category.Index})");
        var gone = new HashSet<string>(category.Paths, StringComparer.OrdinalIgnoreCase);
        var expectedSounds = before.Entries.Select(e => e.Path).Where(p => !gone.Contains(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var expectedCategories = before.Categories.Where(c => c.Index != category.Index).OrderBy(c => c.Index).Select(c => c.Name).ToArray();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (attempt > 0) await Task.Delay(150);
            var after = await Read(pipe);
            if (after.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).SequenceEqual(expectedSounds, StringComparer.OrdinalIgnoreCase)
                && after.Categories.OrderBy(c => c.Index).Select(c => c.Name).SequenceEqual(expectedCategories)) return;
        }
        throw new InvalidDataException("Soundpad did not confirm removing the Temp category. Nothing else was changed; cleanup retries later.");
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
