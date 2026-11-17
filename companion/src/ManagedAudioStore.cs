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
    private Dictionary<string, ReadyAudio> ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileStamp> stamps = new();
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
            assets.Add(new(asset.Id, asset.Name, asset.Origin, asset.Uploader, asset.State, asset.CreatedAt, normal == null ? null : View(normal), reversed == null ? null : View(reversed), asset.Variants.Where(v => v.State != "removed").Select(View).ToArray(),
                // A removed file that waits for cleanup is no longer Temp audio of the source (2.46).
                asset.Variants.Any(v => v.OwnsFile && !v.Permanent && !v.LegacyApproval && v.State != "removed" && !v.Retired)));
        }
        // What Prepare would hand back without generating anything: per source and direction, the current ready
        // variant made from the current source. Only one that is in Soundpad and not retired can be played at once.
        var playable = new Dictionary<string, ReadyAudio>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in journal.Assets)
            foreach (var orientation in new[] { "normal", "reversed" })
            {
                var variant = asset.Variants.LastOrDefault(v => v.Current && v.Orientation == orientation && v.State == "ready" && v.SourceHash == asset.SourceHash);
                if (variant is not { Retired: false, Native: { VerifiedAt: > 0, SoundId: not null } }) continue;
                var source = asset.Origin == "native" ? asset.Variants.LastOrDefault(v => v.Current && v.Orientation == "normal" && !v.OwnsFile)?.Path : null;
                playable[asset.Id + "|" + orientation] = new(asset.Id, asset.State, variant.Id, variant.Native.SoundId, variant.Path, variant.Bytes, source);
            }
        var issues = journal.Issues.TakeLast(128).ToList();
        if (writeFailed) issues.Add(new("storage-write", null, "storage-write", "The recovery journal could not be saved. Managed mutations and cleanup are paused until local storage is writable.", Action: "retry"));
        var usage = TempUsage();
        var view = new AudioManagerView(journal.Revision, assets.ToArray(), issues.ToArray(), journal.TempCategory?.NativeId, journal.TempCategory?.Name, blocked || writeFailed, usage.Bytes,
            usage.WaitingFiles > 0 ? new TempWaiting(usage.WaitingFiles, usage.WaitingBytes) : null);
        // Removed files still on the PC, by who removed them: a guest's count in that guest's Temp budget until deleted.
        var waitingBy = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (removedNow)
            foreach (var (id, removal) in removedNow)
                if (journal.Assets.FirstOrDefault(a => a.Id == id) is { } removedAsset && removedAsset.Variants.Any(v => removal.VariantIds.Contains(v.Id) && v.Retired && v.State != "removed"))
                    waitingBy[id] = removal.By;
        Volatile.Write(ref removedWaiting, waitingBy);
        Volatile.Write(ref hostLocations, journal.Assets.SelectMany(a => a.Variants.Where(v => v.OwnsFile && v.State != "removed").Select(v => (object)new { assetId = a.Id, variantId = v.Id, v.Path, v.Storage })).ToArray());
        Volatile.Write(ref ready, playable);
        // What the companion's poll retries every few seconds: a removal waiting for the Temp category is not (2.46).
        Volatile.Write(ref owners, journal.Assets.ToDictionary(a => a.Id, a => new AssetOwner(a.UploaderId, a.State != "permanent" && a.Promotion == null && !a.Variants.Any(v => v.Permanent || v.LegacyApproval)), StringComparer.Ordinal));
        recoveryDue = journal.Assets.Any(a => a.State is "waiting-soundpad" or "promotion-pending" || a.State == "cleanup-pending" && (a.Input is { State: "retired" } || a.Variants.Any(v => v.Retired && v.State != "removed" && !v.Permanent && !Deferred(v))));
        Volatile.Write(ref annotations, map); Volatile.Write(ref published, view); Changed?.Invoke();
    }
    private volatile bool recoveryDue;
    private Dictionary<string, string> removedWaiting = new(StringComparer.Ordinal);
    /// <summary>
    /// Who removed this source in this launch, while any file that removal retired is still on the PC (waiting for the
    /// Temp category to go, or for Soundpad to be quiet); else null. As last published, safe from any thread.
    /// </summary>
    public string? WaitingRemovedBy(string assetId) => Volatile.Read(ref removedWaiting).GetValueOrDefault(assetId);
    private Dictionary<string, AssetOwner> owners = new(StringComparer.Ordinal);
    /// <summary>
    /// Who added a source (its uploader's connection id) and whether it is only temporary audio (never kept, nor being
    /// kept), as last published (2.46). Safe to call from any thread, unlike <see cref="AssetById"/>.
    /// </summary>
    public AssetOwner? OwnerOf(string assetId) => Volatile.Read(ref owners).GetValueOrDefault(assetId);
    /// <summary>Whether recovery or cleanup has something to retry (2.46; until then, any source in one of these states).</summary>
    public bool RecoveryDue => recoveryDue;
    public AudioAnnotation? Annotation(string soundId) => Volatile.Read(ref annotations).GetValueOrDefault(soundId);
    /// <summary>
    /// The variant a play of this source and direction would use, if it can be played right now without any
    /// preparation (2.35); null when it must go through <see cref="Prepare"/> first. Safe to call from any thread.
    /// <para>
    /// Prepare verifies, on every call: the journal is writable; for a reverse of a library sound, that the
    /// original is still in Soundpad and its content hash is unchanged (else the reverse is stale and retired);
    /// the managed file's length and full SHA-256; and that Soundpad lists the file exactly once (else it is
    /// imported again). This check keeps the guarantees that matter for a play without reading file contents:
    /// the variant is the current one for the current source, is recorded as imported and not retired, Prepare
    /// fully verified it earlier in this run, and since then neither the managed file nor the original has
    /// changed in size or modified time. The caller still confirms from a fresh sound list that Soundpad lists the
    /// file. Anything else (first play after a start, a changed file, a missing entry) takes the full Prepare.
    /// Keep, Remove and clean-up are unaffected: they always verify by hash themselves.
    /// </para>
    /// </summary>
    public ReadyAudio? Ready(string assetId, string orientation)
    {
        if (!Volatile.Read(ref ready).TryGetValue(assetId + "|" + orientation, out var found) || !stamps.TryGetValue(found.VariantId, out var stamp)) return null;
        return FileStamp.Of(found.Path, found.SourcePath) is { } now && now == stamp && now.Bytes == found.Bytes ? found : null;
    }
    /// <summary>Remembers the files' size and time at the moment Prepare (or an upload) has fully verified a variant.</summary>
    private void Stamp(AudioAsset asset, AudioVariant variant)
    {
        var source = asset.Origin == "native" ? asset.Variants.LastOrDefault(v => v.Current && v.Orientation == "normal" && !v.OwnsFile)?.Path : null;
        if (FileStamp.Of(variant.Path, source) is { } stamp) stamps[variant.Id] = stamp; else stamps.TryRemove(variant.Id, out _);
    }
    /// <summary>Temp's capacity (2.18): files and bytes of temporary audio on the PC, removed files included until deleted.</summary>
    public const int TempFileLimit = 32;
    public const long TempByteLimit = 256L * 1024 * 1024;
    /// <summary>
    /// Every temporary file on the PC (staged inputs and owned temporary variants not yet deleted) and, of those, the
    /// ones already removed or retired that wait to be deleted: with the real Soundpad, a removed clip's file waits for
    /// the Temp category to go (quit or Clear Temp, see <see cref="Deferred"/>). They still take Temp's capacity.
    /// </summary>
    private (int Files, long Bytes, int WaitingFiles, long WaitingBytes) TempUsage()
    {
        static long Size(string path, long recorded) { try { return Math.Max(recorded, File.Exists(path) ? new FileInfo(path).Length : 0); } catch (IOException) { return Math.Max(recorded, 64L * 1024 * 1024); } }
        int files = 0, waitingFiles = 0; long bytes = 0, waitingBytes = 0;
        foreach (var a in journal.Assets)
        {
            if (a.Input != null)
            {
                long size = Size(a.Input.Path, a.Input.Bytes); files++; bytes += size;
                if (a.Input.State == "retired") { waitingFiles++; waitingBytes += size; }
            }
            foreach (var v in a.Variants.Where(v => v.OwnsFile && !v.Permanent && v.State != "removed"))
            {
                long size = Size(v.Path, v.Bytes); files++; bytes += size;
                if (v.Retired && !v.LegacyApproval) { waitingFiles++; waitingBytes += size; }
            }
        }
        return (files, bytes, waitingFiles, waitingBytes);
    }
    private long TemporaryBytes() => TempUsage().Bytes;
    private void Capacity(int additionalFiles = 1, long additionalBytes = 0)
    {
        var usage = TempUsage();
        if (usage.Files + additionalFiles > TempFileLimit || usage.Bytes + additionalBytes >= TempByteLimit)
        {
            string waiting = $"{usage.WaitingFiles} removed file{(usage.WaitingFiles == 1 ? "" : "s")} ({usage.WaitingBytes / (1024.0 * 1024):0.#} MB)";
            // Full only because removed files have not been deleted yet: say so, and what frees them.
            if (usage.WaitingFiles > 0 && usage.Files - usage.WaitingFiles + additionalFiles <= TempFileLimit && usage.Bytes - usage.WaitingBytes + additionalBytes < TempByteLimit)
                throw new InvalidDataException($"Temporary storage is full (32 files / 256 MiB), only because {waiting} {(usage.WaitingFiles == 1 ? "is" : "are")} still waiting to be freed. The host's Clear all temporary audio in Temp frees them (quitting the companion does too).");
            throw new InvalidDataException("Temporary storage is full (32 files / 256 MiB)." + (usage.WaitingFiles > 0 ? $" {waiting} of it {(usage.WaitingFiles == 1 ? "is" : "are")} waiting to be freed: the host's Clear all temporary audio in Temp frees them." : " Remove or keep some Temp audio before adding more."));
        }
        if (journal.Assets.Count >= 2048) throw new InvalidDataException("The managed-audio catalog reached its 2,048-source limit.");
    }
    private void Issue(AudioAsset? asset, string code, string message, string severity = "error", string action = "retry")
    {
        var id = (asset?.Id ?? "manager") + ":" + code;
        journal.Issues.RemoveAll(i => i.Id == id); journal.Issues.Add(new(id, asset?.Id, code, message, severity, action));
        if (journal.Issues.Count > 128) journal.Issues.RemoveRange(0, journal.Issues.Count - 128);
    }
    /// <summary>
    /// Records a failure with its actual reason, so Temp shows why (the generic text alone hid the
    /// Soundpad encoding bug). Garbled duplicate categories get their own issue with a cleanup action.
    /// </summary>
    private void Report(AudioAsset asset, string code, string generic, Exception ex)
    {
        if (ex.Data.Contains(ManagedSoundpad.GarbledKey)) { Issue(null, "category-duplicates", ex.Message, "warning", "cleanup-categories"); return; }
        Issue(asset, code, ex is InvalidDataException ? generic + " Reason: " + ex.Message : generic);
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

    // ---- Backups (2.44) --------------------------------------------------------------------------------------------
    // The caller holds the audio gate (Background lane for a capture), so the journal does not change meanwhile.

    /// <summary>A kept (permanent) file the journal records: what a backup copies and a restore brings back.</summary>
    public sealed record KeptFile(string AssetId, string VariantId, string Name, string Path, string Hash, long Bytes);

    /// <summary>
    /// The journal as it stands, for a backup: its bytes (null while it is blocked or was never written), its revision,
    /// its app id, and every kept file it records.
    /// </summary>
    public (byte[]? Bytes, long Revision, string AppId, bool OnDisk, KeptFile[] Kept) CaptureBackup()
    {
        bool onDisk = File.Exists(JournalPath);
        var kept = journal.Assets.SelectMany(a => a.Variants.Where(v => v.Permanent && v.OwnsFile && v.Storage == "permanent" && v.State == "ready" && v.FileHash.Length == 64)
            .Select(v => new KeptFile(a.Id, v.Id, a.Name, v.Path, v.FileHash, v.Bytes))).ToArray();
        return (blocked || !onDisk ? null : JsonSerializer.SerializeToUtf8Bytes(journal, Json.Options), journal.Revision, journal.AppId, onDisk && !blocked, kept);
    }

    /// <summary>The kept files of a backup's journal (strictly read: its schema and limits as Load checks them).</summary>
    public static (AudioJournal Journal, KeptFile[] Kept) ParseBackup(byte[] bytes)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("The backup's audio journal is too large.");
        using (var document = JsonDocument.Parse(bytes))
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 3) throw new InvalidDataException("The backup's audio journal has an unknown format.");
        var j = JsonSerializer.Deserialize<AudioJournal>(bytes, Json.Options) ?? throw new InvalidDataException("The backup's audio journal is empty.");
        if (j.Assets.Count > 2048 || !Guid.TryParseExact(j.AppId, "N", out _) || j.Assets.Select(a => a.Id).Distinct().Count() != j.Assets.Count) throw new InvalidDataException("The backup's audio journal is damaged.");
        var kept = j.Assets.SelectMany(a => a.Variants.Where(v => v.Permanent && v.OwnsFile && v.Storage == "permanent" && v.State == "ready" && v.FileHash.Length == 64 && Path.IsPathFullyQualified(v.Path))
            .Select(v => new KeptFile(a.Id, v.Id, a.Name, v.Path, v.FileHash, v.Bytes))).ToArray();
        return (j, kept);
    }

    /// <summary>True when the journal records this source.</summary>
    public bool Knows(string assetId) => journal.Assets.Any(a => a.Id == assetId);

    /// <summary>
    /// Puts a kept source back into the journal from a backup's (it is not there any more): only its permanent variants
    /// and its reference to a Soundpad original, never temporary or session records, and the session stays as it is.
    /// Validated and saved within the journal's limits (schema 3, 2,048 sources, 4 MiB); refused otherwise, with
    /// nothing changed. Its Soundpad entries are verified again by <see cref="RecheckKept"/>.
    /// </summary>
    public bool MergePermanent(AudioAsset fromBackup, bool keepWorkspaceKeys)
    {
        RequireWritable();
        if (journal.Assets.Any(a => a.Id == fromBackup.Id)) return false;
        if (journal.Assets.Count >= 2048) throw new InvalidDataException("The managed-audio catalog reached its 2,048-source limit.");
        var variants = fromBackup.Variants.Where(v => v.Current && (v.Permanent && v.OwnsFile && v.Storage == "permanent" && v.State == "ready" || v.Storage == "external" && !v.OwnsFile)).ToList();
        if (!variants.Any(v => v.Permanent)) return false;
        var asset = new AudioAsset
        {
            Id = fromBackup.Id, Origin = fromBackup.Origin, Name = Clean(fromBackup.Name, 120), Uploader = Clean(fromBackup.Uploader, 40), UploaderId = fromBackup.UploaderId.Length <= 128 ? fromBackup.UploaderId : "anonymous",
            SourceHash = fromBackup.SourceHash, SourcePath = fromBackup.SourcePath, UploadHash = fromBackup.UploadHash, CreatedAt = fromBackup.CreatedAt, ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), State = "permanent",
            SourceSoundId = fromBackup.Origin == "native" && fromBackup.SourcePath != null ? nativeId(fromBackup.SourcePath) : null,
            WorkspaceKey = keepWorkspaceKeys ? fromBackup.WorkspaceKey : null,
        };
        foreach (var v in variants)
        {
            asset.Variants.Add(new AudioVariant
            {
                Id = v.Id, Orientation = v.Orientation, SourceHash = v.SourceHash, Path = v.Path, FileHash = v.FileHash, Storage = v.Storage, Bytes = v.Bytes, OwnsFile = v.OwnsFile, Current = true,
                Permanent = v.Permanent, State = v.Storage == "external" ? v.State : "ready", CreatedAt = v.CreatedAt, WorkspaceKey = keepWorkspaceKeys ? v.WorkspaceKey : null,
                // The Soundpad entry is verified again before it counts (the ids follow this install's identity).
                Native = new NativeAudioReference { Path = v.Path, SoundId = nativeId(v.Path) },
            });
        }
        if (asset.WorkspaceKey == null && asset.Variants.FirstOrDefault(v => v.Orientation == "normal") is { } normal) asset.WorkspaceKey = asset.SourceSoundId ?? normal.Native!.SoundId;
        journal.Assets.Add(asset);
        try { Save(); }
        catch { journal.Assets.Remove(asset); writeFailed = false; Publish(); throw; }
        return true;
    }

    /// <summary>
    /// Verifies a kept source's Soundpad entries again (after a restore): true when every kept file is listed in
    /// Soundpad. Nothing is ever imported for a kept file; a missing entry is left for the host to add again.
    /// </summary>
    public async Task<bool> RecheckKept(string assetId)
    {
        var asset = journal.Assets.SingleOrDefault(a => a.Id == assetId);
        if (asset == null) return false;
        bool all = true;
        foreach (var variant in asset.Variants.Where(v => v.Permanent && v.Current && v.State == "ready"))
        {
            try { await EnsureImported(asset, variant); ClearIssues(asset, "native-import"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { all = false; Report(asset, "native-import", "The kept file is back, but Soundpad does not list it. Add it to Soundpad again (Saved category).", ex); Save(); }
        }
        return all;
    }
}
/// <summary>Temporary files of one source at a moment (2.40 crash recovery): its variant ids and staged input, if any.</summary>
public sealed record TemporaryFiles(string AssetId, string[] VariantIds, string? InputPath);
/// <summary>A variant that can be played without preparation; see <see cref="ManagedAudioStore.Ready"/>.</summary>
public sealed record ReadyAudio(string AssetId, string State, string VariantId, string SoundId, string Path, long Bytes, string? SourcePath);
/// <summary>Size and modified time of a managed file, and of the library original it was made from (if any).</summary>
internal readonly record struct FileStamp(long Bytes, long Written, long SourceBytes, long SourceWritten)
{
    public static FileStamp? Of(string path, string? source)
    {
        try
        {
            var file = new FileInfo(path); if (!file.Exists) return null;
            if (source == null) return new(file.Length, file.LastWriteTimeUtc.Ticks, 0, 0);
            var original = new FileInfo(source); if (!original.Exists) return null;
            return new(file.Length, file.LastWriteTimeUtc.Ticks, original.Length, original.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
public record AssetOwner(string UploaderId, bool OnlyTemporary);
public record AudioAnnotation(string AssetId, string Name, string Uploader, string Orientation, bool Temporary, string Kind, bool InternalVariant, string? AliasOf, string WorkspaceId, string[] Aliases, bool Retired);
