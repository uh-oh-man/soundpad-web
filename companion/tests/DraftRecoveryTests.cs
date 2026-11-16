using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// 2.45 (WP2, D05 and D06): the host's drafts in drafts.v1.json, one "Recover previous session / Discard" choice after a
/// crash that covers Temp, the recording history and drafts, a normal quit that leaves nothing, an update that still
/// carries drafts, and each guest's storage action history. Mock Soundpad and fixture capture only.
/// A crash is a launch whose Bridge is never disposed: its history is abandoned (no clean close) and its journal
/// session stays open, as when the process dies.
/// </summary>
[Collection("History")]
public sealed class DraftRecoveryTests : IDisposable
{
    private const int Rate = MimicFixture.Rate;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-draft-recovery-" + Guid.NewGuid().ToString("N"));
    private long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public DraftRecoveryTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static JsonElement Body(Reply reply) => JsonSerializer.SerializeToElement(reply.Data, SoundpadWeb.Json.Options);
    private static long S(double seconds) => (long)Math.Round(seconds * Rate);
    private static async Task<Reply> Ok(Task<Reply> reply) { var r = await reply; Assert.True(r.Ok, r.Error); return r; }
    private static async Task<string> Refused(Task<Reply> reply) { var r = await reply; Assert.False(r.Ok, "expected a refusal"); return r.Error!; }
    private static readonly Participant Alice = new("11111111-1111-1111-1111-111111111111", "Alice");
    private static readonly Participant Bob = new("22222222-2222-2222-2222-222222222222", "Bob");
    private string DraftFile => Path.Combine(folder, DraftStore.FileName);

