using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

// One install's backups on disk (2.44). See docs/BACKUPS.md.
//   <folder>\Soundpad Web Backups\<appId[..6]>-<hostId[..8]>\
//     blobs\<aa>\<sha256>.<ext>                 audio, icons: content-addressed, written once, shared by snapshots
//     snapshots\<yyyyMMddTHHmmssZ>-<id8>\       one backup: data\<the data folder's files>, manifest.v1.json last
//     snapshots\.partial-<id8>\                 a backup being written (renamed when its manifest is on disk)
// Nothing outside these names is ever deleted, and links are never followed.

/// <summary>A small file of the data folder copied whole into the snapshot (state, library, sentences, journal, settings).</summary>
public sealed class BackupDoc
{
    public string Role { get; set; } = "";
    /// <summary>Its path relative to the data folder, with backslashes ("library\voice-library.v1.json").</summary>
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Bytes { get; set; }
}

/// <summary>A file the snapshot refers to in the blob store: a kept clip, a recording's file, a sentence version, an icon.</summary>
public sealed class BackupBlobRef
{
    /// <summary>"clip" (kept audio), "recording" (a voice-library recording's file), "sentence" (a kept version), "icon".</summary>
    public string Role { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Ext { get; set; } = "";
    public long Bytes { get; set; }
    /// <summary>Where it came from: a full path (clips, recordings), "&lt;sentence id&gt;\&lt;file&gt;" (sentences) or the icon's file name.</summary>
    public string Original { get; set; } = "";
    public string? AssetId { get; set; }
    public string? VariantId { get; set; }
    public string? RecordingId { get; set; }
    public string? SentenceId { get; set; }
    public int? Version { get; set; }
    /// <summary>A Soundpad file (not one the app made): its size and modified time when copied, so an unchanged one is not copied again.</summary>
    public bool Native { get; set; }
    public long? Modified { get; set; }
}

public sealed class BackupManifest
{
    public const string FileName = "manifest.v1.json";
    public static readonly int[] Reader = [2, 44];
    public int Format { get; set; } = 1;
    public int[] MinReader { get; set; } = [.. Reader];
    public string Id { get; set; } = "";
    /// <summary>"scheduled", "manual", "pre-restore", "pre-update" or "pre-organize".</summary>
    public string Kind { get; set; } = "manual";
    public long CreatedUtcMs { get; set; }
    public string CompanionVersion { get; set; } = "";
    public string AppId { get; set; } = "";
    public string HostId { get; set; } = "";
    /// <summary>The library, sentences or kept audio changed while the backup was made, three times in a row: their files may be from slightly different moments.</summary>
    public bool Fuzzy { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<BackupDoc> Files { get; set; } = [];
    public List<BackupBlobRef> Blobs { get; set; } = [];
    /// <summary>Everything the snapshot refers to, and what this run copied.</summary>
    public long Bytes { get; set; }
    public long NewBytes { get; set; }
    public static readonly string[] Kinds = ["scheduled", "manual", "pre-restore", "pre-update", "pre-organize"];
    public static readonly string[] DocRoles = ["state", "library", "words", "sentences", "journal", "keep", "mimic", "ui", "workspace"];
    public static readonly string[] BlobRoles = ["clip", "recording", "sentence", "icon"];
    /// <summary>The data folder's files a snapshot may hold, by role.</summary>
    public static readonly Dictionary<string, string> DocPaths = new()
    {
        ["state"] = "state.json", ["library"] = @"library\voice-library.v1.json", ["words"] = @"library\voice-words.v1.jsonl", ["sentences"] = @"library\sentences.v1.json",
        ["journal"] = @"managed-audio\journal.json", ["keep"] = "keep-settings.json", ["mimic"] = "mimic-settings.json", ["ui"] = "ui.json",
        ["workspace"] = "backup-workspace.v1.json",
    };

    private static readonly Regex Hash = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    public static readonly Regex SnapshotName = new(@"^\d{8}T\d{6}Z-[a-f0-9]{8}$", RegexOptions.CultureInvariant);
    public static readonly Regex PartialName = new("^\\.partial-[a-f0-9]{8}$", RegexOptions.CultureInvariant);
    public static readonly Regex BlobName = new("^[a-f0-9]{64}\\.(wav|png|jsonl|json|mp3|ogg|flac|m4a|aac|wma|opus)$", RegexOptions.CultureInvariant);
    public static readonly string[] Exts = ["wav", "png", "jsonl", "json", "mp3", "ogg", "flac", "m4a", "aac", "wma", "opus"];
    public static bool ValidHash(string? h) => h != null && Hash.IsMatch(h);

    /// <summary>True when this companion can read a manifest asking for <paramref name="min"/>.</summary>
    public static bool Readable(int[] min) => min.Length == 2 && (min[0] < Reader[0] || min[0] == Reader[0] && min[1] <= Reader[1]);

    /// <summary>Strict: anything out of place refuses the snapshot (it is left as it is).</summary>
    public static void Validate(BackupManifest m, string folderName)
    {
        void Require(bool ok, string what) { if (!ok) throw new InvalidDataException(what); }
        Require(m.Format == 1, "unknown manifest format");
        Require(m.MinReader is { Length: 2 }, "bad manifest");
        Require(Readable(m.MinReader), "it was made by a newer Soundpad Web; update the companion to read it");
        Require(m.Id == folderName && SnapshotName.IsMatch(m.Id), "the manifest does not belong to its folder");
        Require(Kinds.Contains(m.Kind) && m.CreatedUtcMs > 0 && m.CompanionVersion is { Length: <= 40 } && m.AppId is { Length: <= 64 } && m.HostId is { Length: <= 64 }, "bad manifest");
        Require(m.Warnings != null && m.Warnings.Count <= 500 && m.Warnings.All(w => w is { Length: <= 600 }), "bad warnings");
        Require(m.Files != null && m.Files.Count <= DocPaths.Count && m.Blobs != null && m.Blobs.Count <= 200000, "bad manifest");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in m.Files!)
            Require(d != null && DocRoles.Contains(d.Role) && roles.Add(d.Role) && d.Path == DocPaths[d.Role] && ValidHash(d.Sha256) && d.Bytes is >= 0 and <= 64L * 1024 * 1024, "bad file entry");
        foreach (var b in m.Blobs!)
        {
            Require(b != null && BlobRoles.Contains(b.Role) && ValidHash(b.Sha256) && Exts.Contains(b.Ext) && b.Bytes is >= 0 and <= 536870912, "bad blob entry");
            Require(b!.Original is { Length: > 0 and <= 1024 } && !b.Original.Any(char.IsControl), "bad blob entry");
            Require(b.AssetId is null or { Length: <= 64 } && b.VariantId is null or { Length: <= 64 } && b.RecordingId is null or { Length: <= 64 } && b.SentenceId is null or { Length: <= 64 } && b.Version is null or (>= 1 and <= 999), "bad blob entry");
        }
        Require(m.Bytes >= 0 && m.NewBytes >= 0, "bad manifest");
    }
}

/// <summary>
/// The file operations a backup uses, so tests can fail them on purpose (a crash in the middle of a copy or of the
/// manifest) or report little free space. The default is the real file system.
/// </summary>
public class BackupFiles
{
    public virtual Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
    public virtual Stream Create(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
    public virtual void Move(string from, string to) => File.Move(from, to, false);
    public virtual void MoveDirectory(string from, string to) => Directory.Move(from, to);
    public virtual void Delete(string path) => File.Delete(path);
    public virtual long FreeBytes(string folder) { try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return -1; } }
    /// <summary>Called after each chunk of a blob is written (tests stop a copy here).</summary>
    public virtual void AfterChunk(string path, long written) { }
}

/// <summary>A file to copy into the blob store.</summary>
public sealed record BlobSource(string Role, string Path, string Ext, long Bytes, string? ExpectedSha, bool Native, string Original, string? AssetId = null, string? VariantId = null, string? RecordingId = null, string? SentenceId = null, int? Version = null);

/// <summary>One install's backup folder: its blobs and snapshots. Only one run uses it at a time.</summary>
public sealed class BackupRepository(string root, BackupFiles files)
{
    public const int Chunk = 1 << 20;
    public string Root => root;
    public string Blobs => Path.Combine(root, "blobs");
    public string Snapshots => Path.Combine(root, "snapshots");
    /// <summary>Bytes this repository wrote into blobs (tests: a second snapshot copies no audio).</summary>
    public long CopiedBytes;

