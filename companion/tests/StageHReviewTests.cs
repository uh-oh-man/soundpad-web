using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// The 2.44 review: Organize recovery and plan integrity, the update / restore / Organize cross-checks, backup
/// retention during a restore, and the smaller data-safety fixes. Mock Soundpad and temporary folders only.
/// </summary>
[Collection("History")]
public sealed class StageHReviewTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-stageh-review-" + Guid.NewGuid().ToString("N"));
    public StageHReviewTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op, object? body = null) => OrganizeKit.Cmd(op, body);
    private static JsonElement B(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<(MockSoundpad Mock, SoundpadPipe Pipe, Bridge Bridge)> Start(Action<MockSoundpad>? setup = null)
    {
        var mock = new MockSoundpad { DuplicateOnAdd = false };
        setup?.Invoke(mock);
        var pipe = new SoundpadPipe(mock.Name, true);
        var bridge = new Bridge(pipe, new StateStore(folder)) { OrganizeQuietMs = 30 };
        await bridge.Poll(true);
        return (mock, pipe, bridge);
    }
    private static async Task Stop((MockSoundpad Mock, SoundpadPipe Pipe, Bridge Bridge) h) { h.Bridge.Dispose(); h.Pipe.Dispose(); await h.Mock.DisposeAsync(); }
    private static JsonElement Step(JsonElement view, string name) => OrganizeKit.Steps(view).Single(s => s.GetProperty("name").GetString() == name);
    private static string State(JsonElement view, string name) => Step(view, name).GetProperty("state").GetString()!;

    // ---- Organize ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStepSoundpadWentAwayDuringIsCheckedAgainWhenItAnswersWithTheSameLibrary()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge; b.StageHStarted();
            await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Alpha" }, new { kind = "createCategory", name = "Beta" });
            int calls = 0;
            b.UseBeforeOrganizeCommand(_ => Interlocked.Increment(ref calls) == 1 ? throw new IOException("pipe broke") : Task.CompletedTask);
            var paused = await OrganizeKit.Apply(b);
            Assert.Equal("applying", State(paused, "Alpha"));
            // Soundpad answers again; nothing in its library changed.
            await b.Poll(true);
            await OrganizeKit.Eventually(async () => State(await OrganizeKit.Get(b), "Alpha") == "pending");
            var again = await OrganizeKit.Apply(b);
            Assert.Equal("done", OrganizeKit.Plan(again).GetProperty("state").GetString());
            Assert.Equal("verified", State(again, "Alpha"));
            Assert.NotNull(OrganizeKit.Category(h.Mock, "Alpha"));
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task ApplyNeverSendsOverAnUncheckedStep()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge;
            await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Alpha" }, new { kind = "createCategory", name = "Beta" });
            int calls = 0;
            b.UseBeforeOrganizeCommand(_ => Interlocked.Increment(ref calls) == 1 ? throw new IOException("pipe broke") : Task.CompletedTask);
            var paused = await OrganizeKit.Apply(b);
            var plan = OrganizeKit.Plan(paused);
            // Apply at once (no refresh in between): the interrupted step is checked first, and nothing is sent.
            var apply = await b.Execute(Cmd("organizeApply", new { planId = plan.GetProperty("id").GetString(), planRevision = OrganizeKit.Revision(paused) }), "host");
            Assert.False(apply.Ok);
            Assert.Contains("checked against Soundpad again", apply.Error);
            Assert.DoesNotContain(OrganizeKit.Sent(h.Mock), x => x.StartsWith("DoAddCategory"));
            var view = await OrganizeKit.Get(b);
            Assert.Equal("pending", State(view, "Alpha"));
            Assert.Equal("draft", OrganizeKit.Plan(view).GetProperty("state").GetString());
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task TheCheckpointFromBeforeAPlanIsKeptThroughALaterApplyOfIt()
    {
        var lepp = Path.Combine(folder, "lepp"); Directory.CreateDirectory(lepp);
        File.WriteAllText(Path.Combine(lepp, "soundlist.spl"), "BEFORE-PLAN");
        var h = await Start(m => m.DuplicateOnAdd = true);
        try
        {
            var b = h.Bridge; b.UseLeppsoftFolder(lepp); var empty = OrganizeKit.Cat(b, "Empty category");
            await OrganizeKit.Edit(b, new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = empty, categoryName = "Empty category" }, new { kind = "addSound", soundId = OrganizeKit.Id(b, "Laugh"), categoryId = empty, categoryName = "Empty category" });
            var paused = await OrganizeKit.Apply(b);
            Assert.Equal("duplicates", OrganizeKit.Plan(paused).GetProperty("paused").GetString());
            var planId = OrganizeKit.Plan(paused).GetProperty("id").GetString()!;
            var checkpoints = Path.Combine(folder, "organize", "checkpoints");
            var json = File.ReadAllText(Path.Combine(checkpoints, planId + ".v1.json"));
            Assert.True(paused.GetProperty("checkpoint").GetProperty("takenAtUtcMs").GetInt64() > 0);
            // Soundpad saved its list since (with the first step's change); Continue applies the rest.
            File.WriteAllText(Path.Combine(lepp, "soundlist.spl"), "AFTER-FIRST-STEP");
            Assert.True((await b.Execute(Cmd("organizeResolve", new { resolution = "continue" }), "host")).Ok);
            await b.OrganizeApplyIdle().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("BEFORE-PLAN", File.ReadAllText(Path.Combine(checkpoints, planId + ".spl.bak")));
            Assert.Equal(json, File.ReadAllText(Path.Combine(checkpoints, planId + ".v1.json")));
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task ARefusedEditLeavesThePlanAsItWas()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge;
            for (int batch = 0; batch < 4; batch++)
                await OrganizeKit.Edit(b, Enumerable.Range(0, batch < 3 ? 50 : 40).Select(i => (object)new { kind = "createCategory", name = $"C{batch}-{i}" }).ToArray());
            var view = await OrganizeKit.Get(b);
            long revision = OrganizeKit.Revision(view);
            var refused = await b.Execute(Cmd("organizeEdit", new { planRevision = revision, add = Enumerable.Range(0, 20).Select(i => new { kind = "createCategory", name = $"Refused-{i}" }).ToArray() }), "host");
            Assert.False(refused.Ok);
            Assert.Contains("at most 200", refused.Error);
            view = await OrganizeKit.Get(b);
            Assert.Equal(190, OrganizeKit.Steps(view).Length);
            Assert.Equal(revision, OrganizeKit.Revision(view));
            var ids = OrganizeKit.Steps(view).Where(s => s.GetProperty("name").GetString()!.StartsWith("C0-")).Take(15).Select(s => s.GetProperty("id").GetString()).ToArray();
            Assert.True((await b.Execute(Cmd("organizeEdit", new { planRevision = revision, remove = ids }), "host")).Ok);
            var disk = new OrganizeFiles(folder).LoadPlan().Plan!;
            Assert.Equal(175, disk.Steps.Count);
            Assert.DoesNotContain(disk.Steps, s => s.Name!.StartsWith("Refused"));
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task GuestsCanNotFillThePlanPastItsLimitOrJamTheHost()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge;
            Assert.True((await b.Execute(Cmd("organizeSettings", new { guestOrganize = true }), "host")).Ok);
            int ok = 0; string? last = null;
            for (int g = 0; g < 11; g++)
            {
                var guest = new Participant(Guid.NewGuid().ToString(), "Friend" + g);
                for (int i = 0; i < 20; i++)
                {
                    var r = await b.Execute(Cmd("organizePropose", new { steps = new[] { new { kind = "createCategory", name = $"G{g}-{i}" } } }), "guest", guest, "peer-" + g);
                    if (r.Ok) ok++; else last = r.Error;
                }
            }
            Assert.Equal(OrganizeFiles.MaxProposals, ok);
            Assert.Contains("too many proposals", last);
            var view = await OrganizeKit.Get(b);
            Assert.Equal(OrganizeFiles.MaxProposals, view.GetProperty("proposals").GetArrayLength());
            Assert.True((await b.Execute(Cmd("organizeEdit", new { planRevision = OrganizeKit.Revision(view), add = new[] { new { kind = "createCategory", name = "Host one" } } }), "host")).Ok);
            Assert.True((await b.Execute(Cmd("organizeDiscard"), "host")).Ok);
            Assert.Equal(OrganizeFiles.MaxProposals, new OrganizeFiles(folder).LoadPlan().Plan!.Proposals.Count);
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task AnEditKeepsTheDuplicatesQuestionForTheHost()
    {
        var h = await Start(m => m.DuplicateOnAdd = true);
        try
        {
            var b = h.Bridge; var empty = OrganizeKit.Cat(b, "Empty category");
            await OrganizeKit.Edit(b, new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = empty, categoryName = "Empty category" }, new { kind = "addSound", soundId = OrganizeKit.Id(b, "Laugh"), categoryId = empty, categoryName = "Empty category" });
            var paused = await OrganizeKit.Apply(b);
            Assert.Equal("duplicates", OrganizeKit.Plan(paused).GetProperty("paused").GetString());
            var edited = await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Another" });
            Assert.Equal(("paused", "duplicates"), (OrganizeKit.Plan(edited).GetProperty("state").GetString(), OrganizeKit.Plan(edited).GetProperty("paused").GetString()));
            var stop = await b.Execute(Cmd("organizeResolve", new { resolution = "stop" }), "host");
            Assert.True(stop.Ok, stop.Error);
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task AStaleCategoryIdThatNowNamesACategoryElsewhereIsRefused()
    {
        var h = await Start(m => m.CategoryXml = "<Categories><Category index=\"2\" name=\"A\"><Category index=\"3\" name=\"Old\"/></Category><Category index=\"4\" name=\"Old\"/></Categories>");
        try
        {
            var b = h.Bridge;
            // The page saw the nested "Old" (index 3, inside "A"); Soundpad renumbered, and index 3 is now the top-level one.
            h.Mock.CategoryXml = "<Categories><Category index=\"2\" name=\"A\"/><Category index=\"3\" name=\"Old\"/></Categories>";
            var view = await OrganizeKit.Get(b);
            var stale = await b.Execute(Cmd("organizeEdit", new { planRevision = OrganizeKit.Revision(view), add = new[] { new { kind = "removeCategory", categoryId = "3", categoryName = "Old", categoryParents = new[] { "A" } } } }), "host");
            Assert.False(stale.Ok);
            Assert.Contains("categories changed", stale.Error);
            var fresh = await b.Execute(Cmd("organizeEdit", new { planRevision = OrganizeKit.Revision(view), add = new[] { new { kind = "removeCategory", categoryId = "3", categoryName = "Old", categoryParents = Array.Empty<string>() } } }), "host");
            Assert.True(fresh.Ok, fresh.Error);
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task RemovingCreatedCategoriesGivesUpWhileSoundpadKeepsPlaying()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge; b.OrganizeRemoveWaitMs = 700;
            await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Made one" }, new { kind = "createCategory", name = "Made two" });
            b.UseBeforeOrganizeCommand(kind => { h.Mock.RejectPrefix = "DoAddCategory(\"Made two"; return Task.CompletedTask; });
            await OrganizeKit.Apply(b);
            h.Mock.RejectPrefix = null;
            h.Mock.DurationMs = 600000; h.Mock.BeginFixturePlayback();
            var clock = Stopwatch.StartNew();
            var reply = await b.Execute(Cmd("organizeResolve", new { resolution = "removeCreated" }), "host").WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(reply.Ok, reply.Error);
            Assert.Equal((0, 1), (OrganizeKit.Data(reply).GetProperty("removed").GetInt32(), OrganizeKit.Data(reply).GetProperty("kept").GetInt32()));
            Assert.True(clock.ElapsedMilliseconds < 10000);
            Assert.NotNull(OrganizeKit.Category(h.Mock, "Made one"));
        }
        finally { await Stop(h); }
    }

    // ---- Update, whole-library restore and Organize apply never overlap ------------------------------------------

    private static Command Install() => Cmd("updateInstall") with { Body = B("""{"version":"2.44.1","confirm":true}""") };

    [Fact]
    public async Task WhileAnUpdateInstallsApplyRemoveAndAWholeLibraryRestoreAreRefused()
    {
        await using var rig = new BackupRig(Path.Combine(folder, "rig"));
        var b = rig.Bridge; b.OrganizeQuietMs = 30; b.StageHStarted();
        await b.Poll(true);
        await rig.SetFolder();
        var run = await rig.Backup();
        await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Alpha" });
        var view = await OrganizeKit.Get(b);
        var exits = new TaskCompletionSource();
        b.UpdateExitRequested += _ => { exits.TrySetResult(); return Task.CompletedTask; };
        b.Updates.Force(new("ready", "", "2.44.1"));
        await rig.Ok(Install());
        await exits.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("installing an update", (await b.Execute(Cmd("organizeApply", new { planId = OrganizeKit.Plan(view).GetProperty("id").GetString(), planRevision = OrganizeKit.Revision(view) }), "host")).Error);
        Assert.Contains("installing an update", (await b.Execute(Cmd("organizeResolve", new { resolution = "removeCreated" }), "host")).Error);
        Assert.Contains("installing an update", (await rig.Op("backupRestore", new { snapshot = run.Id, kind = "library" })).Error);
        Assert.DoesNotContain(OrganizeKit.Sent(rig.Mock), x => x.StartsWith("DoAddCategory"));
    }

    [Fact]
    public async Task WhileApplyIsStartingAnUpdateAndAWholeLibraryRestoreAreRefused()
    {
        await using var rig = new BackupRig(Path.Combine(folder, "rig"));
        var b = rig.Bridge; b.OrganizeQuietMs = 30; b.StageHStarted();
        await b.Poll(true);
        await rig.SetFolder();
        var run = await rig.Backup();
        await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Alpha" });
        var view = await OrganizeKit.Get(b);
        var held = new TaskCompletionSource(); var release = new TaskCompletionSource();
        b.BackupHold = async _ => { held.TrySetResult(); await release.Task; };
        // Apply's "before organizing" backup is held, so Apply is still starting.
        var apply = b.Execute(Cmd("organizeApply", new { planId = OrganizeKit.Plan(view).GetProperty("id").GetString(), planRevision = OrganizeKit.Revision(view) }), "host");
        await held.Task.WaitAsync(TimeSpan.FromSeconds(20));
        b.Updates.Force(new("available", "", "2.44.1"));
        Assert.Contains("organization plan", (await b.Execute(Install(), "host")).Error);
        Assert.Contains("organization plan", (await rig.Op("backupRestore", new { snapshot = run.Id, kind = "library" })).Error);
        // The host's plan can't be edited under it either.
        Assert.False((await b.Execute(Cmd("organizeEdit", new { planRevision = OrganizeKit.Revision(view), add = new[] { new { kind = "createCategory", name = "Late" } } }), "host")).Ok);
        b.BackupHold = null; release.TrySetResult();
        var started = await apply.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(started.Ok, started.Error);
        await b.OrganizeApplyIdle().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(OrganizeKit.Category(rig.Mock, "Alpha"));
        Assert.Null(OrganizeKit.Category(rig.Mock, "Late"));
    }

    [Fact]
    public async Task WhileAWholeLibraryRestoreMakesItsBackupAnUpdateAndApplyAreRefused()
    {
        await using var rig = new BackupRig(Path.Combine(folder, "rig"));
        var b = rig.Bridge; b.OrganizeQuietMs = 30; b.StageHStarted();
        await b.Poll(true);
        await rig.SetFolder();
        var run = await rig.Backup();
        await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Alpha" });
        var view = await OrganizeKit.Get(b);
        var held = new TaskCompletionSource(); var release = new TaskCompletionSource();
        b.BackupHold = async _ => { held.TrySetResult(); await release.Task; };
        rig.Now = rig.Now.AddMinutes(5);
        var restore = rig.Op("backupRestore", new { snapshot = run.Id, kind = "library" });
        await held.Task.WaitAsync(TimeSpan.FromSeconds(20));
        b.Updates.Force(new("available", "", "2.44.1"));
        Assert.Contains("restoring the library", (await b.Execute(Install(), "host")).Error);
        Assert.Contains("restoring the library", (await b.Execute(Cmd("organizeApply", new { planId = OrganizeKit.Plan(view).GetProperty("id").GetString(), planRevision = OrganizeKit.Revision(view) }), "host")).Error);
        b.BackupHold = null; release.TrySetResult();
        var done = await restore.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(done.Ok, done.Error);
        Assert.Null(OrganizeKit.Category(rig.Mock, "Alpha"));
    }

    // ---- Backups -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RestoringFromABackupNeverRetiresThatBackupOrItsFiles()
    {
        await using var rig = new BackupRig(Path.Combine(folder, "rig"));
        await rig.Host();
        var asset = await rig.KeepClip("Only here");
        await rig.SetFolder(keepCount: 2);
        var variant = rig.KeptVariant(asset);
        var bytes = File.ReadAllBytes(variant.Path);
        var first = await rig.Backup();
        // The clip is deleted; a later backup the same week no longer has it, so only the first one does.
        File.Delete(variant.Path);
        rig.Now = rig.Now.AddMinutes(30);
        await rig.Backup();
        // Weeks later: the first backup is now the oldest, past keepDays and not its week's newest. The restore's
        // own "before restore" backup would push it out of the newest two.
        rig.Now = rig.Now.AddDays(40);
        var reply = await rig.OkOp("backupRestore", new { snapshot = first.Id, kind = "clip", ids = new[] { variant.Id } });
        Assert.Equal(0, reply.GetProperty("conflicts").GetArrayLength());
        Assert.Equal(bytes, File.ReadAllBytes(variant.Path));
    }

    /// <summary>Deleting a snapshot's data files fails (an antivirus holds them); its manifest can still be deleted.</summary>
    private sealed class StuckDataFiles : BackupFiles
    {
        public bool Stuck;
        public override void Delete(string path)
        {
            if (Stuck && path.Contains(Path.DirectorySeparatorChar + "data" + Path.DirectorySeparatorChar)) throw new IOException("in use");
            base.Delete(path);
        }
    }

    [Fact]
    public async Task RetentionThatIsCutShortNeverLeavesASnapshotWithoutItsManifest()
    {
        var files = new StuckDataFiles();
        await using var rig = new BackupRig(Path.Combine(folder, "rig"), files);
        await rig.Host();
        await rig.SetFolder(keepCount: 1, keepDays: 1);
        // Two the same week: the older one is not its week's newest, so retention removes it later.
        await rig.Backup();
        rig.Now = rig.Now.AddMinutes(30);
        await rig.Backup();
        rig.Now = rig.Now.AddDays(10);
        files.Stuck = true;
        var second = await rig.Backup();
        Assert.True(second.Ok);
        var repo = rig.Repo;
        Assert.All(repo.SnapshotFolders(), name => Assert.NotNull(repo.Read(name, out _)));
        files.Stuck = false;
        rig.Now = rig.Now.AddDays(10);
        var third = await rig.Backup();
        Assert.DoesNotContain(third.Warnings, w => w.Contains("could not be read"));
        Assert.Equal(3, repo.SnapshotFolders().Length); // one per week, and the newest
        Assert.Empty(Directory.GetDirectories(Path.Combine(rig.Install, "snapshots"), ".partial-*"));
    }

    [Fact]
    public async Task AFailedScheduledBackupIsTriedAgainWithinTheHour()
    {
        var files = new FaultyFiles();
        await using var rig = new BackupRig(Path.Combine(folder, "rig"), files);
        await rig.Host();
        await rig.SetFolder();
        await rig.Backup();
        // The backup drive has no room (or is missing); the scheduled backup fails.
        files.Free = 1;
        rig.Now = rig.Now.AddHours(25);
        Assert.False(await rig.Bridge.BackupTick());
        // It has room again. An hour later the schedule tries again (not a whole interval later).
        files.Free = null;
        rig.Now = rig.Now.AddMinutes(30);
        Assert.False(await rig.Bridge.BackupTick());
        rig.Now = rig.Now.AddMinutes(31);
        Assert.True(await rig.Bridge.BackupTick());
    }

    [Fact]
    public void SettingsFilesFromABackupAreCheckedBeforeTheyAreWritten()
    {
        Assert.True(Bridge.SettingsDocUsable("ui", "{\"version\":1,\"theme\":\"dark\"}"u8.ToArray()));
        Assert.True(Bridge.SettingsDocUsable("keep", "{\"format\":1,\"guestKeep\":false}"u8.ToArray()));
        Assert.True(Bridge.SettingsDocUsable("mimic", "{\"format\":1,\"historySeconds\":60}"u8.ToArray()));
        Assert.False(Bridge.SettingsDocUsable("ui", "{\"version\":1,"u8.ToArray()));
        Assert.False(Bridge.SettingsDocUsable("keep", "[1,2]"u8.ToArray()));
        Assert.False(Bridge.SettingsDocUsable("mimic", "{\"historySeconds\":\"long\"}"u8.ToArray()));
        Assert.False(Bridge.SettingsDocUsable("ui", new byte[UiPrefsStore.MaxBytes + 1]));
    }

    [Fact]
    public async Task AWholeLibraryRestoreKeepsAGoodSettingsFileOverADamagedBackupCopy()
    {
        await using var rig = new BackupRig(Path.Combine(folder, "rig"));
        await rig.Host();
        await rig.SetFolder();
        var ui = Path.Combine(rig.Data, "ui.json");
        File.WriteAllText(ui, "{\"version\":1,");
        var run = await rig.Backup();
        File.WriteAllText(ui, "{\"version\":1}");
        rig.Now = rig.Now.AddMinutes(10);
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "library" });
        Assert.Equal("{\"version\":1}", File.ReadAllText(ui));
        Assert.Contains(reply.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("ui.json is damaged"));
    }

    [Fact]
    public async Task ARestoreWritesOnlyAudioOfTheBackupsTypeToTheCopysPath()
    {
        await using var rig = new BackupRig(Path.Combine(folder, "rig"));
        await rig.Host();
        var asset = await rig.KeepClip("Kept line");
        await rig.SetFolder();
        var variant = rig.KeptVariant(asset);
        var run = await rig.Backup();
        // A doctored backup: the kept clip's path now names a script in a startup folder.
        var snapshot = Path.Combine(rig.Repo.SnapshotPath(run.Id!));
        var manifest = rig.Manifest(run.Id!);
        var doc = manifest.Files.Single(f => f.Role == "journal");
        var journalPath = Path.Combine(snapshot, "data", doc.Path);
        var target = Path.Combine(folder, "startup", "x.bat");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var original = File.ReadAllText(journalPath);
        var text = original.Replace(JsonSerializer.Serialize(variant.Path).Trim('"'), JsonSerializer.Serialize(target).Trim('"'));
        Assert.NotEqual(original, text);
        File.WriteAllText(journalPath, text);
        var bytes = File.ReadAllBytes(journalPath);
        doc.Sha256 = BackupRepository.Sha(bytes); doc.Bytes = bytes.Length;
        File.WriteAllBytes(Path.Combine(snapshot, BackupManifest.FileName), JsonSerializer.SerializeToUtf8Bytes(manifest, SoundpadWeb.Json.Options));
        File.Delete(variant.Path);
        var reply = await rig.OkOp("backupRestore", new { snapshot = run.Id, kind = "clip", ids = new[] { variant.Id } });
        Assert.Contains(reply.GetProperty("conflicts").EnumerateArray(), c => c.GetString()!.Contains("not an audio file"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void TheUnreadableStateHintOffersOnlyThisInstallsBackups()
    {
        var data = Path.Combine(folder, "data"); var backups = Path.Combine(folder, "backups");
        Directory.CreateDirectory(Path.Combine(data, "managed-audio"));
        File.WriteAllText(Path.Combine(data, "backup-settings.v1.json"), JsonSerializer.Serialize(new { format = 1, folder = backups, keepCount = 14, keepDays = 30, intervalHours = 24, afterChangeMinutes = 10, includeNativeFiles = true, lastResult = (object?)null }));
        var root = BackupFolders.Root(backups);
        string Snapshot(string install, string id)
        {
            var snapshot = Path.Combine(root, install, "snapshots", id);
            Directory.CreateDirectory(Path.Combine(snapshot, "data"));
            var state = JsonSerializer.SerializeToUtf8Bytes(new DiskState { SchemaVersion = 2, IdentityKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }, SoundpadWeb.Json.Options);
            File.WriteAllBytes(Path.Combine(snapshot, "data", "state.json"), state);
            var manifest = new BackupManifest { Id = id, Kind = "manual", CreatedUtcMs = 1, AppId = install[..6], HostId = new string('a', 64), Files = [new BackupDoc { Role = "state", Path = "state.json", Sha256 = BackupRepository.Sha(state), Bytes = state.Length }], Blobs = [] };
            File.WriteAllBytes(Path.Combine(snapshot, BackupManifest.FileName), JsonSerializer.SerializeToUtf8Bytes(manifest, SoundpadWeb.Json.Options));
            return snapshot;
        }
        var other = Snapshot("bbbbbb-22222222", "20261114T120000Z-2b2b2b2b");
        // No readable app id: offered only while the folder holds a single install's backups.
        Assert.Equal(other, BackupLocator.NewestState(data));
        var third = Snapshot("cccccc-33333333", "20261114T130000Z-3c3c3c3c");
        Assert.Null(BackupLocator.NewestState(data));
        Assert.Equal("", BackupLocator.Hint(data));
        // This install's app id is known but it has no backup yet: still not the other one.
        File.WriteAllText(Path.Combine(data, "managed-audio", "journal.json"), "{\"appId\":\"aaaaaa0000\"}");
        Assert.Null(BackupLocator.NewestState(data));
        var mine = Snapshot("aaaaaa-11111111", "20261114T110000Z-1a1a1a1a");
        Assert.Equal(mine, BackupLocator.NewestState(data));
        Assert.Equal(mine, BackupLocator.NewestState(data)); // never the newer backups of the other installs
    }

    // ---- Updates -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDownloadedUpdateIsCheckedAgainRightBeforeTheSwap()
    {
        var exe = Path.Combine(folder, "SoundpadWeb.exe");
        File.WriteAllText(exe, "the running version");
        var image = File.ReadAllBytes(typeof(Bridge).Assembly.Location);
        var product = FileVersionInfo.GetVersionInfo(typeof(Bridge).Assembly.Location).ProductVersion!.Split('+')[0];
        var hash = Convert.ToHexString(SHA256.HashData(image));
        var source = UpdateSource.ForTest("http://127.0.0.1:9/", out _)!;
        var handler = new CannedHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/releases/latest" => CannedHandler.Json($$"""{"tag_name":"v{{product}}","assets":[{"name":"SoundpadWeb.exe","size":{{image.Length}},"browser_download_url":"http://127.0.0.1:9/releases/download/v{{product}}/SoundpadWeb.exe"},{"name":"SHA256.txt","browser_download_url":"http://127.0.0.1:9/releases/download/v{{product}}/SHA256.txt"}]}"""),
            var p when p.EndsWith("SHA256.txt") => CannedHandler.Json(hash + "\r\n"),
            _ => CannedHandler.Bytes(image),
        });
        using var updater = new Updater(new UpdateOptions
        {
            Current = "1.0.0", Source = source, ExePath = exe, DataDir = folder, Handler = handler, LocationProblem = _ => null,
            Probe = (_, _) => Task.FromResult((0, "MIT License\nCopyright (c) 2017 Leppsoft")),
        });
        Assert.True(await updater.Check());
        Assert.True(await updater.Download(product));
        Assert.True(updater.StillVerified());
        var path = updater.DownloadPath!;
        // The file changed while it waited (after Later, say): refused, and the download is removed.
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Write)) { f.Position = 100; f.WriteByte(0x42); }
        Assert.False(updater.StillVerified());
        Assert.False(File.Exists(path));
        Assert.Null(updater.DownloadPath);
    }
}
