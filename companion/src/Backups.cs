using System.Collections.Concurrent;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

// Automatic local backups (2.44, gate backups). See docs/BACKUPS.md and reports/OVERHAUL-PLAN.md, "Data lifecycle,
// updates and setup". A run captures the data folder's small files under their own locks (sentences, library, kept-audio
// journal, state, in that order), then copies the audio and icons they refer to outside every lock, on one
// below-normal-priority thread, into a content-addressed blob store; the snapshot's manifest is written last. A run that
// saw the library, sentences or journal change meanwhile captures again (up to three times), then marks itself fuzzy.

/// <summary>
/// The backup thread (the <see cref="AudioWorker"/> pattern): one below-normal thread, so copying never competes with
/// playback, the poll loop or the socket. Its callers continue on the thread pool.
/// </summary>
public sealed class BackupWorker : IDisposable
{
    private readonly BlockingCollection<Action> queue = new(16);
    private readonly object startGate = new();
    private bool started;

    public Task<T> Run<T>(Func<T> work, CancellationToken cancel = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (startGate)
        {
            if (!started) { new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Soundpad Web backups" }.Start(); started = true; }
        }
        try
        {
            if (!queue.TryAdd(() =>
            {
                if (cancel.IsCancellationRequested) { done.TrySetCanceled(cancel); return; }
                try { done.TrySetResult(work()); }
                catch (OperationCanceledException ex) { done.TrySetCanceled(ex.CancellationToken); }
                catch (Exception ex) { done.TrySetException(ex); }
            })) return Task.FromException<T>(new InvalidDataException("The backup worker is busy."));
        }
        catch (InvalidOperationException) { return Task.FromException<T>(new ObjectDisposedException(nameof(BackupWorker))); }
        return done.Task;
    }
    public Task Run(Action work, CancellationToken cancel = default) => Run(() => { work(); return true; }, cancel);
    private void Work()
    {
        try { foreach (var job in queue.GetConsumingEnumerable()) job(); }
        catch (ObjectDisposedException) { }
    }
    public void Dispose() { try { queue.CompleteAdding(); } catch (ObjectDisposedException) { } }
}

/// <summary>How a run ended.</summary>
public sealed record BackupRunResult(bool Ok, string? Id, string? Path, long Bytes, long NewBytes, int Snapshots, bool Fuzzy, string[] Warnings, string Message);

public sealed partial class Bridge
{
    private readonly BackupWorker backupWorker = new();
    private readonly SemaphoreSlim backupRun = new(1, 1);
    private readonly CancellationTokenSource backupStop = new();
    private BackupSettingsStore? backupSettingsStore;
    private readonly object backupInit = new();
    private BackupSettingsStore BackupStore { get { lock (backupInit) return backupSettingsStore ??= new(Path.Combine(store.DirectoryPath, "backup-settings.v1.json")); } }
    private BackupFiles backupFiles = new();
    private Func<DateTimeOffset> backupClock = () => DateTimeOffset.UtcNow;
    /// <summary>Soundpad's settings folder for the backup folder rules and restores (never written): the one tests set with UseLeppsoftFolder (BridgeOrganize.cs), otherwise %APPDATA%\Leppsoft.</summary>
    private string leppsoftFolder => leppsoftOverride ?? BackupFolders.Leppsoft();
    private string suggestedBackupFolder = BackupFolders.Suggested();
    private long backupsRevision;
    private volatile bool backupRunning;
    private int backupsStarted;
    /// <summary>The data revisions the last finished backup captured, the revisions seen last and when they changed.</summary>
    private (long Library, long Sentences, long Journal)? backedUpRevisions, seenRevisions;
    private long changedAtMs;
    /// <summary>Test seams.</summary>
    internal void UseBackupFiles(BackupFiles files) => backupFiles = files;
    internal void UseBackupClock(Func<DateTimeOffset> clock) => backupClock = clock;
    internal void UseSuggestedBackupFolder(string folder) => suggestedBackupFolder = folder;
    /// <summary>Tests and the harness: awaited at the start of every run, after it was marked running (a slow backup).</summary>
    internal Func<CancellationToken, Task>? BackupHold { get; set; }
    /// <summary>The first scheduled run waits this long after the companion starts (launch stays light).</summary>
    internal int BackupStartDelayMs { get; set; } = 60000;
    /// <summary>The repository the last run used (tests read its copy counter).</summary>
    internal BackupRepository? LastBackupRepository { get; private set; }