    public string BlobPath(string sha, string ext) => Path.Combine(Blobs, sha[..2], sha + "." + ext);
    public bool HasBlob(string sha, string ext, long bytes)
    {
        try { var f = new FileInfo(BlobPath(sha, ext)); return f.Exists && f.Length == bytes && f.LinkTarget == null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsLink(FileSystemInfo i) => i.Attributes.HasFlag(FileAttributes.ReparsePoint) || i.LinkTarget != null;

    /// <summary>The snapshot folders (valid names, not links), newest first.</summary>
    public string[] SnapshotFolders()
    {
        if (!Directory.Exists(Snapshots) || IsLink(new DirectoryInfo(Snapshots))) return [];
        return [.. new DirectoryInfo(Snapshots).EnumerateDirectories().Where(d => BackupManifest.SnapshotName.IsMatch(d.Name) && !IsLink(d)).Select(d => d.Name).OrderByDescending(n => n, StringComparer.Ordinal)];
    }

    public string SnapshotPath(string name) => Path.Combine(Snapshots, name);

    /// <summary>Reads and checks a snapshot's manifest; null with the reason when it cannot be used.</summary>
    public BackupManifest? Read(string name, out string? problem)
    {
        problem = null;
        try
        {
            if (!BackupManifest.SnapshotName.IsMatch(name)) throw new InvalidDataException("unknown backup");
            var path = Path.Combine(SnapshotPath(name), BackupManifest.FileName);
            if (!File.Exists(path)) throw new InvalidDataException("its manifest is missing");
            if (new FileInfo(path).Length > 32L * 1024 * 1024) throw new InvalidDataException("its manifest is too large");
            var m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllBytes(path), Json.Options) ?? throw new InvalidDataException("its manifest is empty");
            BackupManifest.Validate(m, name);
            return m;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            problem = ex is InvalidDataException ? ex.Message : "its manifest could not be read";
            return null;
        }
    }

    /// <summary>A doc of a snapshot, checked against its hash. Throws when it is missing or changed.</summary>
    public byte[] ReadDoc(string name, BackupDoc doc)
    {
        var path = Path.Combine(SnapshotPath(name), "data", doc.Path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != doc.Bytes || Sha(bytes) != doc.Sha256) throw new InvalidDataException($"The backup's copy of {doc.Path} is damaged.");
        return bytes;
    }

    /// <summary>Opens a blob after checking its size; the caller checks the hash while copying.</summary>
    public string RequireBlob(BackupBlobRef b)
    {
        var path = BlobPath(b.Sha256, b.Ext);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != b.Bytes || IsLink(info)) throw new InvalidDataException("The backup's copy of " + System.IO.Path.GetFileName(b.Original) + " is missing or damaged.");
        return path;
    }

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Removes snapshots that were never finished (a crash before their manifest was renamed).</summary>
    public void SweepPartials()
    {
        if (!Directory.Exists(Snapshots) || IsLink(new DirectoryInfo(Snapshots))) return;
        foreach (var d in new DirectoryInfo(Snapshots).EnumerateDirectories())
            if (BackupManifest.PartialName.IsMatch(d.Name) && !IsLink(d)) DeleteTree(d);
        if (Directory.Exists(Blobs) && !IsLink(new DirectoryInfo(Blobs)))
            foreach (var dir in new DirectoryInfo(Blobs).EnumerateDirectories())
            {
                if (IsLink(dir) || !Regex.IsMatch(dir.Name, "^[a-f0-9]{2}$")) continue;
                foreach (var f in dir.EnumerateFiles(".tmp-*")) if (Regex.IsMatch(f.Name, "^\\.tmp-[a-f0-9]{32}$") && !IsLink(f)) Try(() => files.Delete(f.FullName));
            }
    }

