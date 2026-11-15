using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

// Restore from a backup (2.44, gate backups). Host only. Everything read from a backup is checked first, with the stores'
// own validators; a "before restore" backup is made first (for a single item it is skipped with a warning when it cannot
// be made; a whole-library restore requires it). Files go back only to the exact path they had, and only where nothing is
// now: a different file there is reported as a conflict and never overwritten. Nothing is ever imported into Soundpad.
public sealed partial class Bridge
{
    private readonly SemaphoreSlim restoreGate = new(1, 1);
    private volatile bool restoringLibrary;
    /// <summary>Undo of sentence restores (sentences have no audit trail): what each restored sentence was before. In memory only.</summary>
    private readonly ConcurrentDictionary<string, List<(string Id, SentenceProject? Before, long Updated)>> sentenceUndo = new(StringComparer.Ordinal);
    private static readonly Regex HexId = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex GuidId = new("^[a-fA-F0-9-]{32,36}$", RegexOptions.CultureInvariant);

    private sealed record OpenBackup(BackupRepository Repo, BackupManifest Manifest);

    private OpenBackup FindBackup(string id)
    {
        if (!BackupManifest.SnapshotName.IsMatch(id)) throw new InvalidDataException("Choose a backup.");
        var folder = BackupStore.Current.Folder ?? throw new InvalidDataException("Backups are not set up.");
        foreach (var repo in BackupRepositories(folder))
        {
            if (!Directory.Exists(repo.SnapshotPath(id))) continue;
            var m = repo.Read(id, out var problem) ?? throw new InvalidDataException("That backup cannot be used: " + problem + ".");
            return new(repo, m);
        }
        throw new InvalidDataException("That backup is no longer in the backup folder.");
    }

    private static byte[]? DocBytes(OpenBackup b, string role) => b.Manifest.Files.FirstOrDefault(f => f.Role == role) is { } d ? b.Repo.ReadDoc(b.Manifest.Id, d) : null;

    /// <summary>A backup's state.json, checked as <see cref="StateStore.Load"/> checks it.</summary>
    internal static DiskState ParseBackupState(byte[] bytes)
    {
        if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("The backup's state is too large.");
        DiskState? state;
        try { state = JsonSerializer.Deserialize<DiskState>(bytes, Json.Options); }
        catch (JsonException) { throw new InvalidDataException("The backup's state is damaged."); }
        if (state == null || state.SchemaVersion != 2 || state.Counter < 0) throw new InvalidDataException("The backup's state has an unknown format.");
        try { if (Convert.FromBase64String(state.IdentityKey).Length != 32) throw new InvalidDataException("The backup's state is damaged."); }
        catch (FormatException) { throw new InvalidDataException("The backup's state is damaged."); }
        if (state.IconFiles.Count > 1024 || state.Activity.Count > 100000) throw new InvalidDataException("The backup's state is damaged.");
        return state;
    }

    private sealed record BackupParts(LibraryFile? Library, Dictionary<string, LibraryWord[]> Words, SentenceFile? Sentences, AudioJournal? Journal, ManagedAudioStore.KeptFile[] Kept, DiskState? State, JsonObject? Workspace);

    private static BackupParts Parts(OpenBackup b, bool state = false)
    {
        var lib = DocBytes(b, "library") is { } l ? VoiceLibrary.ParseBackup(l) : null;
        var words = VoiceLibrary.ParseWords(DocBytes(b, "words"));
        var sentences = DocBytes(b, "sentences") is { } s ? SentenceStore.ParseBackup(s) : null;
        AudioJournal? journal = null; ManagedAudioStore.KeptFile[] kept = [];
        if (DocBytes(b, "journal") is { } j) (journal, kept) = ManagedAudioStore.ParseBackup(j);
        var st = state && DocBytes(b, "state") is { } sb ? ParseBackupState(sb) : null;
        var ws = DocBytes(b, "workspace") is { } w ? ParseWorkspaceFile(w) : null;
        return new(lib, words, sentences, journal, kept, st, ws);
    }

    // ---- Detail -----------------------------------------------------------------------------------------------------

    private static string Status(bool here, bool same) => !here ? "missing now" : same ? "same" : "changed since";

