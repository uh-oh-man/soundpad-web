using System.Security.Cryptography;
namespace SoundpadWeb;

public sealed partial class ManagedAudioStore
{
    private readonly Dictionary<string, long> transformations = [];
    private readonly Dictionary<string, Queue<long>> uploads = [];
    public async Task<AudioAsset> Upload(byte[] bytes, string name, string uploader, string uploaderId, bool host, CancellationToken cancel = default)
    {
        RequireWritable(); if (!journal.SessionOpen) throw new InvalidDataException("Hosting has ended. Start a new session before uploading audio.");
        if (bytes.Length is < 1 or > 16777216) throw new InvalidDataException("Uploads must be between one byte and 16 MiB.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3" or ".ogg" or ".flac" or ".m4a" or ".aac" or ".wma")) throw new InvalidDataException("Choose supported WAV, MP3, OGG, FLAC, M4A, AAC or WMA audio.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var duplicate = journal.Assets.FirstOrDefault(a => a.Origin == "upload" && a.UploadHash == hash && a.UploaderId == uploaderId && a.Variants.Any(v => (v.LeaseSessionId ?? v.SessionId) == journal.SessionId && !v.Retired));
        if (duplicate != null)
        {
            var existing = duplicate.Variants.LastOrDefault(v => v.Current && v.Orientation == "normal" && v.State == "ready");
            if (existing != null) { await TryImport(duplicate, existing); return duplicate; }
            throw new InvalidDataException("This upload is already processing or needs recovery. Open Temp and retry its pending operation.");
        }
        if (!host)
        {
            var now = Environment.TickCount64;
            foreach(var key in uploads.Where(p=>p.Value.Count==0 || now-p.Value.Last()>60000).Select(p=>p.Key).ToArray())uploads.Remove(key);
            var times = uploads.GetValueOrDefault(uploaderId) ?? new Queue<long>();
            while (times.Count > 0 && now - times.Peek() > 60000) times.Dequeue();
            if (times.Count >= 6) throw new InvalidDataException("Upload limit: six new files per minute. Retry later.");
            times.Enqueue(now); uploads[uploaderId] = times;
        }
        Capacity(2);
        if (TemporaryBytes() + bytes.Length >= 256L * 1024 * 1024) throw new InvalidDataException("Temporary storage is full. Resolve pending cleanup before uploading.");
        var asset = new AudioAsset { Name = Clean(Path.GetFileNameWithoutExtension(name), 120), Uploader = Clean(uploader, 40), UploaderId = Clean(uploaderId, 128), UploadHash = hash };
        var inputPath = Path.Combine(Incoming, journal.SessionId!, asset.Id, "source" + extension);
        asset.Input = new() { Path = inputPath, Hash = hash, Bytes = bytes.Length, State = "planned", SessionId = journal.SessionId! };
        var variant = Allocate(asset, "normal"); asset.Variants.Add(variant); journal.Assets.Add(asset); Save();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!); ManagedFiles.RequireOwnedPath(inputPath, Incoming);
            using (var file = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { await file.WriteAsync(bytes, cancel); file.Flush(true); }
            asset.Input.State = "staged"; Save();
            await Generate(asset, variant, inputPath, false, cancel);
            asset.SourceHash = variant.FileHash; asset.SourcePath = variant.Path; variant.SourceHash = asset.SourceHash; asset.Input.State = "retired";
            Save(); await TryImport(asset, variant); return asset;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            asset.State = "failed"; variant.State = "failed";
            Issue(asset, "upload-processing", "The upload could not be prepared. Its recovery record and any staged source are preserved. Retry in Temp, or remove it when playback is stopped."); Save();
            throw new InvalidDataException(ex is InvalidDataException ? ex.Message : "Audio preparation failed. See Temp for recovery and retry options.");
        }
    }
    public async Task<(AudioAsset Asset, AudioVariant Variant)> NativeSource(string soundId, string path, string name, string creator, CancellationToken cancel = default)
    {
        RequireWritable();
        var owned = journal.Assets.SelectMany(a => a.Variants.Select(v => (Asset: a, Variant: v))).FirstOrDefault(x => x.Variant.Native?.SoundId == soundId);
        if (owned.Asset != null) return owned;
        var catalog = await Native(() => ManagedSoundpad.Read(pipe));
        if (!catalog.Entries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The source is no longer in Soundpad.");
        ManagedFiles.LocalSource(path);
        var asset = journal.Assets.FirstOrDefault(a => a.Origin == "native" && a.SourceSoundId == null && a.SourcePath == path);
        if (asset == null)
        {
            Capacity(0);
            asset = new() { Origin = "native", Name = Clean(name, 120), Uploader = Clean(creator, 40), SourceSoundId = soundId, SourcePath = path, WorkspaceKey = soundId };
            journal.Assets.Add(asset);
        }
        asset.SourceSoundId = soundId; asset.WorkspaceKey ??= soundId; asset.Name = Clean(name, 120);
        var hash = await Task.Run(() => ManagedFiles.Hash(path), cancel); asset.SourceHash = hash;
        var normal = asset.Variants.FirstOrDefault(v => v.Current && v.Orientation == "normal" && !v.OwnsFile);
        if (normal == null) { normal = new() { Orientation = "normal", Storage = "external", OwnsFile = false }; asset.Variants.Add(normal); }
        normal.Path = path; normal.FileHash = normal.SourceHash = hash; normal.Bytes = new FileInfo(path).Length; normal.State = "ready";
        normal.Native = new() { Path = path, SoundId = soundId, VerifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        asset.State = "native"; Save(); return (asset, normal);
    }
    public (AudioAsset Asset, AudioVariant Variant)? Resolve(string soundId)
    {
        foreach (var asset in journal.Assets) foreach (var variant in asset.Variants) if (variant.Native?.SoundId == soundId) return (asset, variant);
        return null;
    }
    public async Task<(AudioAsset Asset, AudioVariant Variant)> Prepare(string assetId, string orientation, bool allowGeneration, CancellationToken cancel = default)
    {
        RequireWritable(); if (orientation is not ("normal" or "reversed")) throw new InvalidDataException("Invalid audio orientation.");
        var asset = Asset(assetId);
        if (asset.Origin == "native") await RefreshNativeSource(asset, cancel);
        var existing = asset.Variants.LastOrDefault(v => v.Current && v.Orientation == orientation && v.State == "ready" && v.SourceHash == asset.SourceHash);
        if (existing != null && ManagedFiles.Verified(existing.Path, existing.FileHash, existing.Bytes))
        {
            if (existing.Retired && !existing.Permanent && !existing.LegacyApproval)
            {
                if ((asset.Origin == "upload" && !asset.Variants.Any(v=>v.Permanent)) || !journal.SessionOpen) throw new InvalidDataException("This temporary upload belongs to an ended session.");
                existing.Retired = false; existing.LeaseSessionId = journal.SessionId; Save();
            }
            await EnsureImported(asset, existing); return (asset, existing);
        }
        if (!allowGeneration) throw new InvalidDataException("The host has disabled guest audio generation.");
        if (!journal.SessionOpen) throw new InvalidDataException("Hosting has ended.");
        var source = asset.Variants.LastOrDefault(v => v.Current && v.Orientation != orientation && v.State == "ready" && (!v.Retired || v.Permanent || v.LegacyApproval) && v.SourceHash == asset.SourceHash && ManagedFiles.Verified(v.Path, v.FileHash, v.Bytes));
        if (source == null) throw new InvalidDataException("No verified source orientation is available. The original files have been preserved.");
        var now = Environment.TickCount64;
        if (transformations.TryGetValue(asset.Id, out var last) && now - last < 1000) throw new InvalidDataException("Audio is still preparing. Retry in a moment.");
        transformations[asset.Id] = now; Capacity();
        foreach (var old in asset.Variants.Where(v => v.Current && v.Orientation == orientation)) { old.Current = false; if (old.OwnsFile && !old.Permanent && !old.LegacyApproval) old.Retired = true; }
        var variant = Allocate(asset, orientation); asset.Variants.Add(variant); asset.State = "processing"; Save();
        try
        {
            await Generate(asset, variant, source.Path, true, cancel);
            if (!ManagedFiles.Verified(source.Path, source.FileHash, source.Bytes)) throw new InvalidDataException("The source changed during reversal. The previous files were preserved.");
            variant.SourceHash = asset.SourceHash; await EnsureImported(asset, variant); asset.State = "temporary"; Save(); return (asset, variant);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            variant.State = variant.FileHash.Length == 64 ? "ready" : "failed"; asset.State = "failed";
            Issue(asset, "transform", "This variant could not finish. Verified source audio is preserved; retry preparation or inspect its recovery state."); Save();
            throw new InvalidDataException(ex is InvalidDataException ? ex.Message : "Variant preparation failed. See Temp for recovery options.");
        }
    }
    private AudioVariant Allocate(AudioAsset asset, string orientation)
    {
        if (asset.Variants.Count >= 64) throw new InvalidDataException("This source reached its variant-history limit. Resolve old temporary recovery items before generating more.");
        var variant = new AudioVariant { Orientation = orientation, OwnsFile = true, Storage = "session", SessionId = journal.SessionId, LeaseSessionId = journal.SessionId, SourceHash = asset.SourceHash };
        variant.Path = Path.Combine(Sessions, journal.SessionId ?? "recovery", asset.Id, ManagedFiles.Name(asset.Name) + "-" + orientation + "-" + variant.Id[..8] + ".wav");
        ManagedFiles.RequireOwnedPath(variant.Path, Sessions); return variant;
    }
    private async Task Generate(AudioAsset asset, AudioVariant variant, string input, bool reverse, CancellationToken cancel)
    {
        long remaining = 256L * 1024 * 1024 - TemporaryBytes();
        await Task.Run(() => ManagedAudioCodec.Convert(input, variant.Path, reverse, remaining, cancel), cancel);
        variant.Bytes = new FileInfo(variant.Path).Length; variant.FileHash = ManagedFiles.Hash(variant.Path); variant.State = "ready";
        asset.ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Save();
    }
    private async Task RefreshNativeSource(AudioAsset asset, CancellationToken cancel)
    {
        var normal = asset.Variants.LastOrDefault(v => v.Current && v.Orientation == "normal" && !v.OwnsFile);
        if (normal == null || !File.Exists(normal.Path)) return;
        var catalog = await Native(() => ManagedSoundpad.Read(pipe));
        if (!catalog.Entries.Any(e => e.Path.Equals(normal.Path, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The original native source was removed. It will not be read by managed audio.");
        ManagedFiles.LocalSource(normal.Path); var hash = await Task.Run(() => ManagedFiles.Hash(normal.Path), cancel);
        if (hash == asset.SourceHash) return;
        asset.SourceHash = hash; normal.SourceHash = normal.FileHash = hash; normal.Bytes = new FileInfo(normal.Path).Length; normal.State = "ready"; asset.SourceSoundId ??= nativeId(normal.Path); asset.WorkspaceKey ??= asset.SourceSoundId; normal.Native ??= Reference(normal.Path); normal.Native.VerifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var variant in asset.Variants.Where(v => v.OwnsFile && v.Current && v.SourceHash != hash)) { variant.Current = false; if (!variant.Permanent && !variant.LegacyApproval) variant.Retired = true; }
        asset.ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Issue(asset, "source-updated", "The source content changed. Stale generated variants were retired; accepted permanent versions are preserved.", "info"); Save();
    }
    private async Task<bool> TryImport(AudioAsset asset, AudioVariant variant)
    {
        try { await EnsureImported(asset, variant); ClearIssues(asset, "native-import", "upload-processing"); asset.State = variant.LegacyApproval ? "accepted-needs-migration" : variant.Permanent ? "permanent" : "temporary"; Save(); return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { asset.State = "waiting-soundpad"; Issue(asset, "native-import", "Audio is prepared, but its native entry is not verified. Open Soundpad and retry in Temp; the source file has been preserved."); Save(); return false; }
    }
    private async Task EnsureImported(AudioAsset asset, AudioVariant variant)
    {
        if (!ManagedFiles.Verified(variant.Path, variant.FileHash, variant.Bytes)) throw new InvalidDataException("The managed file is missing or was modified. Its source and recovery information are preserved.");
        await Native(async () =>
        {
            var catalog = await ManagedSoundpad.Read(pipe); var matches = catalog.Entries.Where(e => e.Path.Equals(variant.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("Multiple entries reference this file. Resolve duplicate native entries before retrying.");
            if (matches.Length == 1)
            {
                variant.Native ??= Reference(variant.Path); variant.Native.ObservedIndex = matches[0].Index; variant.Native.VerifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); variant.Native.LibraryFingerprint = catalog.Fingerprint;
            }
            else
            {
                if (!variant.OwnsFile) throw new InvalidDataException("The original native entry is unavailable.");
                if (variant.Permanent) throw new InvalidDataException("The accepted native entry was removed. Re-add the permanent file in Soundpad; the app will not silently create duplicates.");
                journal.TempCategory ??= new() { Name = "Temp · Soundpad Web [" + journal.AppId[..6] + "-" + Guid.NewGuid().ToString("N")[..6] + "]" };
                Save(); int category = await ManagedSoundpad.EnsureCategory(pipe, journal.TempCategory, Save);
                variant.Native = await ManagedSoundpad.Import(pipe, variant.Path, category, journal.TempCategory.Name); variant.Native.SoundId = nativeId(variant.Path);
            }
            if (variant.Orientation == "normal") asset.WorkspaceKey ??= asset.SourceSoundId ?? variant.Native.SoundId;
            else variant.WorkspaceKey ??= asset.Variants.FirstOrDefault(v => v.Orientation == "reversed" && v.WorkspaceKey != null)?.WorkspaceKey ?? variant.Native.SoundId;
            Save(); return true;
        });
    }
}