    /// <summary>Deletes a folder of the app's own (a snapshot): its files and folders, never through a link.</summary>
    private void DeleteTree(DirectoryInfo dir)
    {
        if (IsLink(dir)) return;
        foreach (var sub in dir.EnumerateDirectories()) { if (IsLink(sub)) Try(() => Directory.Delete(sub.FullName, false)); else DeleteTree(sub); }
        foreach (var f in dir.EnumerateFiles()) Try(() => { if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly; files.Delete(f.FullName); });
        Try(() => Directory.Delete(dir.FullName, false));
    }
    private static void Try(Action a) { try { a(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    /// <summary>
    /// Copies a file into the blob store, hashing it as it goes, in 1 MiB chunks; <paramref name="pause"/> runs after
    /// each chunk (it waits a moment while Soundpad plays). A file whose hash is known and already stored is not copied.
    /// Returns the hash and size, or null with a warning when the file is missing, changed or does not match its record.
    /// </summary>
    public (string Sha, long Bytes)? Store(BlobSource s, Func<string, long, string?>? knownNative, Action pause, CancellationToken cancel, List<string> warnings)
    {
        string name = System.IO.Path.GetFileName(s.Path);
        if (s.ExpectedSha != null && HasBlob(s.ExpectedSha, s.Ext, s.Bytes)) return (s.ExpectedSha, s.Bytes);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            FileInfo before;
            try { before = new FileInfo(s.Path); if (!before.Exists) { warnings.Add($"{name} is missing; it is not in this backup."); return null; } if (IsLink(before)) { warnings.Add($"{name} is a link; it is not in this backup."); return null; } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { warnings.Add($"{name} could not be read; it is not in this backup."); return null; }
            long modified = before.LastWriteTimeUtc.Ticks, length = before.Length;
            // A Soundpad file or an icon (neither has a recorded hash): as the newest snapshot copied it, when unchanged.
            if ((s.Native || s.Role == "icon") && knownNative?.Invoke(s.Original, length) is { } cached && cached.Split('|') is [var knownSha, var ticks] && ticks == modified.ToString(CultureInfo.InvariantCulture) && HasBlob(knownSha, s.Ext, length)) return (knownSha, length);
            var temporary = "";
            try
            {
                Directory.CreateDirectory(Blobs);
                if (IsLink(new DirectoryInfo(Blobs))) throw new InvalidDataException("The backup folder's blobs folder is a link.");
                string hash;
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    // The final name is known only at the end; a temporary name in a random fan-out folder until then.
                    var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(1)).ToLowerInvariant();
                    Directory.CreateDirectory(Path.Combine(Blobs, prefix));
                    temporary = Path.Combine(Blobs, prefix, ".tmp-" + Guid.NewGuid().ToString("N"));
                    using (var input = files.OpenRead(s.Path))
                    using (var output = files.Create(temporary))
                    {
                        var buffer = new byte[Chunk]; long written = 0; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            sha.AppendData(buffer, 0, read); output.Write(buffer, 0, read); written += read;
                            Interlocked.Add(ref CopiedBytes, read);
                            files.AfterChunk(temporary, written);
                            if (written > 536870912) throw new InvalidDataException("too large");
                            pause();
                        }
                        output.Flush();
                        if (output is FileStream fs) fs.Flush(true);
                    }
                    hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }
                var after = new FileInfo(s.Path);
                bool changed = !after.Exists || after.Length != length || after.LastWriteTimeUtc.Ticks != modified || new FileInfo(temporary).Length != length;
                if (s.ExpectedSha != null && hash != s.ExpectedSha)
                {
                    Try(() => files.Delete(temporary));
                    warnings.Add($"{name} does not match the file that was kept; it is not in this backup.");
                    return null;
                }
                if (changed)
                {
                    Try(() => files.Delete(temporary));
                    if (attempt == 0) continue;
                    warnings.Add($"{name} kept changing while it was copied; it is not in this backup.");
                    return null;
                }
                var final = BlobPath(hash, s.Ext);
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                if (File.Exists(final)) Try(() => files.Delete(temporary));
                else files.Move(temporary, final);
                return (hash, length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (temporary.Length > 0) Try(() => files.Delete(temporary));
                if (ex is IOException && files.FreeBytes(root) is >= 0 and < 16L * 1024 * 1024) throw new InvalidDataException("The backup folder's drive is full.");
                if (attempt == 0) continue;
                warnings.Add($"{name} could not be copied ({ex.GetType().Name}); it is not in this backup.");
                return null;
            }
            catch { if (temporary.Length > 0) Try(() => files.Delete(temporary)); throw; }
        }
        return null;
    }

    /// <summary>
    /// Writes a snapshot: its docs into a partial folder, the manifest last (flushed), then the folder takes its final
    /// name. A crash before the rename leaves a partial folder that the next run removes; earlier snapshots are untouched.
    /// </summary>
    public string Write(BackupManifest manifest, IReadOnlyList<(BackupDoc Doc, byte[] Bytes)> docs)
    {
        Directory.CreateDirectory(Snapshots);
        if (IsLink(new DirectoryInfo(Snapshots))) throw new InvalidDataException("The backup folder's snapshots folder is a link.");
        var id8 = manifest.Id[^8..];
        var partial = Path.Combine(Snapshots, ".partial-" + id8);
        if (Directory.Exists(partial)) DeleteTree(new DirectoryInfo(partial));
        Directory.CreateDirectory(Path.Combine(partial, "data"));
        foreach (var (doc, bytes) in docs)
        {
            var path = Path.Combine(partial, "data", doc.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = files.Create(path);
            stream.Write(bytes); stream.Flush();
            if (stream is FileStream fs) fs.Flush(true);
        }
        var manifestPath = Path.Combine(partial, BackupManifest.FileName);
        using (var stream = files.Create(manifestPath))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, Json.Options)); stream.Flush();
            if (stream is FileStream fs) fs.Flush(true);
        }
        var final = SnapshotPath(manifest.Id);
        files.MoveDirectory(partial, final);
        return final;
    }