    private object BackupDetail(Command c)
    {
        var body = CommandBody.Of(c, "snapshot");
        var b = FindBackup(body.Str("snapshot", 40) ?? throw new InvalidDataException("Choose a backup."));
        var p = Parts(b);
        var state = DocBytes(b, "state") is { } sb ? ParseBackupState(sb) : null;
        const int Max = 500;
        var clips = p.Kept.Take(Max).Select(k =>
        {
            bool exists = File.Exists(k.Path);
            string status = !exists ? "missing now" : !ManagedFiles.Verified(k.Path, k.Hash, k.Bytes) ? "changed since" : !Audio.Knows(k.AssetId) ? "not here" : "same";
            return new { id = k.VariantId, name = k.Name, file = Path.GetFileName(k.Path), path = k.Path, status };
        }).ToArray();
        var library = Voices.CopyFile();
        var hereRecordings = library.Recordings.ToDictionary(r => r.Id, StringComparer.Ordinal);
        string Shape(LibraryRecording r) => Json.Write(new { r.Title, r.VoiceId, Tags = r.Tags.ToArray(), r.Favorite, r.CorrectedText, r.Reviewed });
        var recordings = (p.Library?.Recordings ?? []).Take(Max).Select(r => new { id = r.Id, name = r.Title.Length > 0 ? r.Title : Path.GetFileNameWithoutExtension(r.Path), status = Status(hereRecordings.TryGetValue(r.Id, out var h), h != null && Shape(h) == Shape(r)) }).ToArray();
        var voices = (p.Library?.Voices ?? []).Take(Max).Select(v => { var h = library.Voices.FirstOrDefault(x => x.Id == v.Id); return new { id = v.Id, name = v.Name, status = Status(h != null, h != null && h.Name == v.Name && h.Color == v.Color) }; }).ToArray();
        var hereSentences = SentenceData.Read(f => f.Sentences.ToDictionary(s => s.Id, s => Json.Write(s), StringComparer.Ordinal));
        var sentences = (p.Sentences?.Sentences ?? []).Take(Max).Select(s => new { id = s.Id, name = s.Title.Length > 0 ? s.Title : s.Text.Length > 60 ? s.Text[..60] + "…" : s.Text, status = Status(hereSentences.TryGetValue(s.Id, out var json), json == Json.Write(s)) }).ToArray();
        JsonObject? currentPages = null;
        lock (workspaceGate) { try { if (File.Exists(WorkspacePath)) currentPages = ParseWorkspaceFile(File.ReadAllBytes(WorkspacePath))?["pages"] as JsonObject; } catch (IOException) { } }
        var layouts = (p.Workspace?["pages"] as JsonObject ?? []).Select(kv => new
        {
            id = kv.Key, name = kv.Key == "window" ? "Companion window layout" : "Browser layout",
            status = currentPages?[kv.Key] is JsonObject now ? (now["workspace"]?.ToJsonString() == kv.Value?["workspace"]?.ToJsonString() ? "same" : "changed since") : "not here",
        }).ToArray();
        bool sameIdentity = state == null || state.IdentityKey == disk.IdentityKey;
        return new
        {
            snapshot = b.Manifest.Id, kind = b.Manifest.Kind, createdUtcMs = b.Manifest.CreatedUtcMs, fuzzy = b.Manifest.Fuzzy, warnings = b.Manifest.Warnings.Take(50).ToArray(),
            clips, recordings, voices, sentences, layouts,
            library = new
            {
                recordings = p.Library?.Recordings.Count ?? 0, voices = p.Library?.Voices.Count ?? 0, sentences = p.Sentences?.Sentences.Count ?? 0, clips = p.Kept.Length,
                icons = state?.IconFiles.Count ?? 0, sounds = state?.Statistics.Count ?? 0, counter = state?.Counter, identity = sameIdentity ? "same" : "different",
                more = new { clips = Math.Max(0, p.Kept.Length - Max), recordings = Math.Max(0, (p.Library?.Recordings.Count ?? 0) - Max), sentences = Math.Max(0, (p.Sentences?.Sentences.Count ?? 0) - Max) },
            },
        };
    }

