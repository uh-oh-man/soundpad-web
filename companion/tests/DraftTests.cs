using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// Stage E2 (2.40): drafts on the recording history and their pins, Quick Mimic (latest phrase, own replay), private
/// preview for guests, guest Keep with budgets and the host's approval, Temp that survives End hosting, and the
/// crash recovery of Temp. Fixture capture and the mock Soundpad only; no audio device.
/// </summary>
[Collection("History")]
public sealed class DraftTests : IDisposable
{
    private const int Rate = MimicFixture.Rate;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-drafts-" + Guid.NewGuid().ToString("N"));
    public DraftTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static JsonElement Body(Reply reply) => JsonSerializer.SerializeToElement(reply.Data, SoundpadWeb.Json.Options);
    private static long S(double seconds) => (long)Math.Round(seconds * Rate);
    private static async Task<Reply> Ok(Task<Reply> reply) { var r = await reply; Assert.True(r.Ok, r.Error); return r; }
    private static async Task<string> Refused(Task<Reply> reply) { var r = await reply; Assert.False(r.Ok, "expected a refusal"); return r.Error!; }

    private static readonly Participant Alice = new("11111111-1111-1111-1111-111111111111", "Alice");

    /// <summary>As HistoryWiringTests' rig: mock Soundpad, fixture capture and transcripts, a clock that moves with the fixture.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly MockSoundpad Mock = new();
        public readonly SoundpadPipe Pipe;
        public readonly Bridge Bridge;
        public readonly MimicFixture Capture = new();
        private long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long Now => Interlocked.Read(ref now);
        public Rig(string folder, HistoryOptions? options = null)
        {
            Pipe = new SoundpadPipe(Mock.Name, true);
            Bridge = new Bridge(Pipe, new StateStore(folder));
            Bridge.UseMimicCapture(Capture); Bridge.UseTranscriptEngine(new TranscriptFixture()); Bridge.UseHotkey(new HotkeyFixture());
            if (options != null) Bridge.UseHistoryOptions(options);
            Bridge.UseClock(() => Now);
        }
        public void Advance(double seconds) { Capture.Advance(TimeSpan.FromSeconds(seconds)); Interlocked.Add(ref now, (long)(seconds * 1000)); }
        public async Task Start()
        {
            await Bridge.Poll(true);
            await Ok(Bridge.Execute(Cmd("beginSession"), "host"));
            await Ok(Bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host"));
            await Ok(Bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"));
            await Task.Delay(20); Capture.Pump();
        }
        public string Session => Bridge.Snapshot.Mimic!.History!.SessionId!;
        public Task<Reply> AsAlice(Command c) => Bridge.Execute(c, "guest", Alice, "peer-a");
        public async ValueTask DisposeAsync() { Bridge.Dispose(); Pipe.Dispose(); await Mock.DisposeAsync(); }
    }

    private static Command Range(string op, string session, double from, double to) => Cmd(op) with { StartFrame = S(from), EndFrame = S(to), SourceId = session };

    // ---- Drafts and their pins -------------------------------------------------------------------------------------

    [Fact]
    public async Task ADraftPinsItsAudioMovesItsPinWhenEditedAndUnpinsOnDiscard()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Start();
        rig.Advance(12);
        var draft = Body(await Ok(bridge.Execute(Range("draftCreate", rig.Session, 2, 4), "host")));
        string id = draft.GetProperty("id").GetString()!;
        Assert.Equal(S(2), draft.GetProperty("startFrame").GetInt64());
        Assert.Equal(new HistoryRange(S(2), S(4)), bridge.History!.Pins(HistoryAccess.Host)["draft:" + id]);
        Assert.True(bridge.Snapshot.Drafts > 0);
        // Extended into the surrounding history: the pin follows.
        await Ok(bridge.Execute(Range("draftUpdate", rig.Session, 1, 6) with { DraftId = id, Name = "Longer" }, "host"));
        Assert.Equal(new HistoryRange(S(1), S(6)), bridge.History!.Pins(HistoryAccess.Host)["draft:" + id]);
        var list = Body(await Ok(bridge.Execute(Cmd("draftList"), "host")));
        var mine = Assert.Single(list.GetProperty("drafts").EnumerateArray());
        Assert.Equal("Longer", mine.GetProperty("name").GetString());
        // Shorter than a clip may be: refused, and the draft keeps its range.
        Assert.Contains("between", await Refused(bridge.Execute(Range("draftUpdate", rig.Session, 1, 1.05) with { DraftId = id }, "host")));
        // Someone else cannot change or discard it.
        Assert.Contains("gone", await Refused(rig.AsAlice(Cmd("draftDiscard") with { DraftId = id })));
        await Ok(bridge.Execute(Cmd("draftDiscard") with { DraftId = id }, "host"));
        Assert.False(bridge.History!.Pins(HistoryAccess.Host).ContainsKey("draft:" + id));
        Assert.Empty(Body(await Ok(bridge.Execute(Cmd("draftList"), "host"))).GetProperty("drafts").EnumerateArray());
    }

