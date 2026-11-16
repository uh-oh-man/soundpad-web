using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Shared steps of the Organize tests (2.44).</summary>
internal static class OrganizeKit
{
    public static Command Cmd(string op, object? body = null) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op) { Body = body == null ? null : JsonSerializer.SerializeToElement(body, Json.Options) };
    public static JsonElement Data(Reply reply) => JsonSerializer.SerializeToElement(reply.Data ?? new { }, Json.Options);
    public static async Task<JsonElement> Get(Bridge bridge, string actor = "host", Participant? p = null) { var r = await bridge.Execute(Cmd("organizeGet"), actor, p); Assert.True(r.Ok, r.Error); return Data(r); }
    public static long Revision(JsonElement view) => view.GetProperty("revision").GetInt64();
    public static JsonElement Plan(JsonElement view) => view.GetProperty("plan");
    public static JsonElement[] Steps(JsonElement view) => [.. Plan(view).GetProperty("steps").EnumerateArray()];
    public static string Id(Bridge bridge, string name) => bridge.Snapshot.Library.Sounds.First(s => s.Name == name).Id;
    public static string Cat(Bridge bridge, string name) => Flatten(bridge.Snapshot.Library.Categories).First(c => c.Name == name).Id;
    public static IEnumerable<Category> Flatten(IEnumerable<Category> cats) => cats.SelectMany(c => Flatten(c.Children).Prepend(c));
    public static async Task<JsonElement> Edit(Bridge bridge, params object[] add)
    {
        var view = await Get(bridge);
        var reply = await bridge.Execute(Cmd("organizeEdit", new { planRevision = Revision(view), add }), "host");
        Assert.True(reply.Ok, reply.Error);
        return Data(reply);
    }
    /// <summary>Applies the plan and waits until Apply has finished or paused.</summary>
    public static async Task<JsonElement> Apply(Bridge bridge)
    {
        var view = await Get(bridge);
        var reply = await bridge.Execute(Cmd("organizeApply", new { planId = Plan(view).GetProperty("id").GetString(), planRevision = Revision(view) }), "host");
        Assert.True(reply.Ok, reply.Error);
        await bridge.OrganizeApplyIdle().WaitAsync(TimeSpan.FromSeconds(30));
        return await Get(bridge);
    }
    public static XDocument Cats(MockSoundpad mock) => XDocument.Parse(mock.CategoryXml);
    public static XElement? Category(MockSoundpad mock, string name) => Cats(mock).Descendants("Category").FirstOrDefault(e => (string?)e.Attribute("name") == name);
    public static string[] Sent(MockSoundpad mock) { lock (mock.Commands) return [.. mock.Commands]; }
    public static async Task Eventually(Func<Task<bool>> condition, int ms = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition()) { if (clock.ElapsedMilliseconds > ms) throw new TimeoutException("The condition was not met in time."); await Task.Delay(50); }
    }
}