    // ---- Restore ----------------------------------------------------------------------------------------------------

    private sealed class RestoreOutcome
    {
        public int Restored, Skipped;
        public List<string> Conflicts = [], Warnings = [];
        public List<object> Attention = [];
        public long? AuditGroup;
        public string? UndoToken;
        public object Reply() => new { restored = Restored, skipped = Skipped, conflicts = Conflicts.Take(100).ToArray(), attention = Attention.Take(100).ToArray(), warnings = Warnings.Take(50).ToArray(), auditGroup = AuditGroup, undoToken = UndoToken };
    }

    private async Task<object> BackupRestore(Command c)
    {
        var body = CommandBody.Of(c, "snapshot", "kind", "ids", "mode", "adoptIdentity", "undo");
        var kind = body.Enum("kind", "clip", "recording", "voice", "sentence", "library") ?? throw new InvalidDataException("Choose what to restore.");
        if (body.Str("undo", 40) is { } token)
        {
            if (kind != "sentence") throw new InvalidDataException("Only restored sentences are undone this way; recordings use Undo in the voice library.");
            if (!sentenceUndo.TryRemove(token, out var undo)) throw new InvalidDataException("That restore is too old to undo.");
            int done = SentenceData.UndoMerge(undo);
            SentencesChanged();
            return new { restored = 0, skipped = undo.Count - done, conflicts = Array.Empty<string>(), attention = Array.Empty<object>(), warnings = done < undo.Count ? new[] { "Some sentences changed since they were restored; they were left as they are." } : [], undone = done };
        }
        var id = body.Str("snapshot", 40) ?? throw new InvalidDataException("Choose a backup.");
        bool replace = (body.Enum("mode", "skip", "replace") ?? "skip") == "replace";
        var pattern = kind == "clip" ? GuidId : HexId;
        var ids = body.Ids("ids", 500, pattern);
        if (kind != "library" && ids is not { Length: > 0 }) throw new InvalidDataException("Choose what to restore.");
        if (!await restoreGate.WaitAsync(0)) throw new InvalidDataException("A restore is already running. Try again when it has finished.");
        try
        {
            var b = FindBackup(id);
            var outcome = new RestoreOutcome();
            if (kind == "library") { await RestoreLibrary(b, body.Bool("adoptIdentity") == true, outcome); }
            else
            {
                var parts = Parts(b);
                // Checked before anything is changed.
                if (kind is "recording" or "voice" && parts.Library == null) throw new InvalidDataException("That backup has no voice library.");
                if (kind == "sentence" && parts.Sentences == null) throw new InvalidDataException("That backup has no sentences.");
                if (kind == "clip" && !ids!.All(v => parts.Kept.Any(k => k.VariantId == v))) throw new InvalidDataException("That kept clip is not in the backup.");
                if (kind == "recording" && !ids!.All(r => parts.Library!.Recordings.Any(x => x.Id == r))) throw new InvalidDataException("That recording is not in the backup.");
                if (kind == "voice" && !ids!.All(v => parts.Library!.Voices.Any(x => x.Id == v))) throw new InvalidDataException("That voice is not in the backup.");
                if (kind == "sentence" && !ids!.All(s => parts.Sentences!.Sentences.Any(x => x.Id == s))) throw new InvalidDataException("That sentence is not in the backup.");
                var checkpoint = await BackupCheckpoint("pre-restore", default);
                if (checkpoint is { Skipped: true }) outcome.Warnings.Add("No backup was made first: " + checkpoint.Reason);
                bool sameIdentity = DocBytes(b, "state") is not { } sb || ParseBackupState(sb).IdentityKey == disk.IdentityKey;
                switch (kind)
                {
                    case "clip": await RestoreClips(b, parts, ids!, sameIdentity, outcome); break;
                    case "recording": await RestoreRecordingItems(b, parts, ids!, replace, sameIdentity, outcome); break;
                    case "voice":
                        {
                            var chosen = parts.Library!.Voices.Where(v => ids!.Contains(v.Id)).ToList();
                            var (restored, skipped, group, conflicts) = Voices.RestoreVoices(chosen, parts.Library.Wanted, replace, "host", "Host");
                            outcome.Restored = restored; outcome.Skipped = skipped; outcome.AuditGroup = group > 0 ? group : null;
                            outcome.Conflicts.AddRange(conflicts.Select(n => $"Another voice is called {n} now; that voice was left out."));
                            break;
                        }
                    case "sentence": RestoreSentenceItems(b, parts, ids!, replace, outcome); break;
                }
            }
            Log($"Restored from a backup ({kind}): {outcome.Restored} restored, {outcome.Skipped} skipped, {outcome.Conflicts.Count} conflict(s)");
            BackupsChanged();
            return outcome.Reply();
        }
        finally { restoringLibrary = false; restoreGate.Release(); }
    }