    /// <summary>
    /// Which snapshots retention keeps: the newest <c>keepCount</c>, everything newer than <c>keepDays</c>, the newest of
    /// every ISO week, and every "before restore" backup for at least 7 days.
    /// </summary>
    public static HashSet<string> Keep(IReadOnlyList<BackupManifest> valid, BackupSettings settings, DateTimeOffset now)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var newest = valid.OrderByDescending(m => m.CreatedUtcMs).ToList();
        foreach (var m in newest.Take(settings.KeepCount)) keep.Add(m.Id);
        long days = now.ToUnixTimeMilliseconds() - settings.KeepDays * 86400000L, week = now.ToUnixTimeMilliseconds() - 7 * 86400000L;
        foreach (var m in newest)
        {
            if (m.CreatedUtcMs >= days) keep.Add(m.Id);
            if (m.Kind == "pre-restore" && m.CreatedUtcMs >= week) keep.Add(m.Id);
        }
        foreach (var g in newest.GroupBy(m => { var d = DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedUtcMs).UtcDateTime; return (ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d)); }))
            keep.Add(g.First().Id);
        return keep;
    }

    /// <summary>
    /// Retention, then mark and sweep: snapshots retention drops are removed, then blobs no remaining manifest refers
    /// to. When any snapshot folder's manifest cannot be read (damaged, or written by a newer companion), no blob is
    /// removed at all, since that snapshot may need them. Returns how many snapshots remain and their size.
    /// </summary>
    /// <param name="protect">Snapshots that stay whatever retention says (a restore is reading them).</param>
    public (int Snapshots, long Bytes, string? Warning) Retain(BackupSettings settings, DateTimeOffset now, IReadOnlySet<string>? protect = null)
    {
        var valid = new List<BackupManifest>(); bool unreadable = false;
        foreach (var name in SnapshotFolders())
        {
            if (Read(name, out _) is { } m) valid.Add(m); else unreadable = true;
        }
        var keep = Keep(valid, settings, now);
        if (protect != null) keep.UnionWith(protect);
        foreach (var m in valid.Where(m => !keep.Contains(m.Id)).ToList())
        {
            // Renamed to a partial name first, so a crash part-way leaves a folder the next run sweeps, never a
            // snapshot without its manifest (which would stop blobs from ever being removed again). When the rename
            // fails (a file is open), the snapshot stays whole and is tried again next time.
            var partial = Path.Combine(Snapshots, ".partial-" + m.Id[^8..]);
            try
            {
                if (Directory.Exists(partial)) DeleteTree(new DirectoryInfo(partial));
                if (Directory.Exists(partial)) continue;
                files.MoveDirectory(SnapshotPath(m.Id), partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            valid.Remove(m);
            DeleteTree(new DirectoryInfo(partial));
        }
        string? warning = null;
        var referenced = valid.SelectMany(m => m.Blobs).Select(b => b.Sha256 + "." + b.Ext).ToHashSet(StringComparer.Ordinal);
        if (unreadable) warning = "A backup in the folder could not be read, so no old files were removed.";
        else if (Directory.Exists(Blobs) && !IsLink(new DirectoryInfo(Blobs)))
            foreach (var dir in new DirectoryInfo(Blobs).EnumerateDirectories())
            {
                if (IsLink(dir) || !Regex.IsMatch(dir.Name, "^[a-f0-9]{2}$")) continue;
                foreach (var f in dir.EnumerateFiles())
                    if (BackupManifest.BlobName.IsMatch(f.Name) && f.Name.StartsWith(dir.Name, StringComparison.Ordinal) && !IsLink(f) && !referenced.Contains(f.Name)) Try(() => files.Delete(f.FullName));
            }
        long bytes = valid.Sum(m => m.Files.Sum(d => d.Bytes)) + valid.SelectMany(m => m.Blobs).DistinctBy(b => b.Sha256 + b.Ext).Sum(b => b.Bytes);
        return (valid.Count, bytes, warning);
    }
}
