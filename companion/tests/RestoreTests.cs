using System.Xml.Linq;
using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// Restore (2.44, WP1): single kept clips, recordings, voices and sentences, the whole library, identity, limits, guests,
/// and the offline --restore-state. Temporary folders and a mock Soundpad only; nothing is imported into Soundpad.
/// </summary>
[Collection("History")]
public sealed class RestoreTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "soundpad-restore-" + Guid.NewGuid().ToString("N"));
    private static int Count(JsonElement reply, string field) => reply.GetProperty(field).ValueKind == JsonValueKind.Array ? reply.GetProperty(field).GetArrayLength() : reply.GetProperty(field).GetInt32();

    private static async Task<(BackupRig Rig, string AssetId)> WithClip(string? root = null)
    {
        var rig = new BackupRig(root ?? NewRoot());
        await rig.Host();
        var asset = await rig.KeepClip("Kept line");
        await rig.SetFolder();
        return (rig, asset);
    }

    [Fact]
    public async Task ClipRestoresToOriginalPathKeepsSoundIdStatsAndIcon()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        await rig.Bridge.Poll(true);
        var sound = rig.Bridge.Snapshot.Library.Sounds.Single(s => s.AssetId == asset);
        await rig.Ok(BackupRig.Cmd("play") with { SoundId = sound.Id, LibraryVersion = rig.Bridge.Snapshot.Library.Version });
        await rig.Ok(BackupRig.Cmd("stop"));
        await rig.Ok(BackupRig.Cmd("icon") with { SoundId = sound.Id, Image = BackupRig.Icon(10) });
        var statistics = rig.State().Statistics.Keys.Order().ToArray();
        Assert.NotEmpty(statistics);
        var variant = rig.KeptVariant(asset);
        var bytes = File.ReadAllBytes(variant.Path);
        var run = await rig.Backup();
        File.Delete(variant.Path);
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "clip", ids = new[] { variant.Id } });
        Assert.Equal(1, Count(reply, "restored"));
        Assert.Equal(0, Count(reply, "conflicts"));
        Assert.Equal(0, Count(reply, "attention"));
        Assert.Equal(bytes, File.ReadAllBytes(variant.Path));
        await rig.Bridge.Poll(true);
        var after = rig.Bridge.Snapshot.Library.Sounds.Single(s => s.AssetId == asset);
        Assert.Equal(sound.Id, after.Id);
        Assert.NotNull(after.Icon);
        Assert.Equal(statistics, rig.State().Statistics.Keys.Order().ToArray());
        // The "before restore" backup was made first.
        var list = await rig.OkOp("backupList", new { });
        Assert.Contains(list.GetProperty("snapshots").EnumerateArray(), s => s.GetProperty("kind").GetString() == "pre-restore");
    }

    [Fact]
    public async Task ClipWithMissingNativeEntryIsListedNotImported()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        var variant = rig.KeptVariant(asset);
        var run = await rig.Backup();
        File.Delete(variant.Path);
        // Its Soundpad entry is gone too.
        var all = System.Xml.Linq.XDocument.Parse(rig.Mock.AllXml);
        all.Root!.Elements("Sound").Where(e => string.Equals((string?)e.Attribute("url"), variant.Path, StringComparison.OrdinalIgnoreCase)).Remove();
        rig.Mock.AllXml = all.ToString();
        var cats = System.Xml.Linq.XDocument.Parse(rig.Mock.CategoryXml);
        cats.Descendants("Sound").Where(e => string.Equals((string?)e.Attribute("url"), variant.Path, StringComparison.OrdinalIgnoreCase)).Remove();
        rig.Mock.CategoryXml = cats.ToString();
        int adds = rig.Mock.Commands.Count(c => c.StartsWith("DoAddSound", StringComparison.Ordinal));
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "clip", ids = new[] { variant.Id } });
        Assert.True(File.Exists(variant.Path));
        var attention = reply.GetProperty("attention").EnumerateArray().Single();
        Assert.Equal($"Add {Path.GetFileName(variant.Path)} to Soundpad again (Saved category).", attention.GetProperty("message").GetString());
        Assert.Equal(variant.Path, attention.GetProperty("path").GetString());
        Assert.Equal(adds, rig.Mock.Commands.Count(c => c.StartsWith("DoAddSound", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExistingDifferentFileIsAConflict()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        var variant = rig.KeptVariant(asset);
        var run = await rig.Backup();
        var other = BackupRig.Wav(1, 880);
        File.WriteAllBytes(variant.Path, other);
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "clip", ids = new[] { variant.Id } });
        Assert.Equal(0, Count(reply, "restored"));
        Assert.Contains("different file", reply.GetProperty("conflicts")[0].GetString());
        Assert.Equal(other, File.ReadAllBytes(variant.Path));
    }

    [Fact]
    public async Task RecordingRestoreIsOneAuditGroupAndUndoReverts()
    {
        await using var rig = new BackupRig(NewRoot());
        await rig.Host();
        await rig.KeepClip("One"); await rig.KeepClip("Two");
        var library = rig.Bridge.VoiceLibraryStore;
        var (voice, _) = library.CreateVoice("Narrator", null, "host", "Host");
        var ids = library.List(new(), 0, 10).Items.Select(r => r.Id).ToArray();
        library.Edit(ids, new LibraryEdit(VoiceId: voice.Id, AddTags: ["kept"]), "host", "Host");
        var words = ids.ToDictionary(id => id, id => library.WordsOf(id).Length);
        await rig.SetFolder();
        var run = await rig.Backup();
        library.Remove(ids, "host", "Host");
        library.DeleteVoice(voice.Id, "host", "Host");
        Assert.Equal(0, library.Count);
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "recording", ids });
        Assert.Equal(2, Count(reply, "restored"));
        long group = reply.GetProperty("auditGroup").GetInt64();
        Assert.Equal(2, library.Count);
        Assert.All(ids, id => Assert.Equal(voice.Id, library.Get(id)!.VoiceId));
        Assert.Single(library.Voices, v => v.Name == "Narrator");
        Assert.All(library.Audit(max: 10).Where(a => a.Group == group), a => Assert.Equal(group, a.Group));
        Assert.Equal(3, library.Audit(max: 20).Count(a => a.Group == group)); // the voice and both recordings
        // The words came back too.
        Assert.All(ids, id => Assert.Equal(words[id], library.WordsOf(id).Length));
        // The normal Undo takes the whole restore back.
        await rig.Ok(BackupRig.Cmd("libraryUndo") with { AuditId = group });
        Assert.Equal(0, library.Count);
        Assert.Empty(library.Voices);
        // Restoring again while they are here skips them; "replace" puts the backup's fields back.
        await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "recording", ids });
        Assert.Equal(2, Count(await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "recording", ids }), "skipped"));
        library.Edit([ids[0]], new LibraryEdit(Title: "Renamed"), "host", "Host");
        var replaced = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "recording", ids = new[] { ids[0] }, mode = "replace" });
        Assert.Equal(1, Count(replaced, "restored"));
        Assert.NotEqual("Renamed", library.Get(ids[0])!.Title);
    }

    [Fact]
    public async Task VoiceRestore()
    {
        await using var rig = new BackupRig(NewRoot());
        await rig.Host();
        var library = rig.Bridge.VoiceLibraryStore;
        var (voice, _) = library.CreateVoice("Villain", "#e0699a", "host", "Host");
        library.AddWanted(voice.Id, "mwahaha", "host", "Host");
        await rig.SetFolder();
        var run = await rig.Backup();
        library.DeleteVoice(voice.Id, "host", "Host");
        Assert.Empty(library.Voices);
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "voice", ids = new[] { voice.Id } });
        Assert.Equal(1, Count(reply, "restored"));
        Assert.Equal("#e0699a", library.Voices.Single().Color);
        Assert.Single(library.Wanted, w => w.Text == "mwahaha");
        await rig.Ok(BackupRig.Cmd("libraryUndo") with { AuditId = reply.GetProperty("auditGroup").GetInt64() });
        Assert.Empty(library.Voices);
        // A voice whose name another voice has now is a conflict.
        library.CreateVoice("Villain", null, "host", "Host");
        var clash = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "voice", ids = new[] { voice.Id } });
        Assert.Equal(0, Count(clash, "restored"));
        Assert.Single(clash.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task SentenceRestoreWithVersionsChecksWavHashes()
    {
        await using var rig = new BackupRig(NewRoot());
        await rig.Host();
        var store = rig.Bridge.SentenceStoreForTests;
        var (id, file, wav) = rig.Sentence("Two takes", 2);
        var (other, otherFile, _) = rig.Sentence("Another");
        await rig.SetFolder();
        var run = await rig.Backup();
        var folder = Path.Combine(store.AudioFolder, id);
        var files = Directory.GetFiles(folder);
        Assert.Equal(2, files.Length);
        store.Change(f => { f.Sentences.RemoveAll(s => s.Id == id || s.Id == other); return 0; });
        foreach (var f in files) File.Delete(f);
        // The other sentence's audio is a different file now: that one is a conflict and stays out.
        File.WriteAllBytes(store.AudioPath(other, otherFile), BackupRig.Wav(0.2, 999));
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "sentence", ids = new[] { id, other } });
        Assert.Equal(1, Count(reply, "restored"));
        Assert.Single(reply.GetProperty("conflicts").EnumerateArray());
        var restored = store.Read(f => SentenceStore.Copy(f.Sentences.Single(s => s.Id == id)));
        Assert.Equal(2, restored.Versions.Count);
        foreach (var v in restored.Versions) Assert.True(ManagedFiles.Verified(store.AudioPath(id, v.File), v.Hash, v.Bytes));
        Assert.Equal(wav, File.ReadAllBytes(store.AudioPath(id, file)));
        Assert.False(store.Read(f => f.Sentences.Any(s => s.Id == other)));
        // Undo takes it out again.
        var undo = await rig.OkOp("backupRestore", new { kind = "sentence", undo = reply.GetProperty("undoToken").GetString() });
        Assert.Equal(1, undo.GetProperty("undone").GetInt32());
        Assert.False(store.Read(f => f.Sentences.Any(s => s.Id == id)));
    }

    [Fact]
    public async Task ADiscardDuringASentenceRestoreNeverLeavesTheSentenceWithoutItsAudio()
    {
        await using var rig = new BackupRig(NewRoot());
        await rig.Host();
        var store = rig.Bridge.SentenceStoreForTests;
        var (id, file, _) = rig.Sentence("Kept line");
        await rig.SetFolder();
        var run = await rig.Backup();
        // The host discards the kept sentence just as a restore of it has checked its audio, before it records it.
        Task<Reply>? discard = null;
        rig.Bridge.SentenceAudioRestored = () =>
        {
            rig.Bridge.SentenceAudioRestored = null;
            discard = Task.Run(() => rig.Bridge.Execute(BackupRig.Cmd("sentenceDiscard") with { SentenceId = id }, "host"));
            discard.Wait(500); // until 2.47 it ran (and swept the audio) here
        };
        await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "sentence", ids = new[] { id } });
        Assert.NotNull(discard);
        var discarded = await discard!;
        Assert.True(discarded.Ok, discarded.Error);
        // Whatever the order, a sentence in the list has its audio, and audio without a sentence is gone.
        bool listed = store.Read(f => f.Sentences.Any(s => s.Id == id));
        Assert.Equal(listed, File.Exists(store.AudioPath(id, file)));
    }

    [Fact]
    public async Task WholeLibraryRefusedWhilePlayingOrPromoting()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        var run = await rig.Backup();
        await rig.Bridge.Poll(true);
        var sound = rig.Bridge.Snapshot.Library.Sounds.First(s => s.Name == "Airhorn");
        rig.Mock.DurationMs = 600000;
        await rig.Ok(BackupRig.Cmd("play") with { SoundId = sound.Id, LibraryVersion = rig.Bridge.Snapshot.Library.Version });
        await rig.Bridge.Poll(false);
        Assert.Contains("Stop playback first", (await rig.Op("backupRestore", new { snapshot = run.Id, kind = "library" })).Error);
        await rig.Ok(BackupRig.Cmd("stop"));
        await rig.Bridge.Poll(false);
        // A Keep under way (its import into Soundpad is slow).
        rig.Mock.SlowMs = 1500; rig.Mock.SlowPrefix = "DoAddSound(";
        var keeping = rig.KeepClip("Being kept");
        await Task.Delay(400);
        Assert.Contains("in progress", (await rig.Op("backupRestore", new { snapshot = run.Id, kind = "library" })).Error);
        await keeping;
        rig.Mock.SlowMs = 0;
        // Refused means nothing changed: no "before restore" backup either.
        var list = await rig.OkOp("backupList", new { });
        Assert.DoesNotContain(list.GetProperty("snapshots").EnumerateArray(), s => s.GetProperty("kind").GetString() == "pre-restore");
    }

    [Fact]
    public async Task WholeLibraryReplacesFieldsNeverReceiptsAndTakesPreRestoreCheckpoint()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        var library = rig.Bridge.VoiceLibraryStore;
        await rig.Ok(BackupRig.Cmd("counter") with { Delta = 1 });
        await rig.Ok(BackupRig.Cmd("counter") with { Delta = 1 });
        await rig.Ok(BackupRig.Cmd("permissions") with { Permissions = new(GuestCounter: true) });
        var (id, _, _) = rig.Sentence("Backed up sentence");
        var run = await rig.Backup();
        // Afterwards: other values, a new receipt, the recording and sentence removed.
        await rig.Ok(BackupRig.Cmd("counter") with { Delta = 1 });
        await rig.Ok(BackupRig.Cmd("permissions") with { Permissions = new(GuestCounter: false, RemotePlayback: false) });
        var receipt = BackupRig.Cmd("counter") with { Delta = -1 };
        await rig.Ok(receipt);
        library.Remove([.. library.List(new(), 0, 10).Items.Select(r => r.Id)], "host", "Host");
        rig.Bridge.SentenceStoreForTests.Change(f => { f.Sentences.Clear(); return 0; });
        rig.Now = rig.Now.AddMinutes(10);
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "library" });
        Assert.Equal(0, Count(reply, "conflicts"));
        var state = rig.State();
        Assert.Equal(2, state.Counter);
        Assert.True(state.Permissions.GuestCounter);
        Assert.True(state.Permissions.RemotePlayback);
        Assert.True(state.Receipts.ContainsKey(receipt.Id)); // receipts are never replaced
        Assert.Equal(1, library.Count);
        Assert.True(rig.Bridge.SentenceStoreForTests.Read(f => f.Sentences.Any(s => s.Id == id)));
        // The library file it replaced is kept as the backup copy of its normal save.
        Assert.True(File.Exists(Path.Combine(rig.Data, "library", "voice-library.v1.bak.json")));
        var list = (await rig.OkOp("backupList", new { })).GetProperty("snapshots").EnumerateArray().ToList();
        Assert.Equal("pre-restore", list[0].GetProperty("kind").GetString());
        // Restoring the "before restore" backup brings back how it was.
        var before = list[0].GetProperty("id").GetString();
        rig.Now = rig.Now.AddMinutes(10);
        await rig.OkOp("backupRestore", new { snapshot = before, kind = "library" });
        state = rig.State();
        Assert.Equal(2, state.Counter); // 2 + 1 - 1
        Assert.False(state.Permissions.RemotePlayback);
        Assert.Equal(0, library.Count);
    }

    [Fact]
    public async Task RestoringThePreRestoreCheckpointBringsBackTheBefore()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        var library = rig.Bridge.VoiceLibraryStore;
        var run = await rig.Backup();
        var (voice, _) = library.CreateVoice("Made after", null, "host", "Host");
        await rig.Ok(BackupRig.Cmd("counter") with { Delta = 1 });
        rig.Now = rig.Now.AddMinutes(1);
        await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "library" });
        Assert.Empty(library.Voices);
        Assert.Equal(0, rig.State().Counter);
        var pre = (await rig.OkOp("backupList", new { })).GetProperty("snapshots").EnumerateArray().First(s => s.GetProperty("kind").GetString() == "pre-restore").GetProperty("id").GetString();
        rig.Now = rig.Now.AddMinutes(1);
        await rig.OkOp("backupRestore", new { snapshot = pre, kind = "library" });
        Assert.Single(library.Voices, v => v.Id == voice.Id);
        Assert.Equal(1, rig.State().Counter);
    }

    [Fact]
    public async Task JournalLibrarySentenceLimitsRespected()
    {
        // The journal's 2,048-source limit.
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "managed-audio"));
            var journal = new AudioJournal();
            for (int i = 0; i < 2048; i++) journal.Assets.Add(new AudioAsset { Origin = "upload", State = "permanent" });
            File.WriteAllBytes(Path.Combine(root, "managed-audio", "journal.json"), JsonSerializer.SerializeToUtf8Bytes(journal, SoundpadWeb.Json.Options));
            await using var mock = new MockSoundpad();
            using var pipe = new SoundpadPipe(mock.Name, true);
            var audio = new ManagedAudioStore(root, pipe, p => "id", work => work());
            Assert.False(audio.Public.Blocked);
            var extra = new AudioAsset { Origin = "upload", State = "permanent" };
            extra.Variants.Add(new AudioVariant { Path = Path.Combine(root, "kept.wav"), Storage = "permanent", OwnsFile = true, Permanent = true, State = "ready", FileHash = new string('a', 64), Bytes = 10 });
            Assert.Contains("2,048", Assert.Throws<InvalidDataException>(() => audio.MergePermanent(extra, true)).Message);
            Assert.Equal(2048, JsonSerializer.Deserialize<AudioJournal>(File.ReadAllBytes(Path.Combine(root, "managed-audio", "journal.json")), SoundpadWeb.Json.Options)!.Assets.Count);
        }
        finally { TestFolders.Delete(root); }
        // The sentences file's size limit: a restore that would pass it changes nothing.
        await using var rig = new BackupRig(NewRoot());
        await rig.Host();
        var (id, _, _) = rig.Sentence("A sentence with some text in it");
        await rig.SetFolder();
        var run = await rig.Backup();
        var store = rig.Bridge.SentenceStoreForTests;
        store.Change(f => { f.Sentences.Clear(); return 0; });
        store.SaveLimit = 100;
        Assert.Contains("MiB", (await rig.Op("backupRestore", new { snapshot = run.Id, kind = "sentence", ids = new[] { id } })).Error);
        Assert.False(store.Read(f => f.Sentences.Any()));
        store.SaveLimit = SentenceStore.MaxFileBytes;
        // The library's: at most 500 recordings per restore.
        var many = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        Assert.False((await rig.Op("backupRestore", new { snapshot = run.Id, kind = "recording", ids = many })).Ok);
    }

    [Fact]
    public async Task IdentityMatch()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        await rig.Bridge.Poll(true);
        var sound = rig.Bridge.Snapshot.Library.Sounds.First(s => s.Name == "Laugh");
        await rig.Ok(BackupRig.Cmd("icon") with { SoundId = sound.Id, Image = BackupRig.Icon(200) });
        await rig.Ok(BackupRig.Cmd("play") with { SoundId = sound.Id, LibraryVersion = rig.Bridge.Snapshot.Library.Version });
        await rig.Ok(BackupRig.Cmd("stop"));
        var key = rig.State().IdentityKey;
        var run = await rig.Backup();
        await rig.Ok(BackupRig.Cmd("clearStatistics"));
        await rig.Ok(BackupRig.Cmd("icon") with { SoundId = sound.Id, Image = "" });
        rig.Now = rig.Now.AddMinutes(1);
        var detail = await rig.OkOp("backupDetail", new { snapshot = run.Id });
        Assert.Equal("same", detail.GetProperty("library").GetProperty("identity").GetString());
        await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "library" });
        var state = rig.State();
        Assert.Equal(key, state.IdentityKey);
        Assert.NotEmpty(state.Statistics);
        Assert.NotEmpty(state.IconFiles);
        Assert.True(File.Exists(Path.Combine(rig.Data, "icons", state.IconFiles.Values.Single())));
    }

    /// <summary>A second install (its own data folder and identity) sharing the first one's backup folder.</summary>
    private static async Task<(BackupRig First, BackupRig Second, string Snapshot)> TwoInstalls()
    {
        var root = NewRoot();
        var first = new BackupRig(root);
        await first.Host();
        await first.KeepClip("From the first install");
        var sound = first.Bridge.Snapshot.Library.Sounds.First(s => s.Name == "Airhorn");
        await first.Ok(BackupRig.Cmd("play") with { SoundId = sound.Id, LibraryVersion = first.Bridge.Snapshot.Library.Version });
        await first.Ok(BackupRig.Cmd("stop"));
        await first.Ok(BackupRig.Cmd("counter") with { Delta = 1 });
        await first.SetFolder();
        var run = await first.Backup();
        var second = new BackupRig(Path.Combine(root, "second"));
        await second.Host();
        await second.SetFolder(first.Backups);
        return (first, second, run.Id!);
    }

    [Fact]
    public async Task IdentityMismatchEmptyAdoptsOnlyWhenAsked()
    {
        var (first, second, snapshot) = await TwoInstalls();
        await using var b = first; await using var a = second;
        var list = (await second.OkOp("backupList", new { })).GetProperty("snapshots").EnumerateArray().ToList();
        Assert.True(list.Single(s => s.GetProperty("id").GetString() == snapshot).GetProperty("other").GetBoolean());
        Assert.Equal("different", (await second.OkOp("backupDetail", new { snapshot })).GetProperty("library").GetProperty("identity").GetString());
        // Without asking: the library and counter, not the statistics, and the identity stays.
        var own = second.State().IdentityKey;
        var reply = await second.OkOp("backupRestore", new { snapshot, kind = "library" });
        Assert.Contains("another identity", reply.GetProperty("warnings")[0].GetString());
        Assert.Equal(own, second.State().IdentityKey);
        Assert.Empty(second.State().Statistics);
        Assert.Equal(1, second.State().Counter);
        Assert.Equal(1, second.Bridge.VoiceLibraryStore.Count);
    }

    [Fact]
    public async Task IdentityMismatchEmptyInstallAdoptsWhenTicked()
    {
        var (first, second, snapshot) = await TwoInstalls();
        await using var b = first; await using var a = second;
        await second.OkOp("backupRestore", new { snapshot, kind = "library", adoptIdentity = true });
        Assert.Equal(first.State().IdentityKey, second.State().IdentityKey);
        Assert.NotEmpty(second.State().Statistics);
        Assert.Equal(first.Bridge.Snapshot.HostId, second.Bridge.Snapshot.HostId);
    }

    [Fact]
    public async Task IdentityMismatchWithDataSkipsIdKeyedState()
    {
        var (first, second, snapshot) = await TwoInstalls();
        await using var b = first; await using var a = second;
        // The second install keeps data of its own.
        var sound = second.Bridge.Snapshot.Library.Sounds.First(s => s.Name == "Laugh");
        await second.Ok(BackupRig.Cmd("play") with { SoundId = sound.Id, LibraryVersion = second.Bridge.Snapshot.Library.Version });
        await second.Ok(BackupRig.Cmd("stop"));
        await second.Ok(BackupRig.Cmd("counter") with { Delta = 1 }); // written at once, with the play's statistics
        var own = second.State();
        Assert.NotEmpty(own.Statistics);
        Assert.Contains("keeps its own data", (await second.Op("backupRestore", new { snapshot, kind = "library", adoptIdentity = true })).Error);
        Assert.Equal(own.IdentityKey, second.State().IdentityKey);
        await second.OkOp("backupRestore", new { snapshot, kind = "library" });
        var after = second.State();
        Assert.Equal(own.IdentityKey, after.IdentityKey);
        Assert.Equal(own.Statistics.Keys.Order(), after.Statistics.Keys.Order());
    }

    [Fact]
    public async Task GuestCannotCallAnyBackupOp()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        var run = await rig.Backup();
        var guest = new Participant("11111111-1111-1111-1111-111111111111", "Alice");
        var bodies = new Dictionary<string, object>
        {
            ["backupSettings"] = new { action = "get" }, ["backupNow"] = new { }, ["backupList"] = new { }, ["backupDetail"] = new { snapshot = run.Id },
            ["backupRestore"] = new { snapshot = run.Id, kind = "library" }, ["backupAttach"] = new { source = "browser", workspace = rig.Workspace() }, ["backupLayout"] = new { snapshot = run.Id, source = "browser" },
        };
        foreach (var (op, body) in bodies)
        {
            var r = await rig.Bridge.Execute(BackupRig.Cmd(op) with { Body = BackupRig.Json(body) }, "guest", guest, "peer-alice");
            Assert.False(r.Ok, op);
            Assert.Equal("Host permission required.", r.Error);
        }
        // Unknown fields and missing bodies are refused for the host too.
        Assert.Contains("does not know", (await rig.Op("backupList", new { path = "C:\\" })).Error);
        Assert.False((await rig.Bridge.Execute(BackupRig.Cmd("backupList"), "host")).Ok);
    }

    [Fact]
    public async Task OfflineRestoreStateValidatesAndRefusesWhileLocked()
    {
        var (rig, asset) = await WithClip();
        await using var _ = rig;
        await rig.Ok(BackupRig.Cmd("counter") with { Delta = 1 });
        var run = await rig.Backup();
        var data = Path.Combine(rig.Root, "offline");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "state.json"), "{ damaged");
        // The startup message names the newest backup's copy (with backup settings in that folder).
        File.Copy(Path.Combine(rig.Data, "backup-settings.v1.json"), Path.Combine(data, "backup-settings.v1.json"));
        var unreadable = Assert.Throws<InvalidDataException>(() => new StateStore(data).Load()).Message;
        Assert.Contains("it has not been reset", unreadable);
        Assert.Contains("--restore-state \"" + run.Path + "\"", unreadable);
        Assert.Contains(Path.Combine(run.Path!, "data") + "\\state.json", unreadable);
        // Refused while another companion holds the lock.
        using (new FileStream(Path.Combine(data, "companion.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains("is running", Assert.Throws<InvalidDataException>(() => OfflineRestore.Restore(run.Path, data)).Message);
        // Refused for a folder that is not a backup.
        Assert.Throws<InvalidDataException>(() => OfflineRestore.Restore(rig.Root, data));
        Assert.Equal(1, OfflineRestore.Run(["--restore-state", rig.Root, "--data-dir", data]));
        // Restored: the backup's copy, the damaged one kept beside it.
        var line = OfflineRestore.Restore(run.Path, data);
        Assert.Contains("state.before-restore-", line);
        Assert.Equal(1, JsonSerializer.Deserialize<DiskState>(File.ReadAllText(Path.Combine(data, "state.json")), SoundpadWeb.Json.Options)!.Counter);
        Assert.Contains(Directory.GetFiles(data, "state.before-restore-*.json"), f => File.ReadAllText(f) == "{ damaged");
        new StateStore(data).Load();
        // A damaged copy in the backup is refused, and nothing is written.
        var doc = rig.Manifest(run.Id!).Files.Single(f => f.Role == "state");
        File.AppendAllText(Path.Combine(run.Path!, "data", doc.Path), " ");
        var before = File.ReadAllText(Path.Combine(data, "state.json"));
        Assert.Contains("damaged", Assert.Throws<InvalidDataException>(() => OfflineRestore.Restore(run.Path, data)).Message);
        Assert.Equal(before, File.ReadAllText(Path.Combine(data, "state.json")));
    }
}