    /// <summary>True when a path is one a restore may write: on this PC, outside Soundpad's folder, the data folder's temporary audio and the backups.</summary>
    private bool RestorablePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Any(char.IsControl)) return false;
        if (ManagedFiles.Beneath(path, leppsoftFolder) || ManagedFiles.Beneath(path, Audio.Root) || ManagedFiles.Beneath(path, Path.Combine(store.DirectoryPath, "session-audio"))) return false;
        if (BackupStore.Current.Folder is { } folder && ManagedFiles.Beneath(path, BackupFolders.Root(folder))) return false;
        return true;
    }

    /// <summary>
    /// Puts a file back from the blob store at its exact path: only where nothing is now (hash verified, never over
    /// another file). True when the file is there afterwards with the backup's content.
    /// </summary>
    private bool RestoreFile(OpenBackup b, BackupBlobRef? blob, string path, RestoreOutcome outcome)
    {
        var name = Path.GetFileName(path);
        if (blob == null) { if (!File.Exists(path)) outcome.Warnings.Add($"{name} is not in the backup."); return File.Exists(path); }
        if (!RestorablePath(path)) { outcome.Conflicts.Add($"{name}: its folder is not one a restore writes to; it was left out."); return false; }
        if (File.Exists(path))
        {
            if (ManagedFiles.Verified(path, blob.Sha256, blob.Bytes)) return true;
            outcome.Conflicts.Add($"{name}: a different file is there now; it was left as it is.");
            return false;
        }
        try { ManagedFiles.CopyNew(b.Repo.RequireBlob(blob), path, blob.Sha256, blob.Bytes); return true; }
        catch (InvalidDataException ex) { outcome.Conflicts.Add($"{name}: {ex.Message}"); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { outcome.Conflicts.Add($"{name} could not be written: {ex.Message}"); return false; }
    }

    /// <summary>Kept clips: the file to its original path, the source back into the journal, its Soundpad entry verified (never imported).</summary>
    private async Task RestoreClips(OpenBackup b, BackupParts p, IReadOnlyCollection<string> variantIds, bool sameIdentity, RestoreOutcome outcome)
    {
        await audioGate.EnterAsync(PriorityGate.Lane.Normal);
        try
        {
            foreach (var k in p.Kept.Where(k => variantIds.Contains(k.VariantId)))
            {
                var blob = b.Manifest.Blobs.FirstOrDefault(x => x.Role == "clip" && x.VariantId == k.VariantId && x.Sha256 == k.Hash);
                bool present = File.Exists(k.Path) && ManagedFiles.Verified(k.Path, k.Hash, k.Bytes);
                if (!RestoreFile(b, blob, k.Path, outcome)) continue;
                bool merged = false;
                if (!Audio.Knows(k.AssetId) && p.Journal?.Assets.FirstOrDefault(a => a.Id == k.AssetId) is { } asset)
                {
                    try { merged = Audio.MergePermanent(asset, sameIdentity); }
                    catch (InvalidDataException ex) { outcome.Conflicts.Add($"{Path.GetFileName(k.Path)}: the kept-audio list could not take it back ({ex.Message})"); continue; }
                }
                if (present && !merged) { outcome.Skipped++; continue; }
                outcome.Restored++;
                if (!await Audio.RecheckKept(k.AssetId))
                    outcome.Attention.Add(new { file = Path.GetFileName(k.Path), path = k.Path, message = $"Add {Path.GetFileName(k.Path)} to Soundpad again (Saved category)." });
            }
        }
        finally { audioGate.Release(); }
        await gate.EnterAsync(PriorityGate.Lane.Normal); try { await RefreshInner(); } finally { gate.Release(); }
        Publish();
    }

    /// <summary>Recordings: a missing file back to its path (and its kept clip into the journal), then the records as one audit group.</summary>
    private async Task RestoreRecordingItems(OpenBackup b, BackupParts p, IReadOnlyCollection<string> ids, bool replace, bool sameIdentity, RestoreOutcome outcome)
    {
        var chosen = p.Library!.Recordings.Where(r => ids.Contains(r.Id)).ToList();
        var clipVariants = new List<string>();
        foreach (var r in chosen)
        {
            var kept = p.Kept.FirstOrDefault(k => string.Equals(k.Path, r.Path, StringComparison.OrdinalIgnoreCase));
            if (kept != null && (!File.Exists(r.Path) || !Audio.Knows(kept.AssetId))) { clipVariants.Add(kept.VariantId); continue; }
            if (!File.Exists(r.Path)) RestoreFile(b, b.Manifest.Blobs.FirstOrDefault(x => x.Role == "recording" && x.RecordingId == r.Id), r.Path, outcome);
        }
        if (clipVariants.Count > 0)
        {
            var clips = new RestoreOutcome();
            await RestoreClips(b, p, clipVariants, sameIdentity, clips);
            outcome.Conflicts.AddRange(clips.Conflicts); outcome.Attention.AddRange(clips.Attention); outcome.Warnings.AddRange(clips.Warnings);
        }
        var (restored, skipped, group) = Voices.RestoreRecordings(chosen, p.Library.Voices, p.Words, replace, "host", "Host");
        outcome.Restored = restored; outcome.Skipped = skipped; outcome.AuditGroup = group > 0 ? group : null;
        foreach (var r in chosen) if (!File.Exists(r.Path)) outcome.Warnings.Add($"The file of “{r.Title}” is not back; it plays once the file is there again.");
    }

    /// <summary>Sentences, with all their versions: each version's audio only to its exact name, checked by hash.</summary>
    private void RestoreSentenceItems(OpenBackup b, BackupParts p, IReadOnlyCollection<string> ids, bool replace, RestoreOutcome outcome)
    {
        var ok = new List<SentenceProject>();
        foreach (var s in p.Sentences!.Sentences.Where(s => ids.Contains(s.Id)))
            if (RestoreSentenceAudio(b, s, outcome)) ok.Add(s);
        if (ok.Count == 0) return;
        var (restored, skipped, undo) = SentenceData.Merge(ok, replace);
        outcome.Restored = restored; outcome.Skipped = skipped;
        if (undo.Count > 0) { var token = Guid.NewGuid().ToString("N"); sentenceUndo[token] = undo; outcome.UndoToken = token; if (sentenceUndo.Count > 64) foreach (var old in sentenceUndo.Keys.Take(sentenceUndo.Count - 64)) sentenceUndo.TryRemove(old, out _); }
        SentencesChanged();
    }

    private bool RestoreSentenceAudio(OpenBackup b, SentenceProject s, RestoreOutcome outcome)
    {
        bool all = true;
        foreach (var v in s.Versions)
        {
            string path;
            try { path = SentenceData.AudioPath(s.Id, v.File); } catch (InvalidDataException) { all = false; continue; }
            if (File.Exists(path))
            {
                if (!ManagedFiles.Verified(path, v.Hash, v.Bytes)) { outcome.Conflicts.Add($"“{(s.Title.Length > 0 ? s.Title : s.Text)}”: version {v.N}'s audio is a different file now; the sentence was left as it is."); all = false; }
                continue;
            }
            var blob = b.Manifest.Blobs.FirstOrDefault(x => x.Role == "sentence" && x.SentenceId == s.Id && x.Version == v.N && x.Sha256 == v.Hash);
            if (blob == null) { outcome.Warnings.Add($"Version {v.N} of “{(s.Title.Length > 0 ? s.Title : s.Text)}” has no audio in the backup; keep it again to hear it."); continue; }
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); ManagedFiles.CopyNew(b.Repo.RequireBlob(blob), path, v.Hash, v.Bytes); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { outcome.Conflicts.Add($"Version {v.N}'s audio could not be written: {ex.Message}"); all = false; }
        }
        return all;
    }

    /// <summary>True when this install keeps nothing yet: no kept audio, recordings, sentences, statistics or icons.</summary>
    private async Task<bool> EmptyInstall()
    {
        int kept;
        await audioGate.EnterAsync(PriorityGate.Lane.Normal);
        try { kept = Audio.CaptureBackup().Kept.Length; } finally { audioGate.Release(); }
        return kept == 0 && Voices.Count == 0 && SentenceData.Read(f => f.Sentences.Count) == 0 && disk.Statistics.Count == 0 && disk.IconFiles.Count == 0;
    }

    /// <summary>
    /// The whole library: refused while anything plays, a Keep or promotion is under way or Temp is being written; a
    /// "before restore" backup is required first. Then the library, sentences and their words through the normal saves,
    /// the kept audio and sentence audio that is missing, the kept clips into the journal (never session or Temp records),
    /// the state's counter, statistics, activity, icons, permissions and Spotify setting (never its receipts), and the
    /// small settings files. Guests' library and sentence changes are refused meanwhile.
    /// </summary>
    private async Task RestoreLibrary(OpenBackup b, bool adoptIdentity, RestoreOutcome outcome)
    {
        if (playback.Status is "PLAYING" or "PAUSED" or "SEEKING") throw new InvalidDataException("Stop playback first. Nothing was restored.");
        // Not during an update or an Organize apply (Stage H cross-checks, BridgeStageH.cs).
        if (UpdateBusy is { } updating) throw new InvalidDataException(updating + " Nothing was restored.");
        if (OrganizeApplying) throw new InvalidDataException("An organization plan is being applied to Soundpad. Try again when it has finished or paused; nothing was restored.");
        if (Audio.Public.Assets.Any(a => a.State is "promoting" or "promotion-pending")) throw new InvalidDataException("A Keep is still being finished. Try again when it is done; nothing was restored.");
        if (!audioGate.TryEnter()) throw new InvalidDataException("A Keep or a change in Temp is in progress. Try again in a moment; nothing was restored.");
        audioGate.Release();
        // Everything is read and checked before anything changes.
        var p = Parts(b, state: true);
        if (p.State == null) throw new InvalidDataException("That backup has no state; nothing was restored.");
        bool sameIdentity = p.State.IdentityKey == disk.IdentityKey;
        bool adopt = !sameIdentity && adoptIdentity && await EmptyInstall();
        if (!sameIdentity && adoptIdentity && !adopt) throw new InvalidDataException("This install keeps its own data already, so it keeps its identity. Restore without “Use this backup's identity”.");
        var checkpoint = await BackupCheckpoint("pre-restore", default);
        if (checkpoint is null or { Skipped: true }) throw new InvalidDataException("A backup of the library as it is now could not be made first (" + (checkpoint?.Reason ?? "unknown").TrimEnd('.') + "). Nothing was restored.");
        if (playback.Status is "PLAYING" or "PAUSED" or "SEEKING") throw new InvalidDataException("Stop playback first. Nothing was restored.");
        restoringLibrary = true;
        bool idKeyed = sameIdentity || adopt;
        if (!idKeyed) outcome.Warnings.Add("This backup belongs to another identity: the library, sentences and counter were restored, but statistics and icons (kept by sound) were left as they are.");
        await audioGate.EnterAsync(PriorityGate.Lane.Normal);
        try
        {
            // Sentences and the voice library, through their own saves.
            if (p.Library != null) { Voices.ReplaceAll(p.Library, p.Words); outcome.Restored += p.Library.Recordings.Count; }
            if (p.Sentences != null)
            {
                foreach (var s in p.Sentences.Sentences) RestoreSentenceAudio(b, s, outcome);
                SentenceData.ReplaceAll(p.Sentences); outcome.Restored += p.Sentences.Sentences.Count;
                SentencesChanged();
            }
            // Missing audio back to its paths: kept clips, then the library's other files.
            foreach (var k in p.Kept)
                RestoreFile(b, b.Manifest.Blobs.FirstOrDefault(x => x.Role == "clip" && x.VariantId == k.VariantId && x.Sha256 == k.Hash), k.Path, outcome);
            foreach (var r in p.Library?.Recordings ?? [])
                if (!File.Exists(r.Path) && b.Manifest.Blobs.FirstOrDefault(x => x.Role == "recording" && x.RecordingId == r.Id) is { } blob) RestoreFile(b, blob, r.Path, outcome);
            // Kept sources the journal lost; never session or Temp records, and the session stays as it is.
            foreach (var asset in p.Journal?.Assets ?? [])
            {
                if (Audio.Knows(asset.Id) || !p.Kept.Any(k => k.AssetId == asset.Id && File.Exists(k.Path))) continue;
                try { if (Audio.MergePermanent(asset, idKeyed)) outcome.Restored++; }
                catch (InvalidDataException ex) { outcome.Conflicts.Add($"{asset.Name}: the kept-audio list could not take it back ({ex.Message})"); break; }
            }
            // The state's fields, never its receipts.
            await gate.EnterAsync(PriorityGate.Lane.Normal);
            try
            {
                var s = p.State;
                var icons = new Dictionary<string, string>(StringComparer.Ordinal);
                if (idKeyed)
                    foreach (var (key, file) in s.IconFiles)
                    {
                        var path = Path.Combine(store.DirectoryPath, "icons", file);
                        if (!IconFile.IsMatch(file)) continue;
                        if (File.Exists(path) || b.Manifest.Blobs.FirstOrDefault(x => x.Role == "icon" && x.Original == file) is { } icon && RestoreIcon(b, icon, path)) icons[key] = file;
                    }
                disk.Counter = s.Counter; disk.Permissions = s.Permissions with { GuestKeep = null, GuestLibraryEdit = null }; disk.SpotifyEnabled = s.SpotifyEnabled;
                if (adopt) { disk.IdentityKey = s.IdentityKey; outcome.Warnings.Add("This install now uses the backup's identity; guests' saved layouts follow it."); }
                if (idKeyed) { disk.Statistics = s.Statistics; disk.Activity = s.Activity; disk.Icons = s.Icons; disk.IconFiles = icons; disk.StatisticsVersion = Math.Max(disk.StatisticsVersion, s.StatisticsVersion) + 1; }
                SaveNow(pruneIcons: true);
                iconCache = null;
                counterSession = Math.Min(counterSession, disk.Counter);
            }
            finally { gate.Release(); }
            // The small settings files (written whole), and what the companion holds of them in memory.
            foreach (var role in new[] { "keep", "mimic", "ui" })
            {
                if (DocBytes(b, role) is not { } bytes) continue;
                if (bytes.Length > 1024 * 1024) { outcome.Warnings.Add($"The backup's {BackupManifest.DocPaths[role]} is too large; it was left out."); continue; }
                DurableFile.Write(Path.Combine(store.DirectoryPath, BackupManifest.DocPaths[role]), bytes);
                if (role == "keep") lock (keepGate) keepSettings = null;
                if (role == "mimic") mimicSettings = null;
                if (role == "ui") { uiPrefs = null; Interlocked.Increment(ref uiRevision); }
            }
            foreach (var k in p.Kept.Where(k => File.Exists(k.Path)).DistinctBy(k => k.AssetId))
                if (!await Audio.RecheckKept(k.AssetId)) outcome.Attention.Add(new { file = Path.GetFileName(k.Path), path = k.Path, message = $"Add {Path.GetFileName(k.Path)} to Soundpad again (Saved category)." });
        }
        finally { audioGate.Release(); restoringLibrary = false; }
        await gate.EnterAsync(PriorityGate.Lane.Normal); try { await RefreshInner(); } finally { gate.Release(); }
        Publish();
    }

    /// <summary>An icon back into the icons folder under its exact name, which carries the start of its hash.</summary>
    private static bool RestoreIcon(OpenBackup b, BackupBlobRef icon, string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(b.Repo.RequireBlob(icon));
            var sha = BackupRepository.Sha(bytes);
            if (sha != icon.Sha256 || IconFile.Match(Path.GetFileName(path)).Groups[1].Value != sha[..16].ToUpperInvariant()) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            DurableFile.Write(path, bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
}
