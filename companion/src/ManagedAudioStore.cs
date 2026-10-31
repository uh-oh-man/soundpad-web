using System.Security.Cryptography;
using System.Text.Json;
namespace SoundpadWeb;

public sealed partial class ManagedAudioStore
{
    private readonly string directory;
    private readonly ISoundpad pipe;
    private readonly Func<string, string> nativeId;
    private readonly Func<Func<Task>, Task> nativeBatch;
    private AudioJournal journal = new();
    private bool blocked, writeFailed;
    private object[] hostLocations = [];
    private AudioManagerView published = new(0, [], [], null, null, false, 0);
    private Dictionary<string, AudioAnnotation> annotations = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;
    public string Root => Path.Combine(directory, "managed-audio");
    private string Sessions => Path.Combine(Root, "sessions");
    private string Incoming => Path.Combine(Root, "incoming");
    private string Library => Path.Combine(directory, "library");
    private string JournalPath => Path.Combine(Root, "journal.json");
    private string LegacyRoot => Path.Combine(directory, "session-audio");
    public AudioManagerView Public => Volatile.Read(ref published);
    public bool SessionOpen => journal.SessionOpen;
    public ManagedAudioStore(string directory, ISoundpad pipe, Func<string, string> nativeId, Func<Func<Task>, Task> nativeBatch)
    {
        this.directory = directory; this.pipe = pipe; this.nativeId = nativeId; this.nativeBatch = nativeBatch;
        Load();
    }
    private void Load()
    {
        try
        {
            if (File.Exists(JournalPath))
            {
                ManagedFiles.RequireOwnedPath(JournalPath, Root);
                if (new FileInfo(JournalPath).Length > 4 * 1024 * 1024) throw new InvalidDataException();
                using var document = JsonDocument.Parse(File.ReadAllBytes(JournalPath));
                if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 3 || !document.RootElement.TryGetProperty("appId", out _) || !document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing journal schema fields.");
                journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(JournalPath), Json.Options) ?? throw new InvalidDataException(); Validate();
            }
            else if (File.Exists(Path.Combine(LegacyRoot, "journal.json"))) Migrate();
            blocked = false; Publish();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            blocked = true;
            journal = new() { Issues = [new("journal-invalid", null, "journal-invalid", "The managed-audio journal is invalid or unreadable. Its bytes and audio files are preserved. Restore a valid journal or its migration backup, then retry recovery. Ordinary Soundpad playback remains available.", Action: "repair-journal")] };
            Publish();
        }
    }
    private void Validate()
    {
        if (journal.SchemaVersion != 3 || !Guid.TryParseExact(journal.AppId, "N", out _) || journal.Assets.Count > 2048 || journal.Issues.Count > 128 || (journal.SessionId != null && !Guid.TryParse(journal.SessionId, out _))) throw new InvalidDataException("Invalid audio journal schema.");
        if (journal.Assets.Select(a => a.Id).Distinct().Count() != journal.Assets.Count) throw new InvalidDataException("Duplicate asset identity.");
        foreach (var asset in journal.Assets)
        {
            if (!Guid.TryParse(asset.Id, out _) || asset.Name.Length > 120 || asset.Uploader.Length > 40 || asset.UploaderId.Length > 128 || asset.Origin is not ("native" or "upload") || asset.Variants.Count > 64) throw new InvalidDataException("Invalid audio asset.");
            if (asset.Variants.Select(v => v.Id).Distinct().Count() != asset.Variants.Count) throw new InvalidDataException("Duplicate variant identity.");
            foreach (var variant in asset.Variants)
            {
                if (!Guid.TryParse(variant.Id, out _) || variant.Orientation is not ("normal" or "reversed") || variant.Storage is not ("session" or "legacy" or "permanent" or "external") || !Path.IsPathFullyQualified(variant.Path) || variant.FileHash.Length is not (0 or 64) || variant.Bytes is < 0 or > 536870912) throw new InvalidDataException("Invalid audio variant.");
                if (variant.OwnsFile && variant.Storage is "session" or "legacy")
                {
                    if (variant.Storage == "session" && !Guid.TryParse(variant.SessionId, out _)) throw new InvalidDataException("Missing file ownership session.");
                    ManagedFiles.RequireOwnedPath(variant.Path, variant.Storage == "legacy" ? LegacyRoot : Path.Combine(Sessions, variant.SessionId!, asset.Id));
                }
                if (variant.Storage == "external" && (variant.OwnsFile || asset.Origin != "native")) throw new InvalidDataException("Invalid external ownership.");
                if (variant.Storage == "permanent" && (ManagedFiles.Beneath(variant.Path, Root) || ManagedFiles.Beneath(variant.Path, LegacyRoot))) throw new InvalidDataException("Permanent storage is inside temporary storage.");
                if (variant.Permanent && variant.Storage != "permanent") throw new InvalidDataException("Permanent audio still references temporary storage.");
            }
            if (asset.Input != null) { if (!Guid.TryParse(asset.Input.SessionId, out _)) throw new InvalidDataException("Missing input ownership session."); ManagedFiles.RequireOwnedPath(asset.Input.Path, Path.Combine(Incoming, asset.Input.SessionId, asset.Id)); if (asset.Input.Hash.Length != 64 || asset.Input.Bytes is < 1 or > 16777216) throw new InvalidDataException("Invalid staged upload."); }
        }
    }
    private void Migrate()
    {
        var original = Path.Combine(LegacyRoot, "journal.json"); ManagedFiles.RequireOwnedPath(original, LegacyRoot);
        var bytes = File.ReadAllBytes(original); if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException();
        var legacy = JsonSerializer.Deserialize<List<ManagedAudio>>(bytes, Json.Options) ?? throw new InvalidDataException();
        if (legacy.Count > 2048) throw new InvalidDataException();
        var backup = Path.Combine(LegacyRoot, "journal.v2.backup.json"); if (!File.Exists(backup)) File.WriteAllBytes(backup, bytes);
        var records = new Dictionary<string, ManagedAudio>(StringComparer.OrdinalIgnoreCase);
        foreach (var old in legacy)
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(old.File), "N", out _) || Path.GetExtension(old.File) != ".wav" || Path.GetFileName(old.File) != old.File) throw new InvalidDataException("Legacy audio path is invalid.");
            var path = Path.Combine(LegacyRoot, old.File); ManagedFiles.RequireOwnedPath(path, LegacyRoot);
            if (!records.TryAdd(path, old)) throw new InvalidDataException("Duplicate legacy file identity.");
        }
        (string Root, bool Reversed) ResolveRoot(string path, HashSet<string> seen)
        {
            if (!seen.Add(path) || seen.Count > 64) throw new InvalidDataException("Legacy source relationships are cyclic or too deep.");
            if (!records.TryGetValue(path, out var record) || record.Source == null) return (path, false);
            int last = record.Source.LastIndexOf(':'), previous = last > 0 ? record.Source.LastIndexOf(':', last - 1) : -1;
            if (previous < 2) throw new InvalidDataException("Legacy source relationship is invalid.");
            var source = record.Source[..previous]; if (!Path.IsPathFullyQualified(source)) throw new InvalidDataException("Invalid legacy source path.");
            var resolved = ResolveRoot(source, seen); return (resolved.Root, !resolved.Reversed);
        }
        var roots = new Dictionary<string, AudioAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, old) in records.OrderBy(record => ResolveRoot(record.Key, new(StringComparer.OrdinalIgnoreCase)).Root.Equals(record.Key, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var resolved = ResolveRoot(path, new(StringComparer.OrdinalIgnoreCase));
            if (!roots.TryGetValue(resolved.Root, out var asset))
            {
                bool upload = records.TryGetValue(resolved.Root, out var rootRecord);
                // Only legacy files under our owned root are read during migration. External
                // roots are hashed later, after membership in the live native catalog is verified.
                var rootHash = upload && File.Exists(resolved.Root) ? ManagedFiles.Hash(resolved.Root) : "";
                asset = new() { Origin = upload ? "upload" : "native", Name = Clean(upload ? rootRecord!.Name : Path.GetFileNameWithoutExtension(resolved.Root), 120), Uploader = Clean(upload ? rootRecord!.By : old.By, 40), SourcePath = resolved.Root, SourceHash = rootHash, State = "temporary", SourceSoundId = upload ? null : nativeId(resolved.Root), WorkspaceKey = nativeId(resolved.Root) };
                if (!upload) asset.Variants.Add(new() { Orientation = "normal", Path = resolved.Root, Storage = "external", State = "pending-source", Native = Reference(resolved.Root) });
                roots[resolved.Root] = asset; journal.Assets.Add(asset);
            }
            var exists = File.Exists(path); var hash = exists ? ManagedFiles.Hash(path) : "";
            var orientation = resolved.Reversed ? "reversed" : "normal";
            bool redundant = asset.Variants.Any(v => v.Orientation == orientation);
            var variant = new AudioVariant { Orientation = orientation, Path = path, Storage = "legacy", OwnsFile = true, FileHash = hash, SourceHash = asset.SourceHash, Bytes = exists ? new FileInfo(path).Length : 0, LegacyApproval = old.Permanent, Current = !redundant, Retired = redundant && !old.Permanent, State = exists ? "ready" : "missing", Native = Reference(path), WorkspaceKey = nativeId(path) };
            asset.Variants.Add(variant);
            if (old.Permanent) { asset.State = "accepted-needs-migration"; Issue(asset, "legacy-approval", "Previously accepted audio is preserved. Choose Keep permanently to move it into durable library storage and verify its native destination.", "info", "promote"); }
        }
        Save();
    }
    private static string Clean(string input, int limit) => new(input.Where(c => !char.IsControl(c)).Take(limit).ToArray());
    private NativeAudioReference Reference(string path) => new() { Path = path, SoundId = nativeId(path) };
    private void RequireWritable()
    {
        if (blocked) throw new InvalidDataException("Managed audio is blocked by an invalid journal. Repair it and retry recovery; existing files are preserved.");
        if (writeFailed) Save();
    }
    private void Save()
    {
        if (blocked) throw new InvalidDataException("The recovery journal is blocked.");
        Validate(); journal.Revision++;
        try
        {
            Directory.CreateDirectory(Root); ManagedFiles.RequireOwnedPath(JournalPath, Root); ManagedFiles.RequireOwnedPath(JournalPath + ".tmp", Root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Json.Options);
            if(bytes.Length > 4*1024*1024) throw new InvalidDataException("The managed recovery journal reached its 4 MiB metadata limit. Existing committed files are preserved.");
            using (var file = new FileStream(JournalPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(JournalPath + ".tmp", JournalPath, true); writeFailed = false; Publish();
        }
        catch { writeFailed = true; Publish(); throw; }
    }
    private void Publish()
    {
        var map = new Dictionary<string, AudioAnnotation>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<AudioAssetView>();
        foreach (var asset in journal.Assets)
        {
            var current = asset.Variants.Where(v => v.Current && v.State != "removed").ToArray();
            var normal = current.LastOrDefault(v => v.Orientation == "normal"); var reversed = current.LastOrDefault(v => v.Orientation == "reversed");
            var canonical = normal is { Retired: false, State: "ready", Native: { VerifiedAt: > 0 } } ? normal : reversed is { Permanent: true, State: "ready", Native: { VerifiedAt: > 0 } } ? reversed : null;
            foreach (var variant in asset.Variants)
            {
                var id = variant.Native?.SoundId; if (id == null) continue;
                var aliases = asset.Variants.Where(v => v.Orientation == variant.Orientation && v.Native?.SoundId != null).Select(v => v.Native!.SoundId!).Distinct().Take(64).ToArray();
                var metadataKey = variant.Orientation == "normal" ? asset.WorkspaceKey ?? asset.SourceSoundId ?? id : variant.WorkspaceKey ?? id;
                map[id] = new(asset.Id, asset.Name + (variant.Orientation == "reversed" ? " (reversed)" : ""), asset.Uploader, variant.Orientation, variant.OwnsFile && !variant.Permanent && !variant.LegacyApproval, variant.OwnsFile ? variant.Permanent || variant.LegacyApproval ? "accepted" : variant.Orientation == "reversed" || asset.Origin == "native" ? "generated" : "upload" : "native", canonical != variant && variant.OwnsFile, canonical?.Native?.SoundId, metadataKey, aliases, variant.Retired && !variant.Permanent && !variant.LegacyApproval);
            }
            AudioVariantView View(AudioVariant v) => new(v.Id, v.Native?.SoundId, v.Orientation, v.Retired ? "cleanup-pending" : v.State, v.State == "ready" && v.Native is { VerifiedAt: > 0 } && !v.Retired, v.Permanent, v.OwnsFile ? Path.GetFileNameWithoutExtension(v.Path) : null, v.Current, v.LegacyApproval);
            assets.Add(new(asset.Id, asset.Name, asset.Origin, asset.Uploader, asset.State, asset.CreatedAt, normal == null ? null : View(normal), reversed == null ? null : View(reversed), asset.Variants.Where(v => v.State != "removed").Select(View).ToArray(), asset.Variants.Any(v => v.OwnsFile && !v.Permanent && !v.LegacyApproval && v.State != "removed")));
        }
        var issues = journal.Issues.TakeLast(128).ToList();
        if (writeFailed) issues.Add(new("storage-write", null, "storage-write", "The recovery journal could not be saved. Managed mutations and cleanup are paused until local storage is writable.", Action: "retry"));
        var view = new AudioManagerView(journal.Revision, assets.ToArray(), issues.ToArray(), journal.TempCategory?.NativeId, journal.TempCategory?.Name, blocked || writeFailed, TemporaryBytes());
        Volatile.Write(ref hostLocations, journal.Assets.SelectMany(a => a.Variants.Where(v => v.OwnsFile && v.State != "removed").Select(v => (object)new { assetId = a.Id, variantId = v.Id, v.Path, v.Storage })).ToArray());
        Volatile.Write(ref annotations, map); Volatile.Write(ref published, view); Changed?.Invoke();
    }
    public AudioAnnotation? Annotation(string soundId) => Volatile.Read(ref annotations).GetValueOrDefault(soundId);
    private long TemporaryBytes()
    {
        long Size(string path, long recorded) { try { return Math.Max(recorded, File.Exists(path) ? new FileInfo(path).Length : 0); } catch (IOException) { return Math.Max(recorded, 64L * 1024 * 1024); } }
        return journal.Assets.Sum(a => (a.Input == null ? 0 : Size(a.Input.Path, a.Input.Bytes)) + a.Variants.Where(v => v.OwnsFile && !v.Permanent && v.State != "removed").Sum(v => Size(v.Path, v.Bytes)));
    }
    private void Capacity(int additionalFiles = 1)
    {
        int count = journal.Assets.Sum(a => (a.Input == null ? 0 : 1) + a.Variants.Count(v => v.OwnsFile && !v.Permanent && v.State != "removed"));
        if (count + additionalFiles > 32 || TemporaryBytes() >= 256L * 1024 * 1024) throw new InvalidDataException("Temporary storage is full (32 files / 256 MiB). Resolve pending cleanup in Temp before adding more audio.");
        if (journal.Assets.Count >= 2048) throw new InvalidDataException("The managed-audio catalog reached its 2,048-source limit.");
    }
    private void Issue(AudioAsset? asset, string code, string message, string severity = "error", string action = "retry")
    {
        var id = (asset?.Id ?? "manager") + ":" + code;
        journal.Issues.RemoveAll(i => i.Id == id); journal.Issues.Add(new(id, asset?.Id, code, message, severity, action));
        if (journal.Issues.Count > 128) journal.Issues.RemoveRange(0, journal.Issues.Count - 128);
    }
    private void ClearIssues(AudioAsset asset, params string[] codes) => journal.Issues.RemoveAll(i => i.AssetId == asset.Id && codes.Contains(i.Code));
    private async Task<T> Native<T>(Func<Task<T>> work) { T result = default!; await nativeBatch(async () => result = await work()); return result; }
    public AudioAsset AssetById(string id) => Asset(id);
    private AudioAsset Asset(string id) => journal.Assets.SingleOrDefault(a => a.Id == id) ?? throw new InvalidDataException("Unknown managed source.");
    private AudioVariant Variant(AudioAsset asset, string orientation) => asset.Variants.LastOrDefault(v => v.Current && v.Orientation == orientation && v.State != "removed") ?? throw new InvalidDataException("This orientation is not available.");
    public void BeginSession()
    {
        RequireWritable(); if (journal.SessionOpen) return; journal.SessionId = Guid.NewGuid().ToString(); journal.SessionOpen = true; Save();
    }
    public void RetireSession()
    {
        RequireWritable(); journal.SessionOpen = false;
        foreach (var asset in journal.Assets)
        {
            if (asset.Input != null) asset.Input.State = "retired";
            foreach (var variant in asset.Variants.Where(v => v.OwnsFile && !v.Permanent && !v.LegacyApproval && v.State != "removed")) { variant.Retired = true; asset.State = "cleanup-pending"; }
        }
        Save();
    }
    public void RetryJournal() { if (blocked) { journal = new(); Load(); } RequireWritable(); }
}
public record AudioAnnotation(string AssetId, string Name, string Uploader, string Orientation, bool Temporary, string Kind, bool InternalVariant, string? AliasOf, string WorkspaceId, string[] Aliases, bool Retired);