    /// <summary>A Bridge on a shared mock Soundpad (a crash does not restart Soundpad), with the fixture capture and a test clock.</summary>
    private Bridge Launch(SoundpadPipe pipe, MimicFixture capture)
    {
        var bridge = new Bridge(pipe, new StateStore(folder));
        bridge.UseMimicCapture(capture); bridge.UseTranscriptEngine(new TranscriptFixture()); bridge.UseHotkey(new HotkeyFixture());
        bridge.UseClock(() => Interlocked.Read(ref now));
        return bridge;
    }
    private void Advance(MimicFixture capture, double seconds) { capture.Advance(TimeSpan.FromSeconds(seconds)); Interlocked.Add(ref now, (long)(seconds * 1000)); }
    private static async Task Host(Bridge bridge, MimicFixture capture)
    {
        await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        await Ok(bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host"));
        await Ok(bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"));
        await Task.Delay(20); capture.Pump();
    }
    private static Command Range(string op, string session, double from, double to) => Cmd(op) with { StartFrame = S(from), EndFrame = S(to), SourceId = session };
    private static string Id(Reply r) => Body(r).GetProperty("id").GetString()!;
    private static async Task<string> PreviewOf(Bridge bridge, string draftId, string session) => Body(await Ok(bridge.Execute(Cmd("mimicPreview") with { DraftId = draftId, SourceId = session }, "host"))).GetProperty("wav").GetString()!;
    /// <summary>The process dies: what was written is on disk, the history is never closed, nothing is cleaned up.</summary>
    private static async Task Crash(Bridge bridge)
    {
        await bridge.FlushDrafts();
        Assert.True(bridge.History!.Flush(30000));
        bridge.History!.Abandon();
    }
    /// <summary>The next launch, as Program starts it: history recovery, then the Temp check, then the first poll.</summary>
    private static async Task StartAfterCrash(Bridge next)
    {
        Assert.True(next.RecoverHistory() > 0);
        await next.StartupCleanup();
        await next.Poll(true);
    }
    private JsonElement Asset(string assetId)
    {
        using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")));
        return journal.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("id").GetString() == assetId).Clone();
    }
    private string TemporaryVariant(string assetId) =>
        Asset(assetId).GetProperty("variants").EnumerateArray().First(v => v.GetProperty("orientation").GetString() == "normal" && v.GetProperty("current").GetBoolean() && !v.GetProperty("permanent").GetBoolean()).GetProperty("id").GetString()!;
    /// <summary>The kept (permanent) file of a source, from the managed-audio journal on disk.</summary>
    private string KeptPath(string assetId)
    {
        return Asset(assetId).GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("permanent").GetBoolean() && v.GetProperty("current").GetBoolean()).GetProperty("path").GetString()!;
    }
    private static SessionRecoveryView Status(Reply r) => JsonSerializer.Deserialize<SessionRecoveryView>(JsonSerializer.Serialize(r.Data, SoundpadWeb.Json.Options), SoundpadWeb.Json.Options)!;
    private static byte[] Wave(int seed = 3)
    {
        using var stream = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(stream, new NAudio.Wave.WaveFormat(16000, 16, 1)))
            for (int i = 0; i < 16000; i++) writer.WriteSample((float)Math.Sin((i + seed) * 0.05) * 0.4f);
        return stream.ToArray();
    }

    [Fact]
    public async Task AfterACrashOnePromptListsTempHistoryAndDraftsAndRecoverBringsThemBackAsTheyWere()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var capture = new MimicFixture();
        var crashed = Launch(pipe, capture);
        await Host(crashed, capture);
        Advance(capture, 12);
        string session = crashed.Snapshot.Mimic!.History!.SessionId!;
        string first = Id(await Ok(crashed.Execute(Range("draftCreate", session, 2, 4) with { Name = "First" }, "host")));
        string second = Id(await Ok(crashed.Execute(Range("draftCreate", session, 5, 7), "host")));
        // Edited after it was made: the edges and the typed name are what comes back.
        await Ok(crashed.Execute(Range("draftUpdate", session, 4.5, 8.25) with { DraftId = second, Name = "Edited" }, "host"));
        // One draft also made its clip in Temp (the clip a crash leaves behind).
        var clip = Body(await Ok(crashed.Execute(Cmd("draftKeep") with { DraftId = first, SourceId = session }, "host"))).GetProperty("assetId").GetString()!;
        // A guest's draft: never written (guests see nothing of a crashed launch afterwards).
        crashed.NoteGuest(Alice, "peer-a", Interlocked.Read(ref now) - 6000);
        string guests = Id(await Ok(crashed.Execute(Range("draftCreate", session, 9, 11), "guest", Alice, "peer-a")));
        var before = Body(await Ok(crashed.Execute(Cmd("draftList"), "host"))).GetProperty("drafts").GetRawText();
        var audio = new Dictionary<string, string> { [first] = await PreviewOf(crashed, first, session), [second] = await PreviewOf(crashed, second, session) };
        await Crash(crashed);
        // On disk: the host's two drafts, in a file of its own; state.json never mentions drafts.
        var file = File.ReadAllText(DraftFile);
        Assert.Contains("\"format\":1", file); Assert.Contains(first, file); Assert.Contains(second, file);
        Assert.DoesNotContain(guests, file); Assert.DoesNotContain("Alice", file);
        Assert.DoesNotContain("draft", File.ReadAllText(Path.Combine(folder, "state.json")), StringComparison.OrdinalIgnoreCase);

        var next = Launch(pipe, new MimicFixture());
        try
        {
            await StartAfterCrash(next);
            // One prompt with every part and its size; drafts are not offered until the host chooses.
            var status = Status(await Ok(next.Execute(Cmd("sessionRecover") with { Action = "status" }, "host")));
            Assert.True(status.Pending);
            Assert.Equal(1, status.Temp!.Count); Assert.True(status.Temp.Bytes > 0);
            Assert.InRange(status.History!.Ms!.Value, 11000, 13000); Assert.Equal(status.History.Ms * Rate / 1000 * 2, status.History.Bytes);
            Assert.Equal(2, status.Drafts!.Count);
            Assert.Equal(S(2) + S(8.25) - S(4.5), status.Drafts.Bytes / 2);
            Assert.Equal(1, next.Snapshot.TempRecovery);
            Assert.True(next.Snapshot.Mimic!.History!.RecoveredMs > 0);
            Assert.Empty(Body(await Ok(next.Execute(Cmd("draftList"), "host"))).GetProperty("drafts").EnumerateArray());
            // The guest's draft pin is let go; the host's pins hold their audio while the host decides.
            Assert.Equal(new[] { "draft:" + first, "draft:" + second }.Order(), next.History!.Pins(HistoryAccess.Host).Keys.Order());
            // Only the host decides.
            Assert.Contains("Only the host", await Refused(next.Execute(Cmd("sessionRecover") with { Action = "recover" }, "guest", Alice, "peer-a")));
            Assert.Contains("Only the host", await Refused(next.Execute(Cmd("sessionRecover"), "guest", Alice, "peer-a")));

            var recovered = Status(await Ok(next.Execute(Cmd("sessionRecover") with { Action = "recover" }, "host")));
            Assert.Equal(2, recovered.Drafts!.Count);
            // Everything is back as before the crash: the drafts with their edges, names and clip, byte for byte.
            Assert.False(next.SessionRecovery().Pending);
            Assert.Null(next.Snapshot.TempRecovery);
            Assert.Equal(0, next.Snapshot.Mimic!.History!.RecoveredMs);
            Assert.Equal(before, Body(await Ok(next.Execute(Cmd("draftList"), "host"))).GetProperty("drafts").GetRawText());
            foreach (var (id, wav) in audio) Assert.Equal(wav, await PreviewOf(next, id, session));
            Assert.Contains(next.Snapshot.Library.Sounds, s => s.Temporary && s.AssetId == clip);
            // The draft still knows its clip: Keep uses it instead of making another.
            var again = Body(await Ok(next.Execute(Cmd("draftKeep") with { DraftId = first, SourceId = session }, "host")));
            Assert.Equal(clip, again.GetProperty("assetId").GetString());
            // The recovered history is the current one and recording carries on in it.
            await Ok(next.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"));
            Assert.Equal(session, next.Snapshot.Mimic!.History!.SessionId);
            // A second crash still has the drafts (the file was written again).
            await next.FlushDrafts();
            Assert.Contains(first, File.ReadAllText(DraftFile));
        }
        finally { next.Dispose(); }
        // That was an ordinary quit: history and drafts are gone, nothing to recover next time.
        Assert.False(File.Exists(DraftFile));
        using var third = Launch(pipe, new MimicFixture());
        Assert.Equal(0, third.RecoverHistory());
        Assert.False(third.SessionRecovery().Pending);
    }

    [Fact]
    public async Task DiscardRemovesOnlyWhatTheCrashLeftAndNeverAnythingKept()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var capture = new MimicFixture();
        var crashed = Launch(pipe, capture);
        await Host(crashed, capture);
        Advance(capture, 12);
        string session = crashed.Snapshot.Mimic!.History!.SessionId!;
        string draft = Id(await Ok(crashed.Execute(Range("draftCreate", session, 2, 4) with { Name = "Gone" }, "host")));
        await Ok(crashed.Execute(Cmd("draftKeep") with { DraftId = draft, SourceId = session }, "host"));
        // A clip kept permanently before the crash.
        var upload = await crashed.Upload(Wave(), "Kept for good.wav", "Host", true, "host");
        string kept = Body(upload).GetProperty("assetId").GetString()!;
        await Ok(crashed.Execute(Cmd("keepAudio") with { AssetId = kept, VariantId = TemporaryVariant(kept), Confirmed = true }, "host"));
        string keptPath = KeptPath(kept);
        Assert.True(File.Exists(keptPath));
        await Crash(crashed);

        var capture2 = new MimicFixture();
        var next = Launch(pipe, capture2);
        try
        {
            await StartAfterCrash(next);
            var status = next.SessionRecovery();
            Assert.Equal((1, 1), (status.Temp!.Count, status.Drafts!.Count));
            // Recording since the restart stays through Discard.
            await Ok(next.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"));
            await Task.Delay(20); capture2.Pump();
            long boundary = next.History!.LiveFrame;
            Advance(capture2, 3);
            var discarded = Status(await Ok(next.Execute(Cmd("sessionRecover") with { Action = "discard" }, "host")));
            Assert.Equal(1, discarded.Drafts!.Count);
            Assert.False(next.SessionRecovery().Pending);
            Assert.Null(next.Snapshot.TempRecovery);
            Assert.Equal(0, next.Snapshot.Mimic!.History!.RecoveredMs);
            await next.Poll(true);
            // The draft, its clip in Temp and the recovered audio are gone; no pin is left.
            Assert.Empty(Body(await Ok(next.Execute(Cmd("draftList"), "host"))).GetProperty("drafts").EnumerateArray());
            Assert.Empty(next.History!.Pins(HistoryAccess.Host));
            Assert.DoesNotContain(next.Snapshot.Library.Sounds, s => s.Temporary);
            Assert.False((await next.Execute(Range("mimicPreview", session, 2, 4), "host")).Ok);
            var timeline = JsonSerializer.Deserialize<HistoryTimelineReply>(JsonSerializer.Serialize((await Ok(next.Execute(Cmd("historyTimeline"), "host"))).Data, SoundpadWeb.Json.Options), SoundpadWeb.Json.Options)!;
            Assert.Equal(session, timeline.SessionId);
            Assert.True(timeline.Available[0].StartFrame >= boundary - S(1));
            // What was kept stays, file and all.
            Assert.True(File.Exists(keptPath));
            Assert.Equal(keptPath, KeptPath(kept));
            await next.FlushDrafts();
            Assert.False(File.Exists(DraftFile));
        }
        finally { next.Dispose(); }
    }

    [Fact]
    public async Task ANormalQuitLeavesNothingToRecoverAndADamagedFileIsIgnored()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var capture = new MimicFixture();
        using (var quit = Launch(pipe, capture))
        {
            await Host(quit, capture);
            Advance(capture, 8);
            await Ok(quit.Execute(Range("draftCreate", quit.Snapshot.Mimic!.History!.SessionId!, 2, 4), "host"));
            await quit.FlushDrafts();
            Assert.True(File.Exists(DraftFile));
        }
        Assert.False(File.Exists(DraftFile));
        using (var next = Launch(pipe, new MimicFixture()))
        {
            Assert.Equal(0, next.RecoverHistory());
            Assert.False(next.SessionRecovery().Pending);
        }

        // A crash with a damaged or foreign drafts file: the history is recovered, drafts are not, nothing throws.
        string[] damaged =
        [
            "not json",
            "{\"format\":2,\"sessionId\":\"x\",\"savedUtcMs\":1,\"drafts\":[]}",
            "{\"format\":1,\"sessionId\":\"another\",\"savedUtcMs\":1,\"drafts\":[]}",
        ];
        foreach (var content in damaged.Append("bad draft"))
        {
            var cap = new MimicFixture();
            var crashed = Launch(pipe, cap);
            await Host(crashed, cap);
            Advance(cap, 8);
            string session = crashed.Snapshot.Mimic!.History!.SessionId!;
            string id = Id(await Ok(crashed.Execute(Range("draftCreate", session, 2, 4), "host")));
            await Crash(crashed);
            if (content == "bad draft")
            {
                // Another person's draft (only the host's are ever written): left out.
                var good = File.ReadAllText(DraftFile);
                File.WriteAllText(DraftFile, good.Replace("\"owner\":\"host\"", "\"owner\":\"" + Alice.Id + "\""));
            }
            else File.WriteAllText(DraftFile, content);
            using var next = Launch(pipe, new MimicFixture());
            Assert.True(next.RecoverHistory() > 0);
            Assert.Equal(0, next.RecoveredDraftCount);
            Assert.Null(next.SessionRecovery().Drafts);
            // Its pin is let go too: it belongs to no draft.
            Assert.DoesNotContain("draft:" + id, next.History!.Pins(HistoryAccess.Host).Keys);
            await Ok(next.Execute(Cmd("sessionRecover") with { Action = "discard" }, "host"));
        }
    }

    [Fact]
    public async Task AnUpdateStillCarriesDraftsAndTheFileFollowsThem()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var capture = new MimicFixture();
        BridgeHandoff part; string session, draft;
        using (var a = Launch(pipe, capture))
        {
            await Host(a, capture);
            Advance(capture, 8);
            session = a.Snapshot.Mimic!.History!.SessionId!;
            draft = Id(await Ok(a.Execute(Range("draftCreate", session, 2, 4) with { Name = "Through the update" }, "host")));
            await a.FlushDrafts();
            part = await a.EnterUpdateExit();
            await a.FlushDrafts();
        }
        // The update path leaves the file (the next process continues these drafts) and hands them over in memory.
        Assert.Contains(draft, File.ReadAllText(DraftFile));
        Assert.Contains(part.Drafts, d => d.Id == draft);
        var b = Launch(pipe, new MimicFixture());
        await b.ResumeFromHandoff(JsonSerializer.Deserialize<BridgeHandoff>(JsonSerializer.SerializeToUtf8Bytes(part, SoundpadWeb.Json.Options), SoundpadWeb.Json.Options)!);
        Assert.False(b.SessionRecovery().Pending); // no question after an update
        var list = Body(await Ok(b.Execute(Cmd("draftList"), "host"))).GetProperty("drafts");
        Assert.Equal("Through the update", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());
        // An edit after the update is written, and a crash of the new process still recovers it.
        await Ok(b.Execute(Range("draftUpdate", session, 1, 5) with { DraftId = draft }, "host"));
        await Crash(b);
        using var c = Launch(pipe, new MimicFixture());
        Assert.True(c.RecoverHistory() > 0);
        await Ok(c.Execute(Cmd("sessionRecover") with { Action = "recover" }, "host"));
        var back = Assert.Single(Body(await Ok(c.Execute(Cmd("draftList"), "host"))).GetProperty("drafts").EnumerateArray());
        Assert.Equal((S(1), S(5)), (back.GetProperty("startFrame").GetInt64(), back.GetProperty("endFrame").GetInt64()));
    }

    /// <summary>D06: a guest's recent Temp and Keep actions, for the host (any guest) and that guest (only their own).</summary>
    [Fact]
    public async Task GuestsStorageActionsAreSharedWithTheHostAndThatGuestOnly()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        using var bridge = Launch(pipe, new MimicFixture());
        await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        long at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clip = await bridge.Upload(Wave(), "Alice line.wav", "Alice", false, "peer-a", guest: Alice.Id);
        Assert.True(clip.Ok, clip.Error);
        Assert.True((await bridge.Upload(Wave(9), "Bob line.wav", "Bob", false, "peer-b", guest: Bob.Id)).Ok);
        await Ok(bridge.Execute(Cmd("keepAudio") with { AssetId = Body(clip).GetProperty("assetId").GetString() }, "guest", Alice, "peer-a"));

        var forHost = Body(await Ok(bridge.Execute(Cmd("budgetActions") with { GuestId = Alice.Id }, "host")));
        Assert.Equal(Alice.Id, forHost.GetProperty("guestId").GetString());
        var actions = forHost.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(["kept", "temp"], actions.Select(a => a.GetProperty("kind").GetString()));
        Assert.All(actions, a => Assert.Equal("Alice line", a.GetProperty("name").GetString()));
        Assert.All(actions, a => Assert.True(a.GetProperty("bytes").GetInt64() > 0));
        Assert.All(actions, a => Assert.InRange(a.GetProperty("at").GetInt64(), at, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        // A guest reads only their own, whichever guest they name.
        var own = Body(await Ok(bridge.Execute(Cmd("budgetActions") with { GuestId = Bob.Id }, "guest", Alice, "peer-a")));
        Assert.Equal(Alice.Id, own.GetProperty("guestId").GetString());
        Assert.DoesNotContain("Bob", own.GetRawText());
        var bobs = Body(await Ok(bridge.Execute(Cmd("budgetActions"), "guest", Bob, "peer-b"))).GetProperty("actions");
        Assert.Equal("Bob line", Assert.Single(bobs.EnumerateArray()).GetProperty("name").GetString());
        Assert.Contains("Choose a guest", await Refused(bridge.Execute(Cmd("budgetActions"), "host")));
        Assert.Empty(Body(await Ok(bridge.Execute(Cmd("budgetActions") with { GuestId = "nobody" }, "host"))).GetProperty("actions").EnumerateArray());
        // At most the last 20.
        for (int i = 0; i < 25; i++) bridge.Budgets.NoteTemp(Bob.Id, "Bob", "asset-" + i, 10, "Clip " + i);
        var many = Body(await Ok(bridge.Execute(Cmd("budgetActions") with { GuestId = Bob.Id }, "host"))).GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(GuestBudgets.MaxActions, many.Length);
        Assert.Equal("Clip 24", many[0].GetProperty("name").GetString());
    }
}