/// <summary>
/// 2.44 Organize: a plan of category changes, applied to Soundpad only where Soundpad can do it, each step behind the
/// quiet rule, re-checked before and verified after; guests propose, only the host applies.
/// </summary>
public sealed class OrganizeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-organize-test-" + Guid.NewGuid().ToString("N"));
    public OrganizeTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op, object? body = null) => OrganizeKit.Cmd(op, body);

    private async Task<(MockSoundpad Mock, SoundpadPipe Pipe, Bridge Bridge)> Start(Action<MockSoundpad>? setup = null, int quietMs = 30)
    {
        var mock = new MockSoundpad { DuplicateOnAdd = false };
        setup?.Invoke(mock);
        var pipe = new SoundpadPipe(mock.Name, true);
        var bridge = new Bridge(pipe, new StateStore(folder)) { OrganizeQuietMs = quietMs };
        await bridge.Poll(true);
        return (mock, pipe, bridge);
    }
    private static async Task Stop((MockSoundpad Mock, SoundpadPipe Pipe, Bridge Bridge) h) { h.Bridge.Dispose(); h.Pipe.Dispose(); await h.Mock.DisposeAsync(); }

    [Fact]
    public void PlanFileRoundTripsWithinLimitsAndADamagedOrNewerFileIsKept()
    {
        var files = new OrganizeFiles(folder);
        var plan = new OrganizePlan();
        plan.Steps.Add(new OrganizeStep { Kind = "createCategory", Name = "Loud ones" });
        plan.Steps.Add(new OrganizeStep { Kind = "addSound", SoundPath = @"C:\Private\airhorn.wav", SoundTitle = "Airhorn", TargetStepId = plan.Steps[0].Id });
        files.SavePlan(plan);
        var back = files.LoadPlan();
        Assert.Null(back.Problem); Assert.Equal(plan.Id, back.Plan!.Id); Assert.Equal(2, back.Plan.Steps.Count);
        // Limits: 201 steps, a name Soundpad can't take, a quote.
        var big = new OrganizePlan(); for (int i = 0; i < 201; i++) big.Steps.Add(new OrganizeStep { Kind = "createCategory", Name = "c" + i });
        Assert.Throws<InvalidDataException>(() => files.SavePlan(big));
        Assert.NotNull(OrganizeFiles.NameProblem("Smile \U0001F600"));
        Assert.NotNull(OrganizeFiles.NameProblem("Say \"hi\""));
        Assert.NotNull(OrganizeFiles.NameProblem(new string('a', 81)));
        Assert.Null(OrganizeFiles.NameProblem("Memes (2024), loud"));
        // An unknown field: damaged, renamed aside, never rewritten silently.
        File.WriteAllText(files.PlanPath, File.ReadAllText(files.PlanPath).Replace("\"revision\"", "\"surprise\":1,\"revision\""));
        var damaged = files.LoadPlan();
        Assert.Null(damaged.Plan); Assert.Contains("damaged", damaged.Problem); Assert.False(damaged.Newer);
        Assert.False(File.Exists(files.PlanPath)); Assert.Single(Directory.GetFiles(files.Folder, "plan.v1.corrupt-*.json"));
        // A newer version: left exactly as it is.
        File.WriteAllText(files.PlanPath, "{\"v\":2,\"anything\":true}");
        var newer = files.LoadPlan();
        Assert.True(newer.Newer); Assert.Null(newer.Plan); Assert.Equal("{\"v\":2,\"anything\":true}", File.ReadAllText(files.PlanPath));
        // Settings: damaged means off and untouched; unknown fields survive a write.
        File.WriteAllText(files.SettingsPath, "{\"format\":1,\"guestOrganize\":\"yes\"}");
        var fresh = new OrganizeFiles(folder);
        Assert.False(fresh.ReadSettings().GuestOrganize); Assert.True(fresh.ReadSettings().Damaged);
        Assert.Equal("{\"format\":1,\"guestOrganize\":\"yes\"}", File.ReadAllText(files.SettingsPath));
        File.WriteAllText(files.SettingsPath, "{\"format\":1,\"guestOrganize\":false,\"future\":[1,2]}");
        var kept = new OrganizeFiles(folder); kept.UpdateSettings(guestOrganize: true);
        Assert.Contains("\"future\":[1,2]", File.ReadAllText(files.SettingsPath)); Assert.True(new OrganizeFiles(folder).ReadSettings().GuestOrganize);
    }

    [Theory]
    [InlineData(MockSoundpad.Indexing.Append)]
    [InlineData(MockSoundpad.Indexing.DepthFirst)]
    public async Task EachNativeStepIsVerifiedWhateverTheIndexing(MockSoundpad.Indexing indexing)
    {
        var h = await Start(m => m.NestedIndexing = indexing);
        try
        {
            var b = h.Bridge;
            await OrganizeKit.Edit(b,
                new { kind = "createCategory", name = "Loud ones", key = "loud" },
                new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), targetKey = "loud" },
                new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = OrganizeKit.Cat(b, "Empty category"), categoryName = "Empty category" },
                new { kind = "removeCategory", categoryId = OrganizeKit.Cat(b, "Empty category"), categoryName = "Empty category" });
            var done = await OrganizeKit.Apply(b);
            var steps = OrganizeKit.Steps(done);
            // The removal stays for a later Apply: the category is no longer empty.
            Assert.Equal("draft", OrganizeKit.Plan(done).GetProperty("state").GetString());
            Assert.Equal(["verified", "verified", "verified", "pending"], steps.Select(s => s.GetProperty("state").GetString()));
            // The removal waits: the category is no longer empty (an earlier step added to it).
            Assert.Equal("manual", steps[3].GetProperty("support").GetString());
            var loud = OrganizeKit.Category(h.Mock, "Loud ones")!;
            Assert.Single(loud.Elements("Sound")); Assert.Contains("airhorn", (string)loud.Element("Sound")!.Attribute("url")!);
            Assert.Single(OrganizeKit.Category(h.Mock, "Empty category")!.Elements("Sound"));
            Assert.Single(XDocument.Parse(h.Mock.AllXml).Root!.Elements("Sound"), e => ((string)e.Attribute("url")!).EndsWith("airhorn.wav"));
            Assert.DoesNotContain(OrganizeKit.Sent(h.Mock), c => c.StartsWith("DoRemoveCategory("));
            h.Mock.AssertNoForbiddenCommands(); Assert.Equal(0, h.Mock.RiskyRemovals);
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task AnEmptyTopLevelCategoryIsRemovedAndANonEmptyOneNever()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge;
            await OrganizeKit.Edit(b,
                new { kind = "removeCategory", categoryId = OrganizeKit.Cat(b, "Favorites"), categoryName = "Favorites" },
                new { kind = "removeCategory", categoryId = OrganizeKit.Cat(b, "Empty category"), categoryName = "Empty category" },
                new { kind = "removeCategory", categoryId = OrganizeKit.Cat(b, "Deep cuts"), categoryName = "Deep cuts" });
            var view = await OrganizeKit.Get(b);
            Assert.Equal(["manual", "native", "manual"], OrganizeKit.Steps(view).Select(s => s.GetProperty("support").GetString()));
            var after = await OrganizeKit.Apply(b);
            Assert.Null(OrganizeKit.Category(h.Mock, "Empty category"));
            Assert.NotNull(OrganizeKit.Category(h.Mock, "Favorites")); Assert.NotNull(OrganizeKit.Category(h.Mock, "Deep cuts"));
            Assert.Single(OrganizeKit.Sent(h.Mock), c => c.StartsWith("DoRemoveCategory("));
            Assert.Equal(4, XDocument.Parse(h.Mock.AllXml).Root!.Elements("Sound").Count());
            Assert.Equal("pending", OrganizeKit.Steps(after)[0].GetProperty("state").GetString());
            Assert.StartsWith("Waiting:", OrganizeKit.Steps(after)[0].GetProperty("note").GetString());
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task ARefusedOrUnexpectedResultPausesAndLagSettles()
    {
        var h = await Start(m => m.ApplyLagReads = 3);
        try
        {
            var b = h.Bridge;
            // Lag: Soundpad shows the change only after three reads; it is still verified.
            await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Lagged" });
            Assert.Equal("verified", OrganizeKit.Steps(await OrganizeKit.Apply(b))[0].GetProperty("state").GetString());
            h.Mock.ApplyLagReads = 0;
            // Refused: failed, plan paused and kept.
            await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Refused" });
            h.Mock.RejectPrefix = "DoAddCategory(";
            var refused = await OrganizeKit.Apply(b);
            Assert.Equal("paused", OrganizeKit.Plan(refused).GetProperty("state").GetString());
            Assert.Equal("failed", OrganizeKit.Steps(refused).Single(s => s.GetProperty("name").GetString() == "Refused").GetProperty("state").GetString());
            Assert.True(File.Exists(Path.Combine(folder, "organize", "plan.v1.json")));
            // Not the expected change: someone added a category at the same moment.
            h.Mock.RejectPrefix = null;
            var step = OrganizeKit.Steps(refused).Single(s => s.GetProperty("name").GetString() == "Refused").GetProperty("id").GetString();
            Assert.True((await b.Execute(Cmd("organizeResolve", new { resolution = "retry", stepId = step }), "host")).Ok);
            b.UseBeforeOrganizeCommand(_ => { var c = OrganizeKit.Cats(h.Mock); c.Root!.Add(new XElement("Category", new XAttribute("index", 40), new XAttribute("name", "Meanwhile"))); h.Mock.CategoryXml = c.ToString(); return Task.CompletedTask; });
            var odd = await OrganizeKit.Apply(b);
            Assert.Equal("paused", OrganizeKit.Plan(odd).GetProperty("state").GetString());
            Assert.Equal("failed", OrganizeKit.Steps(odd).Single(s => s.GetProperty("name").GetString() == "Refused").GetProperty("state").GetString());
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task DriftElsewhereIsFineAndAConflictPausesKeepsThePlanAndRebinds()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge;
            await OrganizeKit.Edit(b, new { kind = "addSound", soundId = OrganizeKit.Id(b, "An extremely long sound title that should wrap gracefully on every single phone screen without breaking the grid"), categoryId = OrganizeKit.Cat(b, "Favorites"), categoryName = "Favorites" });
            // Drift that does not touch the step: a new category elsewhere.
            var c = OrganizeKit.Cats(h.Mock); c.Root!.Add(new XElement("Category", new XAttribute("index", 9), new XAttribute("name", "Unrelated"))); h.Mock.CategoryXml = c.ToString();
            await b.Poll(true);
            Assert.Equal("verified", OrganizeKit.Steps(await OrganizeKit.Apply(b))[0].GetProperty("state").GetString());
            // A conflict: the target's contents changed after the preview.
            await OrganizeKit.Edit(b, new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = OrganizeKit.Cat(b, "Empty category"), categoryName = "Empty category" });
            c = OrganizeKit.Cats(h.Mock); c.Descendants("Category").Single(e => (string?)e.Attribute("name") == "Empty category").Add(new XElement("Sound", new XAttribute("index", 2), new XAttribute("url", @"C:\Private\laugh.wav"))); h.Mock.CategoryXml = c.ToString();
            await b.Poll(true);
            var paused = await OrganizeKit.Apply(b);
            Assert.Equal("paused", OrganizeKit.Plan(paused).GetProperty("state").GetString());
            var conflict = OrganizeKit.Steps(paused).Single(s => s.GetProperty("state").GetString() == "conflict");
            Assert.Equal(OrganizeKit.Cat(b, "Empty category"), conflict.GetProperty("candidate").GetProperty("categoryId").GetString());
            Assert.DoesNotContain(OrganizeKit.Sent(h.Mock), x => x.StartsWith("DoAddSound(") && x.Contains("airhorn"));
            Assert.True(File.Exists(Path.Combine(folder, "organize", "plan.v1.json")));
            // Apply refuses until it is resolved; Rebind to the same category, then Apply.
            var view = await OrganizeKit.Get(b);
            Assert.False((await b.Execute(Cmd("organizeApply", new { planId = OrganizeKit.Plan(view).GetProperty("id").GetString(), planRevision = OrganizeKit.Revision(view) }), "host")).Ok);
            Assert.True((await b.Execute(Cmd("organizeResolve", new { resolution = "rebind", stepId = conflict.GetProperty("id").GetString(), targetCategoryId = OrganizeKit.Cat(b, "Empty category"), targetCategoryName = "Empty category" }), "host")).Ok);
            var applied = await OrganizeKit.Apply(b);
            Assert.All(OrganizeKit.Steps(applied), s => Assert.Equal("verified", s.GetProperty("state").GetString()));
            Assert.Contains(OrganizeKit.Category(h.Mock, "Empty category")!.Elements("Sound"), e => ((string)e.Attribute("url")!).EndsWith("airhorn.wav"));
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task ARestartBetweenCommandAndVerificationIsCheckedAgain()
    {
        var mock = new MockSoundpad { DuplicateOnAdd = false };
        using var pipe = new SoundpadPipe(mock.Name, true);
        try
        {
            // The companion stops right after "applying" was recorded: once before the command reached Soundpad, once after.
            foreach (bool sent in new[] { false, true })
            {
                var name = sent ? "Sent before the crash" : "Never sent";
                var first = new Bridge(pipe, new StateStore(folder)) { OrganizeQuietMs = 30 }; await first.Poll(true);
                await OrganizeKit.Edit(first, new { kind = "createCategory", name });
                var hold = new TaskCompletionSource();
                first.UseBeforeOrganizeCommand(_ => { if (sent) { var c = OrganizeKit.Cats(mock); c.Root!.Add(new XElement("Category", new XAttribute("index", 30), new XAttribute("name", name))); mock.CategoryXml = c.ToString(); } return hold.Task; });
                var view = await OrganizeKit.Get(first);
                Assert.True((await first.Execute(Cmd("organizeApply", new { planId = OrganizeKit.Plan(view).GetProperty("id").GetString(), planRevision = OrganizeKit.Revision(view) }), "host")).Ok);
                await OrganizeKit.Eventually(async () => OrganizeKit.Steps(await OrganizeKit.Get(first)).Any(s => s.GetProperty("state").GetString() == "applying"));
                // A new companion on the same data (the first one never finishes).
                var next = new Bridge(pipe, new StateStore(folder)) { OrganizeQuietMs = 30 };
                next.StageHStarted(); await next.Poll(true);
                await OrganizeKit.Eventually(async () => OrganizeKit.Steps(await OrganizeKit.Get(next)).All(s => s.GetProperty("state").GetString() != "applying"));
                var step = OrganizeKit.Steps(await OrganizeKit.Get(next)).Single(s => s.GetProperty("name").GetString() == name);
                Assert.Equal(sent ? "verified" : "pending", step.GetProperty("state").GetString());
                if (!sent) { Assert.Equal("verified", OrganizeKit.Steps(await OrganizeKit.Apply(next)).Single(s => s.GetProperty("name").GetString() == name).GetProperty("state").GetString()); }
                Assert.Single(OrganizeKit.Cats(mock).Descendants("Category"), e => (string?)e.Attribute("name") == name);
                Assert.True((await next.Execute(Cmd("organizeDiscard"), "host")).Ok);
                // The first companion stays stuck: it never wakes to write over the data the second one uses.
                next.Dispose();
            }
        }
        finally { await mock.DisposeAsync(); }
    }

    [Fact]
    public async Task EveryStepWaitsForQuietAndAPlayLandsBetweenSteps()
    {
        var h = await Start(m => m.DurationMs = 600, quietMs: QuietRule.QuietMs);
        try
        {
            var b = h.Bridge;
            await OrganizeKit.Edit(b, new { kind = "createCategory", name = "First" }, new { kind = "createCategory", name = "Second" });
            var airhorn = OrganizeKit.Id(b, "Airhorn");
            Task<Reply>? play = null;
            b.UseBeforeOrganizeCommand(_ => { play ??= b.Execute(Cmd("play") with { SoundId = airhorn, LibraryVersion = b.Snapshot.Library.Version }, "host"); return Task.CompletedTask; });
            var done = await OrganizeKit.Apply(b);
            Assert.True((await play!).Ok);
            Assert.All(OrganizeKit.Steps(done), s => Assert.Equal("verified", s.GetProperty("state").GetString()));
            (long At, string Command)[] timeline; lock (h.Mock.Timeline) timeline = [.. h.Mock.Timeline];
            long first = timeline.First(e => e.Command.StartsWith("DoAddCategory(\"First")).At, played = timeline.First(e => e.Command.StartsWith("DoPlaySound(")).At, second = timeline.First(e => e.Command.StartsWith("DoAddCategory(\"Second")).At;
            Assert.True(first < played && played < second, "the play went between the two steps");
            // The second step waited for the sound to end (600 ms) and a quiet second after that.
            Assert.True((second - played) * 1000.0 / Stopwatch.Frequency >= 1500, "the second step waited for quiet");
            Assert.Equal(0, h.Mock.RiskyRemovals);
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task TheFirstAddThatDuplicatesInAllSoundsPausesAndIsRemembered()
    {
        var h = await Start(m => m.DuplicateOnAdd = true);
        try
        {
            var b = h.Bridge; var empty = OrganizeKit.Cat(b, "Empty category");
            await OrganizeKit.Edit(b, new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = empty, categoryName = "Empty category" }, new { kind = "addSound", soundId = OrganizeKit.Id(b, "Laugh"), categoryId = empty, categoryName = "Empty category" });
            var paused = await OrganizeKit.Apply(b);
            Assert.Equal("duplicates", OrganizeKit.Plan(paused).GetProperty("paused").GetString());
            Assert.Equal(["verified", "pending"], OrganizeKit.Steps(paused).Select(s => s.GetProperty("state").GetString()));
            Assert.True(OrganizeKit.Steps(paused)[0].GetProperty("addedToAllSounds").GetBoolean());
            Assert.Equal("duplicates", new OrganizeFiles(folder).ReadSettings().AddBehaviour);
            Assert.True((await b.Execute(Cmd("organizeResolve", new { resolution = "continue" }), "host")).Ok);
            await b.OrganizeApplyIdle().WaitAsync(TimeSpan.FromSeconds(30));
            var done = await OrganizeKit.Get(b);
            Assert.Equal("done", OrganizeKit.Plan(done).GetProperty("state").GetString());
            Assert.All(OrganizeKit.Steps(done), s => Assert.Equal("verified", s.GetProperty("state").GetString()));
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task TheDuplicateMeasurementWaitsUntilAllSoundsListsTheNewEntry()
    {
        // The real Soundpad (4.0.35, 2026-11-15) shows an added sound in its category a moment (up to about 60 ms) before All sounds
        // lists the new entry. A read in between must not count as "no second entry".
        var h = await Start(m => { m.DuplicateOnAdd = true; m.SoundlistLagReads = 2; });
        try
        {
            var b = h.Bridge; var empty = OrganizeKit.Cat(b, "Empty category");
            await OrganizeKit.Edit(b, new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = empty, categoryName = "Empty category" });
            var paused = await OrganizeKit.Apply(b);
            Assert.Equal("duplicates", OrganizeKit.Plan(paused).GetProperty("paused").GetString());
            Assert.True(OrganizeKit.Steps(paused)[0].GetProperty("addedToAllSounds").GetBoolean());
            Assert.Equal("duplicates", new OrganizeFiles(folder).ReadSettings().AddBehaviour);
        }
        finally { await Stop(h); }
    }

    [Fact]
    public void ACategoryEntryIsListedOnlyOnceAllSoundsHasItsIndex()
    {
        const string soundlist = "<Soundlist><Sound index=\"1\" url=\"a.wav\" /></Soundlist>";
        const string categories = "<Categories><Category index=\"1\" type=\"1\" name=\"All sounds\" /><Category index=\"2\" name=\"One\"><Sound index=\"1\" url=\"a.wav\" /><Sound index=\"2\" url=\"a.wav\" /></Category></Categories>";
        Assert.False(NativeTree.Parse(soundlist, categories).Listed("A.WAV"));
        const string both = "<Soundlist><Sound index=\"1\" url=\"a.wav\" /><Sound index=\"2\" url=\"a.wav\" /></Soundlist>";
        Assert.True(NativeTree.Parse(both, categories).Listed("A.WAV"));
        // Soundpad versions that give no index: nothing to wait for.
        Assert.True(NativeTree.Parse(soundlist, "<Categories><Category index=\"2\" name=\"One\"><Sound url=\"a.wav\" /></Category></Categories>").Listed("A.WAV"));
    }

    [Fact]
    public async Task GuestsProposeWithinLimitsAndNeverApply()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge; var guest = new Participant(Guid.NewGuid().ToString(), "Friend");
            var step = new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = OrganizeKit.Cat(b, "Empty category"), categoryName = "Empty category" };
            // Off by default.
            Assert.False((await b.Execute(Cmd("organizePropose", new { steps = new[] { step } }), "guest", guest, "peer-1")).Ok);
            // A 2.44 page's permissions command may carry it; it goes to organize\settings.v1.json, never state.json.
            Assert.True((await b.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "permissions") { Permissions = new Permissions(GuestOrganize: true) }, "host")).Ok);
            Assert.True(b.Snapshot.Permissions.GuestOrganize);
            Assert.DoesNotContain("guestOrganize", File.ReadAllText(Path.Combine(folder, "state.json")), StringComparison.OrdinalIgnoreCase);
            Assert.True((await b.Execute(Cmd("organizeSettings", new { guestOrganize = false }), "host")).Ok);
            Assert.False(b.Snapshot.Permissions.GuestOrganize);
            Assert.True((await b.Execute(Cmd("organizeSettings", new { guestOrganize = true }), "host")).Ok);
            // A path, an unknown field or too many steps are refused; so is a name Soundpad can't take.
            Assert.False((await b.Execute(Cmd("organizePropose", new { steps = new[] { new { kind = "addSound", path = @"C:\Windows\x.wav", categoryId = "7" } } }), "guest", guest, "peer-1")).Ok);
            Assert.False((await b.Execute(Cmd("organizePropose", new { steps = Enumerable.Repeat(step, 51).ToArray() }), "guest", guest, "peer-1")).Ok);
            Assert.False((await b.Execute(Cmd("organizePropose", new { steps = new[] { new { kind = "createCategory", name = "Smile \U0001F600" } } }), "guest", guest, "peer-1")).Ok);
            var proposed = await b.Execute(Cmd("organizePropose", new { steps = new[] { step } }), "guest", guest, "peer-1");
            Assert.True(proposed.Ok, proposed.Error);
            // Guests: never Apply, Edit, Settings; see their own proposal, and no paths at all.
            foreach (var op in new[] { "organizeApply", "organizeEdit", "organizeSettings", "organizeDiscard", "organizeResolve" })
                Assert.False((await b.Execute(Cmd(op, new { }), "guest", guest, "peer-1")).Ok);
            var seen = await OrganizeKit.Get(b, "guest", guest);
            Assert.Single(seen.GetProperty("proposals").EnumerateArray());
            Assert.DoesNotContain("Private", seen.GetRawText());
            Assert.True(seen.GetProperty("checkpoint").ValueKind == JsonValueKind.Null);
            var other = await OrganizeKit.Get(b, "guest", new Participant(Guid.NewGuid().ToString(), "Other"));
            Assert.Empty(other.GetProperty("proposals").EnumerateArray());
            Assert.DoesNotContain(OrganizeKit.Sent(h.Mock), c => c.StartsWith("DoAdd"));
            // Revoked: refused.
            Assert.True((await b.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "revokePeer") { OwnerId = "peer-1" }, "host")).Ok);
            Assert.False((await b.Execute(Cmd("organizePropose", new { steps = new[] { step } }), "guest", guest, "peer-1")).Ok);
            // The host accepts it and applies it.
            var host = await OrganizeKit.Get(b);
            var proposal = host.GetProperty("proposals")[0].GetProperty("id").GetString();
            Assert.True((await b.Execute(Cmd("organizeEdit", new { planRevision = OrganizeKit.Revision(host), acceptProposal = proposal }), "host")).Ok);
            var applied = await OrganizeKit.Apply(b);
            Assert.Equal("Friend", OrganizeKit.Steps(applied)[0].GetProperty("by").GetString());
            Assert.Equal("verified", OrganizeKit.Steps(applied)[0].GetProperty("state").GetString());
            // Twenty waiting proposals at most per guest.
            var third = new Participant(Guid.NewGuid().ToString(), "Busy");
            for (int i = 0; i < 20; i++) Assert.True((await b.Execute(Cmd("organizePropose", new { steps = new[] { new { kind = "createCategory", name = "Idea " + i } } }), "guest", third, "peer-3")).Ok);
            Assert.False((await b.Execute(Cmd("organizePropose", new { steps = new[] { new { kind = "createCategory", name = "Idea 21" } } }), "guest", third, "peer-3")).Ok);
            var mine = (await OrganizeKit.Get(b, "guest", third)).GetProperty("proposals")[0].GetProperty("id").GetString();
            Assert.False((await b.Execute(Cmd("organizeWithdraw", new { proposalId = mine }), "guest", guest, "peer-1x")).Ok); // not theirs
            Assert.True((await b.Execute(Cmd("organizeWithdraw", new { proposalId = mine }), "guest", third, "peer-3")).Ok);
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task TempSessionSlotAndAppCategoriesAreRefused()
    {
        var sessions = Path.Combine(folder, "managed-audio", "sessions", Guid.NewGuid().ToString(), "x.wav");
        var slot = Path.Combine(folder, "slots", "slot-1.wav");
        var h = await Start(m =>
        {
            m.AllXml = MockSoundpad.Sounds.Replace("</Soundlist>", $"<Sound index=\"5\" url=\"{sessions}\" title=\"Session file\"/><Sound index=\"6\" url=\"{slot}\" title=\"Slot file\"/></Soundlist>");
            m.CategoryXml = MockSoundpad.Categories.Replace("</Categories>", "<Category index=\"9\" name=\"Temp - Soundpad Web [abcdef-123456]\"/></Categories>");
        });
        try
        {
            var b = h.Bridge; var empty = OrganizeKit.Cat(b, "Empty category");
            var view = await OrganizeKit.Get(b);
            Assert.False((await b.Execute(Cmd("organizeEdit", new { planRevision = 0, add = new[] { new { kind = "addSound", soundId = OrganizeKit.Id(b, "Session file"), categoryId = empty, categoryName = "Empty category" } } }), "host")).Ok);
            // Slot files are hidden from the library only once registered; by path they are refused anyway.
            if (b.Snapshot.Library.Sounds.Any(s => s.Name == "Slot file"))
                Assert.False((await b.Execute(Cmd("organizeEdit", new { planRevision = 0, add = new[] { new { kind = "addSound", soundId = OrganizeKit.Id(b, "Slot file"), categoryId = empty, categoryName = "Empty category" } } }), "host")).Ok);
            // A stale category id (its name no longer matches) is refused.
            Assert.False((await b.Execute(Cmd("organizeEdit", new { planRevision = 0, add = new[] { new { kind = "addSound", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = empty, categoryName = "Favorites" } } }), "host")).Ok);
            Assert.Equal(0, OrganizeKit.Revision(view));
        }
        finally { await Stop(h); }
        // The app's own Temp category, made by a real reverse.
        var wav = Path.Combine(folder, "tone.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(wav, new NAudio.Wave.WaveFormat(48000, 16, 2))) writer.Write(new byte[4800], 0, 4800);
        var t = await Start(m => { m.AllXml = MockSoundpad.Sounds.Replace(@"C:\Private\airhorn.wav", wav); m.CategoryXml = MockSoundpad.Categories.Replace(@"C:\Private\airhorn.wav", wav); });
        try
        {
            var b = t.Bridge;
            Assert.True((await b.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "beginSession"), "host")).Ok);
            Assert.True((await b.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "reverse", OrganizeKit.Id(b, "Airhorn"), b.Snapshot.Library.Version), "host")).Ok);
            await b.Poll(true);
            var temp = OrganizeKit.Flatten(b.Snapshot.Library.Categories).First(c => c.Name.StartsWith("Temp - Soundpad Web"));
            var tempSound = b.Snapshot.Library.Sounds.First(s => s.Temporary);
            var refusedTarget = await b.Execute(Cmd("organizeEdit", new { planRevision = 0, add = new[] { new { kind = "addSound", soundId = OrganizeKit.Id(b, "Laugh"), categoryId = temp.Id, categoryName = temp.Name } } }), "host");
            Assert.False(refusedTarget.Ok); Assert.Contains("Soundpad Web", refusedTarget.Error);
            var refusedSound = await b.Execute(Cmd("organizeEdit", new { planRevision = 0, add = new[] { new { kind = "addSound", soundId = tempSound.Id, categoryId = OrganizeKit.Cat(b, "Empty category"), categoryName = "Empty category" } } }), "host");
            Assert.False(refusedSound.Ok); Assert.Contains("Keep it first", refusedSound.Error);
            Assert.True((await b.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "stop"), "host")).Ok);
        }
        finally { await Stop(t); }
    }

    [Fact]
    public async Task TheCheckpointCopiesSoundlistSplReadOnlyToANonSplName()
    {
        var leppsoft = Path.Combine(folder, "leppsoft-fixture"); Directory.CreateDirectory(leppsoft);
        var spl = Path.Combine(leppsoft, "soundlist.spl"); File.WriteAllText(spl, "<Soundlist>fixture</Soundlist>");
        var before = File.GetLastWriteTimeUtc(spl);
        var h = await Start();
        try
        {
            var b = h.Bridge; b.UseLeppsoftFolder(leppsoft);
            await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Checked" });
            var done = await OrganizeKit.Apply(b);
            var id = OrganizeKit.Plan(done).GetProperty("id").GetString()!;
            var copy = Path.Combine(folder, "organize", "checkpoints", id + ".spl.bak");
            Assert.Equal("<Soundlist>fixture</Soundlist>", File.ReadAllText(copy));
            Assert.True(File.Exists(Path.Combine(folder, "organize", "checkpoints", id + ".v1.json")));
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "organize"), "*.spl", SearchOption.AllDirectories));
            Assert.Equal(before, File.GetLastWriteTimeUtc(spl)); Assert.Single(Directory.GetFiles(leppsoft));
            Assert.Equal(copy, done.GetProperty("checkpoint").GetProperty("copy").GetString());
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task GuidedStepsAreTickedOffWhenSeenDoneInSoundpad()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge;
            await OrganizeKit.Edit(b,
                new { kind = "removeFromCategory", soundId = OrganizeKit.Id(b, "Airhorn"), categoryId = OrganizeKit.Cat(b, "Favorites"), categoryName = "Favorites" },
                new { kind = "renameCategory", categoryId = OrganizeKit.Cat(b, "Empty category"), categoryName = "Empty category", name = "Nothing yet" },
                new { kind = "createNested", categoryId = OrganizeKit.Cat(b, "Favorites"), categoryName = "Favorites", name = "Best of" });
            Assert.All(OrganizeKit.Steps(await OrganizeKit.Get(b)), s => Assert.Equal("manual", s.GetProperty("state").GetString()));
            // The host does it in Soundpad.
            var c = OrganizeKit.Cats(h.Mock);
            var favorites = c.Descendants("Category").Single(e => (string?)e.Attribute("name") == "Favorites");
            favorites.Elements("Sound").Where(e => ((string)e.Attribute("url")!).EndsWith("airhorn.wav")).Remove();
            favorites.Add(new XElement("Category", new XAttribute("index", 20), new XAttribute("name", "Best of")));
            c.Descendants("Category").Single(e => (string?)e.Attribute("name") == "Empty category").SetAttributeValue("name", "Nothing yet");
            h.Mock.CategoryXml = c.ToString();
            await b.Poll(true);
            await OrganizeKit.Eventually(async () => OrganizeKit.Steps(await OrganizeKit.Get(b)).All(s => s.GetProperty("state").GetString() == "done"));
            Assert.DoesNotContain(OrganizeKit.Sent(h.Mock), x => x.StartsWith("DoAdd") || x.StartsWith("DoRemove"));
        }
        finally { await Stop(h); }
    }

    [Fact]
    public async Task APartialApplyOffersToRemoveTheEmptyCategoriesItCreated()
    {
        var h = await Start();
        try
        {
            var b = h.Bridge;
            await OrganizeKit.Edit(b, new { kind = "createCategory", name = "Made one" }, new { kind = "createCategory", name = "Made two" });
            b.UseBeforeOrganizeCommand(kind => { h.Mock.RejectPrefix = "DoAddCategory(\"Made two"; return Task.CompletedTask; });
            var paused = await OrganizeKit.Apply(b);
            Assert.Equal("paused", OrganizeKit.Plan(paused).GetProperty("state").GetString());
            Assert.Equal(1, paused.GetProperty("recovery").GetProperty("created").GetInt32());
            h.Mock.RejectPrefix = null;
            var removed = await b.Execute(Cmd("organizeResolve", new { resolution = "removeCreated" }), "host");
            Assert.True(removed.Ok, removed.Error);
            Assert.Equal(1, OrganizeKit.Data(removed).GetProperty("removed").GetInt32());
            Assert.Null(OrganizeKit.Category(h.Mock, "Made one"));
            Assert.Equal(0, h.Mock.RiskyRemovals);
        }
        finally { await Stop(h); }
    }
}