    private long BackupsRevision => Interlocked.Read(ref backupsRevision);
    private void BackupsChanged() { Interlocked.Increment(ref backupsRevision); Publish(); }

    /// <summary>The snapshot's hostId (the identity's workspace key), without building a whole snapshot.</summary>
    private string HostIdNow => Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Convert.FromBase64String(disk.IdentityKey), "workspace-host-v2"u8)).ToLowerInvariant();
    private string? KeepRoot { get { var d = Keep.Destination; return string.IsNullOrWhiteSpace(d) ? null : Path.Combine(d.Trim(), "Soundpad Web"); } }
    private string CheckBackupFolder(string folder, bool probe = true) => BackupFolders.Check(folder, store.DirectoryPath, leppsoftFolder, KeepRoot, probe);

    /// <summary>
    /// Every install's backup folder under the chosen folder: this install's (its app id may differ when the kept-audio
    /// journal was made after backups started) and others' (an earlier install, before Windows was reinstalled, say).
    /// Only this install's own folder is ever written to or tidied.
    /// </summary>
    private IEnumerable<BackupRepository> BackupRepositories(string folder)
    {
        var root = BackupFolders.Root(folder);
        if (!Directory.Exists(root) || BackupFolders.Linked(root)) yield break;
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories())
            if (Regex.IsMatch(d.Name, "^[a-f0-9]{6}-[a-f0-9]{8}$") && !d.Attributes.HasFlag(FileAttributes.ReparsePoint)) yield return new BackupRepository(d.FullName, backupFiles);
    }
    /// <summary>True when the repository is this install's (its folder ends with this identity's host id).</summary>
    private bool OwnRepository(BackupRepository repo) => Path.GetFileName(repo.Root).EndsWith("-" + HostIdNow[..8], StringComparison.Ordinal);
    /// <summary>Tests: called after each capture's copy, with the attempt number, before the run checks whether anything moved.</summary>
    internal Action<int>? BackupAttemptHook { get; set; }

    private (long Library, long Sentences, long Journal) DataRevisions() => (Voices.Revision, SentenceData.Revision, Audio.Public.Revision);

    private sealed record BackupCapture(List<(BackupDoc Doc, byte[] Bytes)> Docs, List<BlobSource> Blobs, (long, long, long) Revisions, string AppId, List<string> Warnings, bool IconMissing);

    private static (BackupDoc, byte[]) Doc(string role, byte[] bytes) => (new BackupDoc { Role = role, Path = BackupManifest.DocPaths[role], Sha256 = BackupRepository.Sha(bytes), Bytes = bytes.Length }, bytes);

    private static readonly Regex IconFile = new("^[A-Za-z0-9]{1,64}-([A-F0-9]{16})\\.png$", RegexOptions.CultureInvariant);

    /// <summary>The audio extension a blob keeps (null when backups do not take that kind of file).</summary>
    private static string? BlobExt(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext != "png" && ext != "json" && ext != "jsonl" && BackupManifest.Exts.Contains(ext) ? ext : null;
    }

    /// <summary>
    /// Captures what a snapshot holds, each part under its own lock and in this order: sentences, library, kept-audio
    /// journal (audio gate, background lane), state (native gate, background lane: a play goes first), small settings files.
    /// </summary>
    private async Task<BackupCapture> CaptureBackup(BackupSettings settings, CancellationToken cancel)
    {
        var docs = new List<(BackupDoc, byte[])>(); var blobs = new List<BlobSource>(); var warnings = new List<string>();
        var sentences = SentenceData.CaptureBackup();
        if (sentences is { } s) docs.Add(Doc("sentences", s.Bytes)); else warnings.Add("The sentences file could not be read, so it is not in this backup.");
        var library = Voices.CaptureBackup();
        if (library is { } l) { docs.Add(Doc("library", l.Library)); docs.Add(Doc("words", l.Words)); } else warnings.Add("The voice library could not be read, so it is not in this backup.");
        (byte[]? Bytes, long Revision, string AppId, bool OnDisk, ManagedAudioStore.KeptFile[] Kept) journal;
        await audioGate.EnterAsync(PriorityGate.Lane.Background);
        try { journal = Audio.CaptureBackup(); }
        finally { audioGate.Release(); }
        if (journal.Bytes != null) docs.Add(Doc("journal", journal.Bytes));
        StateStore.StateImage state; string[] icons;
        await gate.EnterAsync(PriorityGate.Lane.Background);
        try { state = store.Capture(disk); icons = [.. disk.IconFiles.Values]; }
        finally { gate.Release(); }
        docs.Add(Doc("state", state.Json));
        foreach (var role in new[] { "keep", "mimic", "ui", "workspace" })
        {
            var path = Path.Combine(store.DirectoryPath, BackupManifest.DocPaths[role]);
            try { if (File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024) docs.Add(Doc(role, File.ReadAllBytes(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"{Path.GetFileName(path)} could not be read, so it is not in this backup."); }
        }
        var kept = journal.Kept.ToDictionary(k => k.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var k in journal.Kept)
        {
            if (BlobExt(k.Path) is not { } ext) { warnings.Add($"{Path.GetFileName(k.Path)} is not a kind of file backups take."); continue; }
            blobs.Add(new("clip", k.Path, ext, k.Bytes, k.Hash, false, k.Path, AssetId: k.AssetId, VariantId: k.VariantId));
        }
        if (library is { } lib)
            foreach (var r in lib.Recordings)
            {
                if (kept.TryGetValue(r.Path, out var k)) { if (BlobExt(r.Path) is { } ext) blobs.Add(new("recording", r.Path, ext, k.Bytes, k.Hash, false, r.Path, AssetId: k.AssetId, RecordingId: r.Id)); continue; }
                // A Soundpad file (or a Voice Replay Studio one) the library uses.
                if (!settings.IncludeNativeFiles) continue;
                if (BlobExt(r.Path) is not { } nativeExt) { warnings.Add($"{Path.GetFileName(r.Path)} is not a kind of file backups take."); continue; }
                if (r.Path.StartsWith(@"\\", StringComparison.Ordinal)) { warnings.Add($"{Path.GetFileName(r.Path)} is on a network folder; it is not in this backup."); continue; }
                blobs.Add(new("recording", r.Path, nativeExt, r.Bytes, null, true, r.Path, RecordingId: r.Id));
            }
        if (sentences is { } sent)
            foreach (var a in sent.Audio)
            {
                string path;
                try { path = SentenceData.AudioPath(a.SentenceId, a.File); } catch (InvalidDataException) { continue; }
                blobs.Add(new("sentence", path, "wav", a.Bytes, a.Hash, false, a.SentenceId + "\\" + a.File, SentenceId: a.SentenceId, Version: a.Version));
            }
        bool iconMissing = false;
        foreach (var icon in icons.Distinct(StringComparer.Ordinal))
        {
            if (!IconFile.IsMatch(icon)) continue;
            var path = Path.Combine(store.DirectoryPath, "icons", icon);
            if (!File.Exists(path)) { iconMissing = true; continue; }
            blobs.Add(new("icon", path, "png", new FileInfo(path).Length, null, false, icon));
        }
        cancel.ThrowIfCancellationRequested();
        var appId = journal.OnDisk && journal.AppId.Length >= 6 ? journal.AppId[..6] : HostIdNow[8..14];
        return new([.. docs], blobs, (library?.Revision ?? Voices.Revision, sentences?.Revision ?? SentenceData.Revision, journal.Revision), appId, warnings, iconMissing);
    }

    /// <summary>A recording's Soundpad file as the newest snapshot copied it: "sha|modified ticks" when size matches.</summary>
    private static Func<string, long, string?> NativeCache(BackupRepository repo)
    {
        Dictionary<string, BackupBlobRef>? known = null;
        return (path, length) =>
        {
            if (known == null)
            {
                known = new(StringComparer.OrdinalIgnoreCase);
                foreach (var name in repo.SnapshotFolders().Take(3))
                    if (repo.Read(name, out _) is { } m)
                        foreach (var b in m.Blobs.Where(b => b.Modified != null)) known.TryAdd(b.Original, b);
            }
            return known.TryGetValue(path, out var hit) && hit.Bytes == length ? hit.Sha256 + "|" + hit.Modified!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        };
    }

    /// <summary>
    /// One backup run: check the folder and free space, capture, copy, check nothing moved (else again, up to three
    /// times), write the snapshot, apply retention. One run at a time; <paramref name="wait"/> false refuses while
    /// another runs. Never during shutdown.
    /// </summary>
    internal async Task<BackupRunResult> RunBackup(string kind, bool wait = true, CancellationToken cancel = default)
    {
        if (!BackupManifest.Kinds.Contains(kind)) throw new ArgumentException("Unknown backup kind.", nameof(kind));
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel, backupStop.Token))
            if (wait) await backupRun.WaitAsync(waiting.Token);
            else if (!await backupRun.WaitAsync(0, cancel)) return new(false, null, null, 0, 0, 0, false, [], "A backup is already running.");
        backupRunning = true; BackupsChanged();
        return await RunAcquired(kind, cancel);
    }

    /// <summary>The run itself, once <see cref="backupRun"/> is held; it lets go of it at the end.</summary>
    private async Task<BackupRunResult> RunAcquired(string kind, CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, backupStop.Token);
        var token = linked.Token;
        var settings = BackupStore.Current;
        try
        {
            if (BackupStore.Problem is { } problem) throw new InvalidDataException(problem);
            if (settings.Folder is not { } chosen) throw new InvalidDataException("Backups are not set up.");
            var folder = CheckBackupFolder(chosen);
            if (BackupHold is { } hold) await hold(token);
            BackupCapture capture; BackupRepository repo; bool fuzzy = false; long copied = 0;
            var refs = new List<BackupBlobRef>(); var warnings = new List<string>();
            for (int attempt = 0; ; attempt++)
            {
                capture = await CaptureBackup(settings, token);
                repo = new BackupRepository(Path.Combine(BackupFolders.Root(folder), capture.AppId + "-" + HostIdNow[..8]), backupFiles);
                LastBackupRepository = repo;
                if (attempt == 0) await backupWorker.Run(repo.SweepPartials, token);
                // Room for what is not stored yet, plus 10%.
                var current = capture;
                long estimate = await backupWorker.Run(() => current.Docs.Sum(d => (long)d.Bytes.Length) + current.Blobs.Where(b => b.ExpectedSha == null || !repo.HasBlob(b.ExpectedSha, b.Ext, b.Bytes)).DistinctBy(b => b.ExpectedSha ?? b.Path).Sum(b => Math.Max(b.Bytes, 0)), token);
                long free = backupFiles.FreeBytes(folder);
                if (free >= 0 && free < estimate + estimate / 10) throw new InvalidDataException($"Not enough free space for the backup: it needs about {Size(estimate + estimate / 10)} and {Size(free)} is free.");
                var runWarnings = new List<string>(capture.Warnings);
                var stored = await backupWorker.Run(() =>
                {
                    var cache = NativeCache(repo); var list = new List<BackupBlobRef>();
                    var done = new Dictionary<string, (string, long)?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var b in current.Blobs)
                    {
                        token.ThrowIfCancellationRequested();
                        var key = b.Path + "|" + b.ExpectedSha;
                        if (!done.TryGetValue(key, out var result)) done[key] = result = repo.Store(b, cache, PauseWhilePlaying, token, runWarnings);
                        if (result is not var (sha, bytes)) continue;
                        list.Add(new BackupBlobRef
                        {
                            Role = b.Role, Sha256 = sha, Ext = b.Ext, Bytes = bytes, Original = b.Original, AssetId = b.AssetId, VariantId = b.VariantId, RecordingId = b.RecordingId, SentenceId = b.SentenceId, Version = b.Version,
                            Native = b.Native, Modified = b.Native || b.Role == "icon" ? SafeModified(b.Path) : null,
                        });
                    }
                    return list;
                }, token);
                refs = stored; warnings = runWarnings; copied += Interlocked.Read(ref repo.CopiedBytes);
                BackupAttemptHook?.Invoke(attempt);
                bool moved = DataRevisions() != capture.Revisions;
                if (!moved && !capture.IconMissing) break;
                if (attempt == 3) { fuzzy = true; break; }
            }
            if (capture.IconMissing) warnings.Add("An icon was being replaced while the backup was made; it may be missing from this backup.");
            var now = backupClock();
            var id = $"{now.UtcDateTime:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..25];
            var manifest = new BackupManifest
            {
                Id = id, Kind = kind, CreatedUtcMs = now.ToUnixTimeMilliseconds(), CompanionVersion = typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "", AppId = capture.AppId, HostId = HostIdNow,
                Fuzzy = fuzzy, Warnings = [.. warnings.Distinct().Take(500)], Files = [.. capture.Docs.Select(d => d.Doc)], Blobs = refs,
                Bytes = capture.Docs.Sum(d => (long)d.Bytes.Length) + refs.DistinctBy(r => r.Sha256 + r.Ext).Sum(r => r.Bytes), NewBytes = copied,
            };
            var path = await backupWorker.Run(() => repo.Write(manifest, capture.Docs), token);
            var (count, total, retainWarning) = await backupWorker.Run(() => repo.Retain(settings, backupClock()), token);
            if (retainWarning != null) warnings.Add(retainWarning);
            backedUpRevisions = capture.Revisions;
            var message = fuzzy ? "Backed up while the library kept changing; some files may be from slightly different moments." : warnings.Count > 0 ? warnings[0] : "Backed up.";
            SaveBackupResult(new(now.ToUnixTimeMilliseconds(), true, total, count, message));
            if (kind != "scheduled") Log($"Backup made ({kind}): {Size(manifest.Bytes)}, {Size(manifest.NewBytes)} new");
            return new(true, id, path, manifest.Bytes, manifest.NewBytes, count, fuzzy, [.. warnings], message);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            var message = ex is OperationCanceledException ? "The backup stopped because the companion is closing." : ex is InvalidDataException ? ex.Message : "The backup could not be written: " + ex.Message;
            SaveBackupResult(new(backupClock().ToUnixTimeMilliseconds(), false, settings.LastResult?.Bytes ?? 0, settings.LastResult?.Snapshots ?? 0, message));
            Log("Backup failed: " + message);
            return new(false, null, null, 0, 0, 0, false, [], message);
        }
        finally
        {
            backupRunning = false; backupRun.Release(); BackupsChanged();
        }
    }

    private static long? SafeModified(string path) { try { return new FileInfo(path).LastWriteTimeUtc.Ticks; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; } }

    /// <summary>The copy waits a moment after each 1 MiB while Soundpad plays, so a game's audio never competes with it.</summary>
    private void PauseWhilePlaying() { if (playback.Status is "PLAYING" or "SEEKING") Thread.Sleep(5); }

    private void SaveBackupResult(BackupLastResult result)
    {
        try { if (BackupStore.Problem == null) BackupStore.Save(BackupStore.Current with { LastResult = result }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Log("The backup result could not be saved: " + ex.Message); }
    }

    internal static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} bytes";

    /// <summary>A backup before a risky step; skipped, with the reason, when backups are off or the run failed.</summary>
    private async Task<BackupCheckpoint?> BackupCheckpoint(string kind, CancellationToken ct)
    {
        if (!BackupStore.Current.Enabled || BackupStore.Problem != null) return new("", "", true, "Backups are not set up.");
        var result = await RunBackup(kind, true, ct);
        return result.Ok ? new(result.Id!, result.Path!, false, null) : new("", "", true, result.Message);
    }

    // ---- Schedule --------------------------------------------------------------------------------------------------

    private void BackupsStarted()
    {
        if (Interlocked.Exchange(ref backupsStarted, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BackupStartDelayMs, backupStop.Token);
                bool first = true;
                while (!backupStop.IsCancellationRequested)
                {
                    try { await BackupTick(first); first = false; }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { Log("A scheduled backup check failed: " + ex.Message); }
                    await Task.Delay(30000, backupStop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// The schedule's check (every 30 s): a backup at startup, every <c>intervalHours</c>, and <c>afterChangeMinutes</c>
    /// after the library, sentences or kept audio last changed. True when it ran one.
    /// </summary>
    internal async Task<bool> BackupTick(bool startup = false)
    {
        var settings = BackupStore.Current;
        if (!settings.Enabled || BackupStore.Problem != null || Volatile.Read(ref disposed) != 0) return false;
        long now = backupClock().ToUnixTimeMilliseconds();
        var revisions = DataRevisions();
        if (seenRevisions != revisions) { seenRevisions = revisions; changedAtMs = now; }
        bool interval = settings.LastResult == null || now - settings.LastResult.AtUtcMs >= settings.IntervalHours * 3600000L;
        bool changed = settings.AfterChangeMinutes > 0 && backedUpRevisions != null && backedUpRevisions != revisions && now - changedAtMs >= settings.AfterChangeMinutes * 60000L;
        if (!startup && !interval && !changed) return false;
        var result = await RunBackup("scheduled", true);
        return result.Ok;
    }

    private void BackupsDispose()
    {
        try { backupStop.Cancel(); } catch (ObjectDisposedException) { }
        backupWorker.Dispose();
    }
}