    [Fact]
    public async Task ADraftKeepsItsAudioPastTheWindowAndBecomesAClipOnKeep()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Start();
        await Ok(bridge.Execute(Cmd("historySettings") with { Seconds = 60 }, "host"));
        rig.Advance(6);
        var id = Body(await Ok(bridge.Execute(Range("draftCreate", rig.Session, 2, 4) with { Name = "Pinned" }, "host"))).GetProperty("id").GetString()!;
        rig.Advance(90); // the window has moved past it
        Assert.False((await bridge.Execute(Range("mimicClip", rig.Session, 12, 14), "host")).Ok); // unpinned audio of that age is gone
        var kept = Body(await Ok(bridge.Execute(Cmd("draftKeep") with { DraftId = id }, "host")));
        Assert.False(kept.GetProperty("closed").GetBoolean());
        string asset = kept.GetProperty("assetId").GetString()!;
        // Keep again with the same range: the same clip, not a second import.
        var again = Body(await Ok(bridge.Execute(Cmd("draftKeep") with { DraftId = id, Action = "close" }, "host")));
        Assert.Equal(asset, again.GetProperty("assetId").GetString());
        Assert.True(again.GetProperty("closed").GetBoolean());
        Assert.Single(bridge.Snapshot.Library.Sounds, s => s.Temporary && s.Name == "Pinned");
        Assert.Empty(bridge.History!.Pins(HistoryAccess.Host)); // closing the draft let its audio go
    }

    [Fact]
    public async Task ThePinBudgetGivesAClearMessage()
    {
        // One 10 s segment of protected audio at most.
        await using var rig = new Rig(folder, new HistoryOptions { PinBudgetBytes = 10L * Rate * 2 }); var bridge = rig.Bridge;
        await rig.Start();
        rig.Advance(25);
        await Ok(bridge.Execute(Range("draftCreate", rig.Session, 1, 2), "host"));
        await Ok(bridge.Execute(Range("draftCreate", rig.Session, 3, 5), "host")); // the same segment
        var error = await Refused(bridge.Execute(Range("draftCreate", rig.Session, 12, 14), "host"));
        Assert.Contains("Protected recording history is full", error);
        Assert.Contains("discard a draft", error);
        Assert.Equal(2, bridge.History!.Pins(HistoryAccess.Host).Count);
    }

    [Fact]
    public async Task EachPersonHasTheirOwnDraftsAndSeesOthersOnlyWithinTheirHistory()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Start();
        rig.Advance(6);
        bridge.NoteGuest(Alice, "peer-a", rig.Now); // Alice joins at 6 s
        rig.Advance(8);
        var early = Body(await Ok(bridge.Execute(Range("draftCreate", rig.Session, 1, 3), "host"))).GetProperty("id").GetString()!;
        var late = Body(await Ok(bridge.Execute(Range("draftCreate", rig.Session, 8, 10) with { Name = "Host's line" }, "host"))).GetProperty("id").GetString()!;
        var herList = Body(await Ok(rig.AsAlice(Cmd("draftList"))));
        Assert.Empty(herList.GetProperty("drafts").EnumerateArray());
        var band = Assert.Single(herList.GetProperty("others").EnumerateArray()); // the early one is from before she joined
        Assert.Equal((late, "Host"), (band.GetProperty("id").GetString(), band.GetProperty("ownerName").GetString()));
        // She copies it into her own, independent draft, and cannot copy what she may not see.
        var copy = Body(await Ok(rig.AsAlice(Cmd("draftCopy") with { DraftId = late })));
        Assert.Equal(S(8), copy.GetProperty("startFrame").GetInt64());
        Assert.Contains("before you joined", await Refused(rig.AsAlice(Cmd("draftCopy") with { DraftId = early })));
        // Her draft is hers: the host sees it as a band, she edits it without touching the host's.
        string hers = copy.GetProperty("id").GetString()!;
        await Ok(rig.AsAlice(Range("draftUpdate", rig.Session, 9, 12) with { DraftId = hers }));
        var hostList = Body(await Ok(bridge.Execute(Cmd("draftList"), "host")));
        Assert.Equal(2, hostList.GetProperty("drafts").GetArrayLength());
        var hersAsBand = Assert.Single(hostList.GetProperty("others").EnumerateArray());
        Assert.Equal(("Alice", S(9)), (hersAsBand.GetProperty("ownerName").GetString(), hersAsBand.GetProperty("startFrame").GetInt64()));
        // Her drafts start where she may see: a draft of audio before she joined is refused.
        Assert.Contains("before you joined", await Refused(rig.AsAlice(Range("draftCreate", rig.Session, 2, 4))));
    }

    // ---- Quick Mimic and own replay -------------------------------------------------------------------------------

    private static TranscriptWord Word(long id, double start, double end, long phrase, bool pauseAfter = false) => new(id, S(start), S(end), "w" + id, 0.9f, 0, false, pauseAfter, "done", phrase);

    [Fact]
    public void LatestPhraseSkipsUnfinishedPhrasesAndTheCompanionsOwnReplayButNotOtherVoices()
    {
        var words = new[]
        {
            Word(1, 1.0, 1.4, 1), Word(2, 1.5, 2.0, 1, pauseAfter: true),
            Word(3, 3.0, 3.5, 2), Word(4, 3.6, 4.0, 2, pauseAfter: true),
            Word(5, 5.0, 5.3, 3), Word(6, 5.4, 5.8, 3), // still being said: ends within half a second of live
        };
        long live = S(6);
        var log = new OwnReplayLog();
        // Nothing replayed: the newest finished phrase.
        Assert.Equal(new HistoryRange(S(3.0) - S(0.02), S(4.0) + S(0.01)), QuickMimic.LatestPhrase(words, live, Rate, (a, z) => log.Covers("s", a, z), S(30)));
        // Phrase 2 was the companion's own replay (wholly inside it): skipped, the one before is taken.
        log.Started("s", S(2.9), S(1.2), S(0.5), "clip");
        Assert.Equal(new HistoryRange(S(1.0) - S(0.02), S(2.0) + S(0.01)), QuickMimic.LatestPhrase(words, live, Rate, (a, z) => log.Covers("s", a, z), S(30)));
        // A replay that only overlaps a phrase (someone talking over it) does not hide that phrase.
        var other = new OwnReplayLog();
        other.Started("s", S(3.2), S(0.2), 0, "short");
        Assert.Equal(S(4.0) + S(0.01), QuickMimic.LatestPhrase(words, live, Rate, (a, z) => other.Covers("s", a, z), S(30))!.Value.EndFrame);
        // Stop cuts a replay short; a later play cuts the earlier one.
        var cut = new OwnReplayLog();
        cut.Started("s", S(2.9), S(10), 0, "long");
        cut.Stopped("s", S(3.2), 0);
        Assert.False(cut.Covers("s", S(3.0), S(4.0)));
        cut.Started("s", S(5), S(10), 0, "next");
        cut.Started("s", S(6), S(1), 0, "newer");
        Assert.Equal(new HistoryRange(S(5), S(6)), cut.Ranges("s")[1]);
        // Nothing finished: null.
        Assert.Null(QuickMimic.LatestPhrase(words.Skip(4), live, Rate, (_, _) => false, S(30)));
        // Last 10 seconds stays inside the newest visible range.
        Assert.Equal(new HistoryRange(S(5), S(15)), QuickMimic.Recent([new(0, S(4)), new(S(5), S(15))], S(15), Rate));
        Assert.Equal(new HistoryRange(S(10), S(20)), QuickMimic.Recent([new(0, S(20))], S(20), Rate));
    }

    [Fact]
    public async Task PlaysAreMarkedAsOwnReplayAndQuickMimicMakesDrafts()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Start();
        Assert.Contains("subtitles", await Refused(rig.AsAlice(Cmd("draftCreate") with { Action = "phrase" })));
        await Ok(bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host"));
        rig.Advance(12);
        // Latest phrase: a finished "Fixture tone N", as a draft for whoever asked.
        Reply? phrase = null;
        for (int i = 0; i < 100 && phrase is not { Ok: true }; i++) { await Task.Delay(100); phrase = await bridge.Execute(Cmd("draftCreate") with { Action = "phrase" }, "host"); }
        Assert.True(phrase!.Ok, phrase.Error);
        var draft = Body(phrase);
        Assert.StartsWith("Fixture tone", draft.GetProperty("name").GetString());
        Assert.InRange(draft.GetProperty("endFrame").GetInt64() - draft.GetProperty("startFrame").GetInt64(), S(0.2), S(3));
        // Last 10 seconds.
        var recent = Body(await Ok(bridge.Execute(Cmd("draftCreate") with { Action = "recent" }, "host")));
        Assert.Equal(S(10), recent.GetProperty("endFrame").GetInt64() - recent.GetProperty("startFrame").GetInt64());
        // A play into the game is recorded as own replay at the live edge, and Stop ends it.
        long live = bridge.History!.LiveFrame;
        await Ok(bridge.Execute(Cmd("play") with { SoundId = bridge.Snapshot.Library.Sounds[0].Id }, "host"));
        var replay = Assert.Single(bridge.OwnReplays);
        Assert.InRange(replay.StartFrame, live, live + S(0.5));
        await Ok(bridge.Execute(Cmd("stop"), "host"));
        Assert.True(Assert.Single(bridge.OwnReplays).EndFrame <= bridge.History!.LiveFrame + S(0.5));
        var timeline = Body(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
        Assert.Single(timeline.GetProperty("replays").EnumerateArray());
    }

    // ---- Private preview for guests --------------------------------------------------------------------------------

    [Fact]
    public async Task AGuestPreviewIsScopedToTheirHistoryLimitedAndSmall()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Start();
        rig.Advance(6);
        bridge.NoteGuest(Alice, "peer-a", rig.Now);
        rig.Advance(8);
        bridge.UsePreviewLimit(new RateWindow(2, TimeSpan.FromMinutes(1)));
        Assert.Contains("before you joined", await Refused(rig.AsAlice(Range("mimicPreview", rig.Session, 1, 3))));
        var wav = Convert.FromBase64String(Body(await Ok(rig.AsAlice(Range("mimicPreview", rig.Session, 8, 10)))).GetProperty("wav").GetString()!);
        Assert.Equal(Bridge.GuestPreviewRate, BitConverter.ToInt32(wav, 24)); // the WAV header's sample rate
        Assert.InRange(wav.Length, 2 * S(2), 2 * S(2) + 100);
        // Her own draft can be previewed by id; someone else's cannot.
        var hers = Body(await Ok(rig.AsAlice(Range("draftCreate", rig.Session, 9, 11)))).GetProperty("id").GetString()!;
        Assert.Contains("Too many previews", await Refused(rig.AsAlice(Cmd("mimicPreview") with { DraftId = hers }))); // the limit of 2 is used up
        bridge.UsePreviewLimit(new RateWindow(5, TimeSpan.FromMinutes(1)));
        await Ok(rig.AsAlice(Cmd("mimicPreview") with { DraftId = hers }));
        var host = Body(await Ok(bridge.Execute(Range("draftCreate", rig.Session, 2, 3), "host"))).GetProperty("id").GetString()!;
        Assert.Contains("gone", await Refused(rig.AsAlice(Cmd("mimicPreview") with { DraftId = host })));
        // At most 30 s, and only while the host allows guest Mimic. The host is not limited.
        Assert.Contains("seconds", await Refused(rig.AsAlice(Range("mimicPreview", rig.Session, 6.5, 13.9) with { EndFrame = S(6.5) + S(31) })));
        for (int i = 0; i < 4; i++) await Ok(bridge.Execute(Range("mimicPreview", rig.Session, 1, 2), "host"));
        await Ok(bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: false) }, "host"));
        Assert.Contains("not enabled Mimic", await Refused(rig.AsAlice(Range("mimicPreview", rig.Session, 8, 10))));
    }

    // ---- Guest Keep and budgets -----------------------------------------------------------------------------------

    private static string Asset(Reply clip) => Body(clip).GetProperty("assetId").GetString()!;

    [Fact]
    public async Task GuestKeepUsesTheHostsDestinationAndPausesAtThePermanentBudgetUntilTheHostAllowsMore()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Start();
        bridge.NoteGuest(Alice, "peer-a", rig.Now);
        rig.Advance(50);
        string destination = Path.Combine(folder, "kept-by-guests");
        await Ok(bridge.Execute(Cmd("keepSettings") with { DestinationDirectory = destination, KeptMb = 1, TempMb = 8, BudgetMode = "session" }, "host"));
        Assert.Contains("Host permission", await Refused(rig.AsAlice(Cmd("keepSettings") with { KeptMb = 100 })));
        Assert.True(bridge.Snapshot.Permissions.GuestKeep); // on by default
        // Three 15 s clips (about 480 KB each at the fixture's 16 kHz): two fit the 1 MB permanent budget.
        var clips = new List<string>();
        for (int i = 0; i < 3; i++) clips.Add(Asset(await Ok(rig.AsAlice(Range("mimicClip", rig.Session, 1 + i * 15, 16 + i * 15)))));
        // A guest's command never carries a path: what she sends is ignored, the host's destination is used.
        await Ok(rig.AsAlice(Cmd("keepAudio") with { AssetId = clips[0], DestinationDirectory = Path.Combine(folder, "elsewhere") }));
        await Ok(rig.AsAlice(Cmd("keepAudio") with { AssetId = clips[1] }));
        Assert.Equal(2, Directory.GetFiles(destination, "*.wav", SearchOption.AllDirectories).Length);
        Assert.False(Directory.Exists(Path.Combine(folder, "elsewhere")));
        var refusal = await Refused(rig.AsAlice(Cmd("keepAudio") with { AssetId = clips[2] }));
        Assert.StartsWith(GuestBudgets.Waiting, refusal);
        // Paused: new clips wait too, while playing and editing go on. The host gets a request.
        Assert.StartsWith(GuestBudgets.Waiting, await Refused(rig.AsAlice(Range("mimicClip", rig.Session, 46, 48))));
        var own = Body(await Ok(rig.AsAlice(Cmd("budgetStatus")))).GetProperty("own");
        Assert.True(own.GetProperty("paused").GetBoolean());
        Assert.Equal("kept", own.GetProperty("waiting").GetString());
        var status = Body(await Ok(bridge.Execute(Cmd("budgetStatus"), "host")));
        var request = Assert.Single(status.GetProperty("requests").EnumerateArray());
        Assert.Equal((Alice.Id, "Alice", "kept"), (request.GetProperty("guestId").GetString(), request.GetProperty("name").GetString(), request.GetProperty("kind").GetString()));
        var usage = Assert.Single(status.GetProperty("guests").EnumerateArray());
        Assert.InRange(usage.GetProperty("keptBytes").GetInt64(), 900_000, 1_000_000);
        // Only the host decides.
        Assert.False((await rig.AsAlice(Cmd("budgetGrant") with { GuestId = Alice.Id, Action = "allow" })).Ok);
        await Ok(bridge.Execute(Cmd("budgetGrant") with { GuestId = Alice.Id, Action = "allow" }, "host"));
        await Ok(rig.AsAlice(Cmd("keepAudio") with { AssetId = clips[2] }));
        await Ok(rig.AsAlice(Range("mimicClip", rig.Session, 46, 48)));
        Assert.Empty(Body(await Ok(bridge.Execute(Cmd("budgetStatus"), "host"))).GetProperty("requests").EnumerateArray());
        // Kept files are never deleted by budgets; and guests cannot remove kept audio.
        Assert.Equal(3, Directory.GetFiles(destination, "*.wav", SearchOption.AllDirectories).Length);
        Assert.Contains("only temporary audio", await Refused(rig.AsAlice(Cmd("removeAudio") with { AssetId = clips[0] })));
        // The host turns guest Keep off: refused.
        await Ok(bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true, GuestKeep: false) }, "host"));
        Assert.False(bridge.Snapshot.Permissions.GuestKeep);
        Assert.Contains("not allowed guests to keep", await Refused(rig.AsAlice(Cmd("keepAudio") with { AssetId = clips[2] })));
        // state.json never holds it (older companions read that file); keep-settings.json does.
        Assert.DoesNotContain("guestKeep", File.ReadAllText(Path.Combine(folder, "state.json")));
        Assert.Contains("\"guestKeep\":false", File.ReadAllText(Path.Combine(folder, "keep-settings.json")));
    }

    [Fact]
    public async Task TheTempBudgetPausesNewClipsAndAGuestMayRemoveTheirOwnToMakeRoom()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Start();
        bridge.NoteGuest(Alice, "peer-a", rig.Now);
        rig.Advance(50);
        await Ok(bridge.Execute(Cmd("keepSettings") with { TempMb = 1 }, "host"));
        var first = Asset(await Ok(rig.AsAlice(Range("mimicClip", rig.Session, 1, 16))));
        await Ok(rig.AsAlice(Range("mimicClip", rig.Session, 16, 31)));
        Assert.StartsWith(GuestBudgets.Waiting, await Refused(rig.AsAlice(Range("mimicClip", rig.Session, 31, 46))));
        Assert.Equal("temp", Assert.Single(bridge.Budgets.Requests()).Kind);
        // Her own clip, also from another connection after a reload: she may remove it, and then it fits again.
        await Ok(bridge.Execute(Cmd("removeAudio") with { AssetId = first }, "guest", Alice, "peer-after-reload"));
        await Ok(rig.AsAlice(Range("mimicClip", rig.Session, 31, 46)));
        Assert.Empty(bridge.Budgets.Requests());
        // Someone else's clip is not hers to remove.
        var hosts = Asset(await Ok(bridge.Execute(Range("mimicClip", rig.Session, 1, 3), "host")));
        Assert.Contains("only temporary audio they added", await Refused(rig.AsAlice(Cmd("removeAudio") with { AssetId = hosts })));
    }

    [Fact]
    public void BudgetsCountKeptAudioPerSessionOrInTotal()
    {
        string totals = Path.Combine(folder, "guest-kept.json");
        var session = new KeepSettings(KeptMb: 1, BudgetMode: "session");
        var total = session with { BudgetMode = "total" };
        var first = new GuestBudgets(totals);
        Assert.Null(first.CheckKeep("g", "Gee", 700_000, session));
        first.NoteKept("g", "Gee", 700_000);
        // A new launch: the session budget starts again, the total one remembers.
        var second = new GuestBudgets(totals);
        Assert.Null(second.CheckKeep("g", "Gee", 700_000, session));
        Assert.NotNull(second.CheckKeep("g", "Gee", 700_000, total));
        Assert.True(second.Grant("g", total));
        Assert.Null(second.CheckKeep("g", "Gee", 700_000, total));
        Assert.False(second.Grant("g", total)); // nothing is waiting any more
        Assert.False(second.Deny("nobody"));
    }

    // ---- Temp lifecycle ---------------------------------------------------------------------------------------------

    private static byte[] Wave(double pitch = 10)
    {
        using var stream = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(stream, new NAudio.Wave.WaveFormat(16000, 16, 1)))
            for (int i = 0; i < 8000; i++) writer.WriteSample((float)Math.Sin(i / pitch) * 0.4f);
        return stream.ToArray();
    }

    [Fact]
    public async Task TempSurvivesEndHostingAndOnlyQuittingClearsIt()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        Assert.True((await bridge.Upload(Wave(), "Kept around.wav", "Host", true, "host")).Ok);
        Assert.Equal(1, bridge.TemporaryClipCount);
        await Ok(bridge.Execute(Cmd("endSession"), "host"));
        await bridge.Poll(true);
        Assert.Equal(1, bridge.TemporaryClipCount);
        Assert.Contains(bridge.Snapshot.Library.Sounds, s => s.Temporary && s.Name == "Kept around");
        // Resuming hosting finds it where it was.
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        await Ok(bridge.Execute(Cmd("play") with { SoundId = bridge.Snapshot.Library.Sounds.Single(s => s.Temporary).Id }, "host"));
        await Ok(bridge.Execute(Cmd("stop"), "host"));
        await bridge.CleanupSession(); await bridge.Poll(true);
        Assert.Equal(0, bridge.TemporaryClipCount);
        Assert.DoesNotContain(bridge.Snapshot.Library.Sounds, s => s.Temporary);
    }

    [Fact]
    public async Task AfterACrashTheHostRecoversOrDiscardsThePreviousTemp()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        using (var crashed = new Bridge(pipe, new StateStore(folder)))
        {
            await crashed.Poll(true);
            await Ok(crashed.Execute(Cmd("beginSession"), "host"));
            Assert.True((await crashed.Upload(Wave(), "Survivor.wav", "Host", true, "host")).Ok);
            Assert.True((await crashed.Upload(Wave(7), "Second.wav", "Host", true, "host")).Ok);
        } // no CleanupSession: the process died
        using (var next = new Bridge(pipe, new StateStore(folder)))
        {
            // As Program does: the startup cleanup runs before the first poll.
            await next.StartupCleanup();
            await next.Poll(true);
            Assert.Equal(2, next.Snapshot.TempRecovery);
            Assert.Equal(2, next.Snapshot.Library.Sounds.Count(s => s.Temporary)); // still there while the host decides
            Assert.False((await next.Execute(Cmd("tempRecover") with { Action = "discard" }, "guest")).Ok);
            await Ok(next.Execute(Cmd("tempRecover") with { Action = "recover" }, "host"));
            Assert.Null(next.Snapshot.TempRecovery);
            Assert.Equal(2, next.Snapshot.Library.Sounds.Count(s => s.Temporary));
            // Recovered clips play as before.
            await Ok(next.Execute(Cmd("beginSession"), "host"));
            await Ok(next.Execute(Cmd("play") with { SoundId = next.Snapshot.Library.Sounds.First(s => s.Temporary).Id }, "host"));
            await Ok(next.Execute(Cmd("stop"), "host"));
        } // crashed again
        using (var third = new Bridge(pipe, new StateStore(folder)))
        {
            await third.StartupCleanup();
            await third.Poll(true);
            Assert.Equal(2, third.Snapshot.TempRecovery);
            await Ok(third.Execute(Cmd("tempRecover") with { Action = "discard" }, "host"));
            Assert.Null(third.Snapshot.TempRecovery);
            Assert.DoesNotContain(third.Snapshot.Library.Sounds, s => s.Temporary);
            Assert.Equal(0, third.TemporaryClipCount);
            await third.CleanupSession(); // an ordinary quit
        }
        using (var fourth = new Bridge(pipe, new StateStore(folder)))
        {
            await fourth.StartupCleanup();
            await fourth.Poll(true);
            Assert.Null(fourth.Snapshot.TempRecovery); // nothing to recover after an ordinary quit
        }
    }
}
