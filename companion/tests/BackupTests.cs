using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
namespace SoundpadWeb.Tests;

/// <summary>
/// Backups (2.44, WP1): what a snapshot holds and how it is written, deduplication, consistency while things change,
/// crashes, retention and clean-up, the folder rules, damaged data, free space, playback priority, checkpoints. Temporary
/// folders, a mock Soundpad, a fake clock and a fault seam only; Soundpad's own folder is a made-up one.
/// </summary>
[Collection("History")]
public sealed class BackupTests(ITestOutputHelper output)
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "soundpad-backups-" + Guid.NewGuid().ToString("N"));
    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static async Task<BackupRig> Full(BackupFiles? files = null)
    {
        var rig = new BackupRig(NewRoot(), files);
        await rig.Host();
        await rig.KeepClip("First clip");
        await rig.KeepClip("Second clip");
        var sound = rig.Bridge.Snapshot.Library.Sounds.First(s => s.Name == "Airhorn");
        await rig.Ok(BackupRig.Cmd("icon") with { SoundId = sound.Id, Image = BackupRig.Icon(90) });
        rig.Sentence("Hello there", 2);
        await rig.OkOp("backupAttach", new { source = "window", workspace = rig.Workspace() });
        await rig.SetFolder();
        return rig;
    }

    [Fact]
    public async Task SnapshotListsEveryFileWithVerifiedHashes()
    {
        await using var rig = await Full();
        var run = await rig.Backup();
        var m = rig.Manifest(run.Id!);
        Assert.Equal(new[] { 2, 44 }, m.MinReader);
        Assert.Equal("manual", m.Kind);
        Assert.False(m.Fuzzy);
        foreach (var role in new[] { "state", "library", "words", "sentences", "journal", "workspace" }) Assert.Contains(m.Files, f => f.Role == role);
        foreach (var doc in m.Files)
        {
            var path = Path.Combine(rig.Repo.SnapshotPath(m.Id), "data", doc.Path);
            Assert.Equal(doc.Sha256, Sha(path));
            Assert.Equal(doc.Bytes, new FileInfo(path).Length);
        }
        Assert.Equal(2, m.Blobs.Count(b => b.Role == "clip"));
        Assert.Equal(2, m.Blobs.Count(b => b.Role == "recording"));
        Assert.Equal(2, m.Blobs.Count(b => b.Role == "sentence"));
        Assert.Single(m.Blobs, b => b.Role == "icon");
        foreach (var b in m.Blobs)
        {
            var path = rig.Repo.BlobPath(b.Sha256, b.Ext);
            Assert.Equal(b.Sha256, Sha(path));
            Assert.Equal(b.Bytes, new FileInfo(path).Length);
        }
        // The kept files are the journal's, with the hashes it recorded.
        foreach (var asset in rig.Journal().Assets) { var v = asset.Variants.Single(x => x.Permanent); Assert.Contains(m.Blobs, b => b.Role == "clip" && b.Sha256 == v.FileHash && b.Original == v.Path); }
        // Listed, newest first, without paths.
        var list = await rig.OkOp("backupList", new { limit = 10 });
        Assert.Equal(run.Id, list.GetProperty("snapshots")[0].GetProperty("id").GetString());
        Assert.DoesNotContain(rig.Data, list.GetRawText().Replace("\\\\", "\\"));
    }

    [Fact]
    public async Task SecondSnapshotCopiesNoAudioBytes()
    {
        await using var rig = await Full();
        var first = await rig.Backup();
        Assert.True(first.NewBytes > 0);
        rig.Now = rig.Now.AddMinutes(5);
        var second = await rig.Backup();
        Assert.Equal(0, second.NewBytes);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, rig.Repo.SnapshotFolders().Length);
        // Both refer to the same blobs.
        Assert.Equal(rig.Manifest(first.Id!).Blobs.Select(b => b.Sha256).Order(), rig.Manifest(second.Id!).Blobs.Select(b => b.Sha256).Order());
    }

    [Fact]
    public async Task ConcurrentLibraryEditsSentenceKeepsAndPromotionsLeaveNoDanglingReference()
    {
        await using var rig = await Full();
        var library = rig.Bridge.VoiceLibraryStore;
        var firstRecording = library.List(new(), 0, 10).Items.First().Id;
        rig.Bridge.BackupAttemptHook = attempt =>
        {
            if (attempt != 0) return;
            // While the first capture's files are copied: a library edit, a kept sentence and a Keep (a promotion).
            library.Edit([firstRecording], new LibraryEdit(AddTags: ["meanwhile"]), "host", "Host");
            rig.Sentence("Said meanwhile");
            rig.KeepClip("Kept meanwhile").GetAwaiter().GetResult();
        };
        var run = await rig.Backup();
        var m = rig.Manifest(run.Id!);
        Assert.False(m.Fuzzy);
        var docs = m.Files.ToDictionary(f => f.Role, f => rig.Repo.ReadDoc(m.Id, f));
        var lib = VoiceLibrary.ParseBackup(docs["library"]);
        var sentences = SentenceStore.ParseBackup(docs["sentences"]);
        var (_, kept) = ManagedAudioStore.ParseBackup(docs["journal"]);
        // The later capture: everything that happened meanwhile is in it, and every file it names is stored.
        Assert.Contains(lib.Recordings, r => r.Title == "Kept meanwhile");
        Assert.Contains(lib.Recordings.Single(r => r.Id == firstRecording).Tags, t => t == "meanwhile");
        Assert.Contains(sentences.Sentences, s => s.Title == "Said meanwhile");
        foreach (var r in lib.Recordings) Assert.Contains(m.Blobs, b => b.RecordingId == r.Id && File.Exists(rig.Repo.BlobPath(b.Sha256, b.Ext)));
        foreach (var k in kept) Assert.Contains(m.Blobs, b => b.Role == "clip" && b.VariantId == k.VariantId && b.Sha256 == k.Hash);
        foreach (var s in sentences.Sentences) foreach (var v in s.Versions) Assert.Contains(m.Blobs, b => b.Role == "sentence" && b.SentenceId == s.Id && b.Version == v.N && b.Sha256 == v.Hash);
    }

    [Fact]
    public async Task RevisionRaceRetriesThenMarksFuzzy()
    {
        await using var rig = await Full();
        var library = rig.Bridge.VoiceLibraryStore;
        var id = library.List(new(), 0, 10).Items.First().Id;
        int attempts = 0;
        rig.Bridge.BackupAttemptHook = attempt => { attempts = attempt + 1; library.Edit([id], new LibraryEdit(Favorite: attempt % 2 == 0), "host", "Host"); };
        var run = await rig.Backup();
        Assert.Equal(4, attempts); // the first capture and three more
        Assert.True(run.Fuzzy);
        Assert.True(rig.Manifest(run.Id!).Fuzzy);
        Assert.Contains("kept changing", run.Message);
    }

    [Fact]
    public async Task CrashMidBlobOrMidManifestLeavesEarlierSnapshotsAndSweepsPartial()
    {
        var files = new FaultyFiles();
        await using var rig = await Full(files);
        var first = await rig.Backup();
        // A new kept clip, and the process "ends" in the middle of copying it.
        await rig.KeepClip("Third clip");
        files.CrashAfterChunk = (_, _) => true;
        await Assert.ThrowsAsync<SimulatedCrash>(() => rig.Bridge.RunBackup("manual"));
        files.CrashAfterChunk = null;
        // And in the middle of writing a manifest.
        rig.Now = rig.Now.AddMinutes(1);
        files.CrashOnCreate = path => path.EndsWith(BackupManifest.FileName, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SimulatedCrash>(() => rig.Bridge.RunBackup("manual"));
        files.CrashOnCreate = null;
        var snapshots = Path.Combine(rig.Install, "snapshots");
        Assert.Contains(Directory.GetDirectories(snapshots), d => Path.GetFileName(d).StartsWith(".partial-", StringComparison.Ordinal));
        // A copy left half-written, as after a power cut.
        var leftover = Path.Combine(rig.Install, "blobs", "ab", ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!); File.WriteAllBytes(leftover, [1, 2, 3]);
        // The earlier snapshot is untouched and still the only one listed.
        Assert.NotNull(rig.Repo.Read(first.Id!, out _));
        Assert.Equal([first.Id!], rig.Repo.SnapshotFolders());
        // The next run sweeps the partial snapshot and the leftover copy.
        rig.Now = rig.Now.AddMinutes(1);
        var next = await rig.Backup();
        Assert.DoesNotContain(Directory.GetDirectories(snapshots), d => Path.GetFileName(d).StartsWith(".partial-", StringComparison.Ordinal));
        Assert.False(File.Exists(leftover));
        Assert.Equal(2, rig.Repo.SnapshotFolders().Length);
        Assert.Contains(rig.Manifest(next.Id!).Blobs, b => b.Role == "clip" && b.Original == rig.Journal().Assets.Single(a => a.Name == "Third clip").Variants.Single(v => v.Permanent).Path);
    }

    private static BackupManifest Fake(DateTimeOffset at, string kind = "scheduled") => new() { Id = $"{at.UtcDateTime:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..25], Kind = kind, CreatedUtcMs = at.ToUnixTimeMilliseconds() };

    [Fact]
    public void RetentionKeepsCountDaysWeeklyAndPreRestoreSevenDays()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero); // a Saturday
        var all = Enumerable.Range(0, 60).Select(d => Fake(now.AddDays(-d).AddHours(-1))).ToList();
        var preRecent = Fake(now.AddDays(-6).AddHours(-3), "pre-restore");
        var preOld = Fake(now.AddDays(-9).AddHours(-3), "pre-restore");
        all.Add(preRecent); all.Add(preOld);
        var keep = BackupRepository.Keep(all, new BackupSettings(KeepCount: 3, KeepDays: 5), now);
        // The newest three, and everything from the last five days.
        foreach (var m in all.OrderByDescending(m => m.CreatedUtcMs).Take(3)) Assert.Contains(m.Id, keep);
        foreach (var m in all.Where(m => m.CreatedUtcMs >= now.AddDays(-5).ToUnixTimeMilliseconds())) Assert.Contains(m.Id, keep);
        // A "before restore" backup for at least 7 days, even past the 5 days.
        Assert.Contains(preRecent.Id, keep);
        // The newest of every ISO week (its own week's newest is a daily one here, so the old pre-restore goes).
        Assert.DoesNotContain(preOld.Id, keep);
        foreach (var week in all.GroupBy(m => System.Globalization.ISOWeek.GetWeekOfYear(DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedUtcMs).UtcDateTime)))
            Assert.Contains(week.MaxBy(m => m.CreatedUtcMs)!.Id, keep);
        // Nothing else: a day 20 days back that is not its week's newest goes.
        var expected = all.OrderByDescending(m => m.CreatedUtcMs).Take(3).Select(m => m.Id)
            .Concat(all.Where(m => m.CreatedUtcMs >= now.AddDays(-5).ToUnixTimeMilliseconds()).Select(m => m.Id)).Append(preRecent.Id)
            .Concat(all.GroupBy(m => (System.Globalization.ISOWeek.GetYear(DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedUtcMs).UtcDateTime), System.Globalization.ISOWeek.GetWeekOfYear(DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedUtcMs).UtcDateTime))).Select(g => g.MaxBy(m => m.CreatedUtcMs)!.Id))
            .ToHashSet();
        Assert.Equal(expected.Order(), keep.Order());
        Assert.True(keep.Count < all.Count);
    }

    [Fact]
    public async Task GcKeepsReferencedBlobsAndNeverTouchesForeignFilesOrReparsePoints()
    {
        await using var rig = await Full();
        rig.Now = rig.Now.AddDays(-40);
        var old = await rig.Backup();
        var blobs = Path.Combine(rig.Install, "blobs");
        // Foreign files and look-alikes, an unreferenced blob of the app's own, and a junction to a folder holding a blob-named file.
        var foreign = new[] { Path.Combine(blobs, "ab", "notes.txt"), Path.Combine(blobs, "ab", new string('a', 64) + ".exe"), Path.Combine(rig.Install, "readme.txt"), Path.Combine(blobs, "zz", new string('b', 64) + ".wav") };
        foreach (var f in foreign) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, "not ours"); }
        var stale = Path.Combine(blobs, "cd", "cd" + new string('0', 62) + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!); File.WriteAllText(stale, "ours, unreferenced");
        var outside = Path.Combine(rig.Root, "outside"); Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "ef" + new string('1', 62) + ".wav"); File.WriteAllText(outsideFile, "behind a junction");
        var junction = Path.Combine(blobs, "ef");
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!) mk.WaitForExit();
        Assert.True(Directory.Exists(junction));
        var foreignSnapshot = Path.Combine(rig.Install, "snapshots", "not-a-backup"); Directory.CreateDirectory(foreignSnapshot); File.WriteAllText(Path.Combine(foreignSnapshot, "x.txt"), "x");
        // Retention drops the 40-day-old snapshot (keep 1, 1 day, and it is not its week's only one... it is, so: a newer one in its week).
        rig.Now = rig.Now.AddHours(1);
        var sameWeek = await rig.Backup();
        rig.Now = rig.Now.AddDays(40);
        await rig.SetFolder(keepCount: 1, keepDays: 1);
        var current = await rig.Backup();
        var left = rig.Repo.SnapshotFolders();
        Assert.DoesNotContain(old.Id!, left);
        Assert.Contains(sameWeek.Id!, left); // the newest of its ISO week
        Assert.Contains(current.Id!, left);
        foreach (var f in foreign) Assert.True(File.Exists(f), f);
        Assert.True(File.Exists(outsideFile));
        Assert.True(Directory.Exists(foreignSnapshot));
        Assert.False(File.Exists(stale));
        foreach (var b in rig.Manifest(current.Id!).Blobs) Assert.True(File.Exists(rig.Repo.BlobPath(b.Sha256, b.Ext)));
        Directory.Delete(junction);
    }

    [Fact]
    public async Task FolderRulesRefuseUncInsideDataInsideKeepInsideLeppsoftAndParents()
    {
        await using var rig = new BackupRig(NewRoot());
        await rig.Host();
        Directory.CreateDirectory(rig.Leppsoft);
        string Refuse(string folder) => Assert.Throws<InvalidDataException>(() => BackupFolders.Check(folder, rig.Data, rig.Leppsoft, Path.Combine(rig.Root, "keep", "Soundpad Web"))).Message;
        Assert.Contains("network", Refuse(@"\\server\share\backups"));
        Assert.Contains("on this PC", Refuse("relative\\folder"));
        Assert.Contains("data folder", Refuse(Path.Combine(rig.Data, "backups")));
        Assert.Contains("data folder", Refuse(rig.Root)); // contains the data folder
        Assert.Contains("Soundpad's own folder", Refuse(Path.Combine(rig.Leppsoft, "x")));
        Assert.Contains("Keep folder", Refuse(Path.Combine(rig.Root, "keep", "Soundpad Web", "b")));
        Assert.Contains("Keep folder", Refuse(Path.Combine(rig.Root, "keep")));
        Assert.Contains("drive", Refuse("Q:\\no-such-drive\\x").Replace("Q:\\", "drive"));
        Assert.Contains("whole drive", Refuse(Path.GetPathRoot(rig.Root)!));
        // A junction is refused.
        var real = Path.Combine(rig.Root, "real"); Directory.CreateDirectory(real);
        var link = Path.Combine(rig.Root, "link");
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!) mk.WaitForExit();
        Assert.Contains("link", Refuse(link));
        // A good folder passes and is created; the command says why a bad one was refused, and saves nothing.
        Assert.Equal(rig.Backups, BackupFolders.Check(rig.Backups, rig.Data, rig.Leppsoft, null));
        var refused = await rig.Op("backupSettings", new { action = "set", folder = @"\\server\share" });
        Assert.False(refused.Ok);
        Assert.False(File.Exists(Path.Combine(rig.Data, "backup-settings.v1.json")));
        // The suggested folder comes from the companion.
        var set = await rig.OkOp("backupSettings", new { action = "set", useSuggested = true });
        Assert.Equal(Path.Combine(rig.Root, "suggested"), set.GetProperty("settings").GetProperty("folder").GetString());
        Directory.Delete(link);
    }

    [Fact]
    public async Task NewerMinReaderAndCorruptManifestOrBlobAreRefused()
    {
        await using var rig = await Full();
        var first = await rig.Backup();
        rig.Now = rig.Now.AddMinutes(1);
        var second = await rig.Backup();
        rig.Now = rig.Now.AddMinutes(1);
        var third = await rig.Backup();
        // A manifest a newer companion wrote.
        var newer = Path.Combine(rig.Repo.SnapshotPath(first.Id!), BackupManifest.FileName);
        File.WriteAllText(newer, File.ReadAllText(newer).Replace("\"minReader\":[2,44]", "\"minReader\":[3,0]"));
        Assert.Null(rig.Repo.Read(first.Id!, out var problem));
        Assert.Contains("newer Soundpad Web", problem);
        Assert.Contains("newer Soundpad Web", (await rig.Op("backupDetail", new { snapshot = first.Id })).Error);
        // A damaged manifest.
        File.WriteAllText(Path.Combine(rig.Repo.SnapshotPath(second.Id!), BackupManifest.FileName), "{ not json");
        Assert.False((await rig.Op("backupRestore", new { snapshot = second.Id, kind = "library" })).Ok);
        // While either is in the folder, retention removes no blob at all (they may be theirs).
        var (_, _, warning) = rig.Repo.Retain(new BackupSettings(KeepCount: 1, KeepDays: 1), rig.Now.AddDays(30));
        Assert.Contains("could not be read", warning);
        // A damaged blob: the restore refuses to put it back, and says why.
        var clip = rig.Manifest(third.Id!).Blobs.First(b => b.Role == "clip");
        var blob = rig.Repo.BlobPath(clip.Sha256, clip.Ext);
        var bytes = File.ReadAllBytes(blob); bytes[^1] ^= 0xff; File.WriteAllBytes(blob, bytes);
        File.Delete(clip.Original);
        var reply = await rig.OkOp("backupRestore", new { snapshot = third.Id, kind = "clip", ids = new[] { clip.VariantId } });
        Assert.Equal(0, reply.GetProperty("restored").GetInt32());
        Assert.NotEmpty(reply.GetProperty("conflicts").EnumerateArray());
        Assert.False(File.Exists(clip.Original));
        // A damaged copy of a small file is refused before anything changes.
        var state = rig.Manifest(third.Id!).Files.Single(f => f.Role == "state");
        File.AppendAllText(Path.Combine(rig.Repo.SnapshotPath(third.Id!), "data", state.Path), " ");
        Assert.Contains("damaged", (await rig.Op("backupRestore", new { snapshot = third.Id, kind = "library" })).Error);
    }

    [Fact]
    public async Task LowFreeSpaceRefusesTheRun()
    {
        var files = new FaultyFiles { Free = 1000 };
        await using var rig = await Full(files);
        var run = await rig.Bridge.RunBackup("manual");
        Assert.False(run.Ok);
        Assert.Contains("Not enough free space", run.Message);
        Assert.False(Directory.Exists(Path.Combine(rig.Backups, BackupFolders.RootName)));
        var status = await rig.OkOp("backupSettings", new { action = "get" });
        Assert.False(status.GetProperty("status").GetProperty("lastResult").GetProperty("ok").GetBoolean());
        Assert.Contains("Not enough free space", status.GetProperty("status").GetProperty("lastResult").GetProperty("message").GetString());
    }

    [Fact]
    public async Task DamagedSettingsFileMeansOffAndIsLeftUntouched()
    {
        await using var rig = new BackupRig(NewRoot());
        var path = Path.Combine(rig.Data, "backup-settings.v1.json");
        var damaged = "{\"format\":1,\"folder\":\"" + rig.Backups.Replace("\\", "\\\\") + "\",\"keepCount\":0}";
        File.WriteAllText(path, damaged);
        await rig.Restart();
        await rig.Host();
        var status = await rig.OkOp("backupSettings", new { action = "get" });
        Assert.Equal(JsonValueKind.Null, status.GetProperty("settings").GetProperty("folder").ValueKind);
        Assert.Contains("left untouched", status.GetProperty("settings").GetProperty("problem").GetString());
        Assert.Contains("could not be read", (await rig.Op("backupSettings", new { action = "set", folder = rig.Backups })).Error);
        Assert.False((await rig.Op("backupNow", new { })).Ok);
        Assert.False(await rig.Bridge.BackupTick(startup: true));
        Assert.False((await rig.Bridge.RunBackup("manual")).Ok);
        Assert.Equal(damaged, File.ReadAllText(path));
        Assert.False(Directory.Exists(rig.Backups));
        // Unknown fields of a good file are kept when it is saved.
        File.WriteAllText(path, "{\"format\":1,\"folder\":null,\"keepCount\":14,\"keepDays\":30,\"intervalHours\":24,\"afterChangeMinutes\":10,\"includeNativeFiles\":true,\"lastResult\":null,\"fromTheFuture\":{\"x\":1}}");
        await rig.Restart();
        await rig.SetFolder();
        Assert.Contains("fromTheFuture", File.ReadAllText(path));
        Assert.Contains(rig.Backups.Replace("\\", "\\\\"), File.ReadAllText(path));
    }

    [Fact]
    public async Task BackupNeverDelaysPlayOrStop()
    {
        var files = new FaultyFiles();
        await using var rig = new BackupRig(NewRoot(), files);
        await rig.Host();
        // A 200 MB Soundpad file the voice library uses, so the run copies 200 MB.
        var big = Path.Combine(rig.Sounds, "big.wav");
        using (var f = new FileStream(big, FileMode.Create)) { var block = new byte[1 << 20]; new Random(1).NextBytes(block); for (int i = 0; i < 200; i++) f.Write(block); }
        rig.Bridge.VoiceLibraryStore.Add(new LibraryRecording { Path = big, Title = "Big", DurationMs = 1000, Source = new LibrarySource { Kind = "soundpad" } }, null, "host", "Host");
        await rig.SetFolder();
        var sound = rig.Bridge.Snapshot.Library.Sounds.First(s => s.Name == "Airhorn");
        async Task<double> Play()
        {
            var clock = Stopwatch.StartNew();
            var r = await rig.Bridge.Execute(BackupRig.Cmd("play") with { SoundId = sound.Id, LibraryVersion = rig.Bridge.Snapshot.Library.Version }, "host");
            Assert.True(r.Ok, r.Error);
            var ms = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            Assert.True((await rig.Bridge.Execute(BackupRig.Cmd("stop"), "host")).Ok);
            return ms + clock.Elapsed.TotalMilliseconds;
        }
        var idle = new List<double>();
        for (int i = 0; i < 20; i++) idle.Add(await Play());
        // During the copy: the copy's thread waits until a play and a stop have both been answered. If the copy held the
        // native gate (or any lock a play needs), the play could not finish and this would time out.
        var during = new List<double>(); int chunks = 0; long played = 0;
        files.OnChunk = (_, _) =>
        {
            if (Interlocked.Increment(ref chunks) % 10 != 1) return;
            var t = Task.Run(Play);
            Assert.True(t.Wait(TimeSpan.FromSeconds(10)), "a play waited for the backup's copy");
            lock (during) during.Add(t.Result);
            Interlocked.Increment(ref played);
        };
        var clock = Stopwatch.StartNew();
        var run = await rig.Backup();
        var seconds = clock.Elapsed.TotalSeconds;
        Assert.True(run.NewBytes >= 200L << 20);
        Assert.True(played >= 10);
        static double P(List<double> l, double q) { var s = l.Order().ToList(); return s[(int)Math.Min(s.Count - 1, Math.Floor(q * s.Count))]; }
        output.WriteLine($"play+stop idle: median {P(idle, 0.5):0.0} ms, p95 {P(idle, 0.95):0.0} ms, max {idle.Max():0.0} ms ({idle.Count})");
        output.WriteLine($"play+stop during a 200 MB backup: median {P(during, 0.5):0.0} ms, p95 {P(during, 0.95):0.0} ms, max {during.Max():0.0} ms ({during.Count}); backup {seconds:0.0} s");
    }

    [Fact]
    public async Task CheckpointWithoutFolderIsSkippedWithReason()
    {
        await using var rig = new BackupRig(NewRoot());
        await rig.Host();
        var checkpoint = await rig.Bridge.CheckpointH("pre-update", default);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Skipped);
        Assert.Equal("Backups are not set up.", checkpoint.Reason);
        // With a folder it is a real backup of that kind.
        await rig.SetFolder();
        var made = await rig.Bridge.CheckpointH("pre-organize", default);
        Assert.False(made!.Skipped);
        Assert.Equal("pre-organize", rig.Manifest(made.Id).Kind);
        Assert.True(Directory.Exists(made.Path));
    }
}
