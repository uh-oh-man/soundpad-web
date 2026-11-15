using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
namespace SoundpadWeb;

/// <summary>
/// One Soundpad category as Organize sees it (2.44): where it sits (the names of its parents, root first), the
/// sounds directly in it (paths, in Soundpad's order) and its child categories. The index is only where it is now:
/// Soundpad renumbers categories when one is added or removed, so a step never trusts an index it read earlier.
/// </summary>
public sealed record NativeNode(int Index, string Name, string? Type, string[] Chain, string[] Paths, NativeNode[] Children, int? ParentIndex)
{
    public bool Builtin => Type == "1";
    public bool Root => Chain.Length == 0;
    /// <summary>A stable fingerprint of what is in it: SHA-256 of its sounds' upper-cased paths, sorted.</summary>
    public string PathsHash => NativeTree.HashPaths(Paths);
    public CategoryRef Ref => new(Name, Chain, PathsHash);
}
/// <summary>
/// What a step remembers about a category (2.44): its name, its parents' names and what is in it. Indexes shift, so
/// a target is found again by this (unique match), or by name and parents alone (only with the host's confirmation).
/// </summary>
public sealed record CategoryRef(string Name, string[] Chain, string PathsHash)
{
    public bool SamePlace(NativeNode node) => node.Name == Name && node.Chain.SequenceEqual(Chain);
    public bool Matches(NativeNode node) => SamePlace(node) && node.PathsHash == PathsHash;
}
/// <summary>Soundpad's library at one moment: the sound list and the category tree (2.44, Organize).</summary>
public sealed class NativeTree
{
    public NativeEntry[] Entries { get; }
    public NativeNode[] Roots { get; }
    /// <summary>Every category, depth first (the All sounds category included; see <see cref="NativeNode.Builtin"/>).</summary>
    public NativeNode[] All { get; }
    /// <summary>SHA-256 of the raw sound list and categories, so any change at all shows.</summary>
    public string Fingerprint { get; }
    private NativeTree(NativeEntry[] entries, NativeNode[] roots, NativeNode[] all, string fingerprint) { Entries = entries; Roots = roots; All = all; Fingerprint = fingerprint; }
    public IEnumerable<NativeNode> Categories => All.Where(n => !n.Builtin);
    public NativeNode? ByIndex(int index) => All.FirstOrDefault(n => n.Index == index);
    public int Count(string path) => Entries.Count(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
    public static string HashPaths(IEnumerable<string> paths) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", paths.Select(p => p.Replace('/', '\\').ToUpperInvariant()).Order(StringComparer.Ordinal))))).ToLowerInvariant();
    private static XDocument Xml(string text, string root)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != root) throw new InvalidDataException("Soundpad returned unavailable or invalid library metadata.");
        return document;
    }
    public static async Task<NativeTree> Read(ISoundpad pipe)
    {
        var soundlist = await pipe.Request("GetSoundlist()");
        var categories = await pipe.Request("GetCategories(true, false)");
        return Parse(soundlist, categories);
    }
    public static NativeTree Parse(string soundlist, string categories)
    {
        var all = Xml(soundlist, "Soundlist"); var cats = Xml(categories, "Categories");
        var entries = all.Root!.Elements("Sound").Select(e => new NativeEntry((int?)e.Attribute("index") ?? -1, (string?)e.Attribute("url") ?? "")).ToArray();
        if (entries.Any(e => e.Index < 1 || string.IsNullOrWhiteSpace(e.Path)) || entries.Select(e => e.Index).Distinct().Count() != entries.Length) throw new InvalidDataException("Soundpad's global mapping is ambiguous.");
        var flat = new List<NativeNode>();
        NativeNode Node(XElement e, string[] chain, int? parent, int depth)
        {
            if (depth > 16) throw new InvalidDataException("Category tree too deep.");
            int index = (int?)e.Attribute("index") ?? -1; var name = (string?)e.Attribute("name") ?? "";
            var placeholder = new NativeNode(index, name, (string?)e.Attribute("type"), chain, [.. e.Elements("Sound").Select(s => (string?)s.Attribute("url") ?? "")], [], parent);
            int at = flat.Count; flat.Add(placeholder);
            var children = e.Elements("Category").Select(c => Node(c, [.. chain, name], index, depth + 1)).ToArray();
            var node = placeholder with { Children = children };
            flat[at] = node; return node;
        }
        var roots = cats.Root!.Elements("Category").Select(c => Node(c, [], null, 0)).ToArray();
        if (flat.Any(c => c.Index < 0) || flat.Select(c => c.Index).Distinct().Count() != flat.Count) throw new InvalidDataException("Soundpad's category mapping is ambiguous.");
        return new(entries, roots, [.. flat], ManagedSoundpad.Hash(soundlist + "\n" + categories));
    }
    /// <summary>
    /// Finds a step's category again: the one with the same name, parents and contents; else, when exactly one has the
    /// same name and parents, that one as a candidate the host must confirm (<paramref name="confirm"/> is set).
    /// </summary>
    public NativeNode? Find(CategoryRef reference, out NativeNode? confirm)
    {
        confirm = null;
        var exact = Categories.Where(reference.Matches).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return null;
        var place = Categories.Where(reference.SamePlace).ToArray();
        if (place.Length == 1) confirm = place[0];
        return null;
    }
    /// <summary>
    /// The categories as a multiset of "where, name and contents" signatures (the built-in All sounds excluded), for
    /// checking that a step made exactly its one expected change, whatever Soundpad did to the indexes.
    /// </summary>
    public List<string> Signatures() => [.. Categories.Select(Signature).Order(StringComparer.Ordinal)];
    public static string Signature(NativeNode n) => Signature(n.Chain, n.Name, n.Paths);
    public static string Signature(IEnumerable<string> chain, string name, IEnumerable<string> paths) =>
        string.Join("\u001f", chain) + "\u001e" + name + "\u001e" + string.Join("\n", paths.Select(p => p.Replace('/', '\\').ToUpperInvariant()).Order(StringComparer.Ordinal));
    /// <summary>The sound list as a sorted multiset of upper-cased paths.</summary>
    public List<string> SoundPaths() => [.. Entries.Select(e => e.Path.Replace('/', '\\').ToUpperInvariant()).Order(StringComparer.Ordinal)];
}
