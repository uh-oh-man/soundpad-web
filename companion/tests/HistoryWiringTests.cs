using System.Text.Json;
using NAudio.CoreAudioApi;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// Mimic recording into the rolling history (2.38): capture → history wiring with the fixture source (no audio device),
/// gaps, window changes, crash recovery, guest scoping of every query, output-only capture and expired answers.
/// </summary>
[Collection("History")]
public sealed class HistoryWiringTests : IDisposable
{
    private const int Rate = MimicFixture.Rate;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-history-wiring-" + Guid.NewGuid().ToString("N"));
    public HistoryWiringTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static T Data<T>(Reply reply) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(reply.Data, SoundpadWeb.Json.Options), SoundpadWeb.Json.Options)!;
    private static JsonElement Body(Reply reply) => JsonSerializer.SerializeToElement(reply.Data, SoundpadWeb.Json.Options);
    private static long S(double seconds) => (long)Math.Round(seconds * Rate);

    /// <summary>A Bridge on the mock Soundpad with the fixture capture, a test clock that moves with the fixture, and transcripts from the fixture engine.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly MockSoundpad Mock = new();
        public readonly SoundpadPipe Pipe;
        public readonly Bridge Bridge;
        public readonly MimicFixture Capture = new();
        public readonly TranscriptFixture Engine = new();
        private long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long Now => Interlocked.Read(ref now);
        public Rig(string folder)
        {
            Pipe = new SoundpadPipe(Mock.Name, true);
            Bridge = new Bridge(Pipe, new StateStore(folder));
            Bridge.UseMimicCapture(Capture); Bridge.UseTranscriptEngine(Engine); Bridge.UseHotkey(new HotkeyFixture());
            Bridge.UseClock(() => Now);
        }
        /// <summary>Moves the fixture source and the clock on together.</summary>
        public void Advance(double seconds) { Capture.Advance(TimeSpan.FromSeconds(seconds)); Interlocked.Add(ref now, (long)(seconds * 1000)); }
        /// <summary>Starts listening and delivers the first packet at once, as a real capture would (it anchors the run's wall time).</summary>
        public async Task Start()
        {
            await Ok(Bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"));
            await Task.Delay(20); Capture.Pump();
        }
        public async ValueTask DisposeAsync() { Bridge.Dispose(); Pipe.Dispose(); await Mock.DisposeAsync(); }
    }

    private static async Task<Reply> Ok(Task<Reply> reply) { var r = await reply; Assert.True(r.Ok, r.Error); return r; }

    [Fact]
    public async Task CaptureRecordsOneTimelineAcrossStopStartAndALostOutput()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge; await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession"), "host")); // clips become Temp sounds, which need a hosting session
        Assert.Null(bridge.Snapshot.Mimic!.History!.SessionId); // nothing recorded yet
        await rig.Start();
        string session = bridge.Snapshot.Mimic!.History!.SessionId!;
        Assert.True(Directory.Exists(Path.Combine(folder, "history", session))); // on disk, in the companion's data folder
        rig.Advance(5);
        var first = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
        Assert.Equal((session, Rate, true, 600), (first.SessionId, first.Rate, first.Recording, first.WindowSeconds));
        Assert.InRange(first.LiveFrame, S(5), S(7));
        // Stop keeps the history and marks a gap.
        await Ok(bridge.Execute(Cmd("mimicStop"), "host"));
        var stopped = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
        Assert.False(stopped.Recording); Assert.Equal(HistoryGapReason.Stopped, Assert.Single(stopped.Gaps).Reason);
        Assert.Null(stopped.Gaps[0].WallToUtcMs);
        Assert.True((await bridge.Execute(Cmd("mimicClip") with { StartFrame = S(2), EndFrame = S(2.5), SourceId = session }, "host")).Ok); // still clippable
        // A new Start continues the same timeline (frames never restart).
        rig.Advance(30); // nothing is recorded while stopped
        await rig.Start();
        rig.Advance(3);
        var resumed = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
        Assert.Equal(session, resumed.SessionId);
        Assert.InRange(resumed.LiveFrame, stopped.LiveFrame + S(3), stopped.LiveFrame + S(5));
        var gap = Assert.Single(resumed.Gaps);
        Assert.Equal(stopped.LiveFrame, gap.AtFrame); Assert.NotNull(gap.WallToUtcMs);
        Assert.InRange(gap.WallToUtcMs!.Value - gap.WallFromUtcMs, 29000, 33000); // the gap took wall time but no frames
        Assert.Equal(2, resumed.Spans.Length);
        // The output goes away: history stays, the gap says why, and the host is asked for another output.
        rig.Capture.Lose();
        Assert.False(bridge.Snapshot.Mimic!.Active); Assert.True(bridge.Snapshot.Mimic.History!.SourceLost);
        var lost = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
        Assert.Equal(HistoryGapReason.DeviceLost, lost.Gaps[^1].Reason);
        Assert.True((await bridge.Execute(Cmd("mimicClip") with { StartFrame = S(4), EndFrame = S(4.5), SourceId = session }, "host")).Ok);
        await rig.Start();
        Assert.False(bridge.Snapshot.Mimic!.History!.SourceLost);
        Assert.Equal(session, bridge.Snapshot.Mimic.History.SessionId);
        // Levels of the whole history, in min/max pairs, from the same timeline.
        var levels = Body(await Ok(bridge.Execute(Cmd("historyLevels") with { StartFrame = 0, EndFrame = lost.LiveFrame, Bins = 100 }, "host")));
        Assert.Equal(200, levels.GetProperty("levels").GetArrayLength());
        Assert.Contains(levels.GetProperty("levels").EnumerateArray(), v => v.GetInt32() > 10000);
        // End hosting stops capture but keeps the history; quitting the companion deletes it.
        await Ok(bridge.Execute(Cmd("endSession"), "host"));
        Assert.True(Directory.Exists(Path.Combine(folder, "history", session)));
        bridge.Dispose();
        Assert.False(Directory.Exists(Path.Combine(folder, "history", session)));
        Assert.False(Directory.Exists(Path.Combine(folder, "transcripts", session)));
    }

    [Fact]
    public async Task TheWindowAndSubtitlesAreHostSettingsSavedInTheirOwnFile()
    {
        await using (var rig = new Rig(folder))
        {
            var bridge = rig.Bridge; await bridge.Poll(true);
            Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host")).Ok);
            Assert.Equal((600, "gaming"), (bridge.Snapshot.Mimic!.History!.Seconds, bridge.Snapshot.Mimic.History.Subtitles));
            Assert.False((await bridge.Execute(Cmd("historySettings") with { Seconds = 120 }, "guest")).Ok);
            Assert.Contains("1 to 60 minutes", (await bridge.Execute(Cmd("historySettings") with { Seconds = 30 }, "host")).Error);
            Assert.False((await bridge.Execute(Cmd("historySettings") with { Seconds = 3601 }, "host")).Ok);
            Assert.False((await bridge.Execute(Cmd("historySettings") with { Subtitles = "turbo" }, "host")).Ok);
            await rig.Start();
            rig.Advance(100);
            await Ok(bridge.Execute(Cmd("historySettings") with { Seconds = 60, Subtitles = "balanced" }, "host"));
            Assert.Equal((60, "balanced"), (bridge.Snapshot.Mimic!.History!.Seconds, bridge.Snapshot.Mimic.History.Subtitles));
            // A shorter window applies at once: what is older (beyond one 10 s segment) is gone.
            var timeline = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
            Assert.Equal(60, timeline.WindowSeconds);
            Assert.InRange(timeline.Available[0].StartFrame, timeline.LiveFrame - S(70), timeline.LiveFrame - S(60));
            await Ok(bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host"));
            Assert.Equal(SubtitleMode.Balanced, rig.Engine.Mode);
            // Saved in a small file of its own (older companions never read it); state.json is untouched.
            var saved = File.ReadAllText(Path.Combine(folder, "mimic-settings.json"));
            Assert.Contains("\"historySeconds\":60", saved); Assert.Contains("\"subtitles\":\"balanced\"", saved); Assert.Contains("fixture:tone", saved);
            Assert.DoesNotContain("history", File.ReadAllText(Path.Combine(folder, "state.json")), StringComparison.OrdinalIgnoreCase);
        }
        // The next launch starts with the host's choices, and offers the remembered source first.
        await using (var again = new Rig(folder))
        {
            Assert.Equal((60, "balanced"), (again.Bridge.Snapshot.Mimic!.History!.Seconds, again.Bridge.Snapshot.Mimic.History.Subtitles));
            Assert.Equal(MimicFixture.Source.Id, Body(await Ok(again.Bridge.Execute(Cmd("mimicSources"), "host"))).GetProperty("remembered").GetString());
        }
        // A damaged settings file is ignored, never trusted.
        File.WriteAllText(Path.Combine(folder, "mimic-settings.json"), "{\"historySeconds\": 99999, \"subtitles\": \"x\", \"sourceId\": \"input:mic\"}");
        await using (var damaged = new Rig(folder))
        {
            Assert.Equal((600, "gaming"), (damaged.Bridge.Snapshot.Mimic!.History!.Seconds, damaged.Bridge.Snapshot.Mimic.History.Subtitles));
            Assert.Equal(JsonValueKind.Null, Body(await Ok(damaged.Bridge.Execute(Cmd("mimicSources"), "host"))).GetProperty("remembered").ValueKind);
        }
    }

    [Fact]
    public async Task AfterACrashTheNextLaunchRecoversTheSameSessionAndTheHostCanDiscardIt()
    {
        // A launch that died: audio and words on disk, no clean close.
        string crashed;
        {
            var store = HistoryStore.Create(folder, new HistoryOptions { Rate = Rate, TailSeconds = 600 });
            crashed = store.SessionId;
            for (int s = 0; s < 125; s++) store.Append(Enumerable.Range(0, Rate).Select(i => MimicFixture.Sample(s * (long)Rate + i)).ToArray());
            Assert.True(store.Flush(30000));
            TranscriptWords.Open(folder, crashed).Add([new(0, S(2), S(2.5), "Hello", 0.9f, 0, true, true, WordStatus.Done, 1)], new(0, S(4)));
            store.Abandon();
        }
        // Left-overs that are not recoverable are removed: an orphaned transcript.
        Directory.CreateDirectory(Path.Combine(folder, "transcripts", "orphan"));
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        long frames = bridge.RecoverHistory();
        Assert.InRange(frames, S(120), S(125));
        Assert.False(Directory.Exists(Path.Combine(folder, "transcripts", "orphan")));
        var state = bridge.Snapshot.Mimic!.History!;
        Assert.Equal(crashed, state.SessionId);
        Assert.InRange(state.RecoveredMs, 120000, 125000); // "Recovered 2 minutes of history from the last session"
        var timeline = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
        Assert.Equal(HistoryGapReason.Crash, Assert.Single(timeline.Gaps).Reason);
        // The recovered words come back with it.
        var words = Body(await Ok(bridge.Execute(Cmd("historyTranscript") with { StartFrame = 0, EndFrame = S(10) }, "host")));
        Assert.Equal("Hello", words.GetProperty("words")[0].GetProperty("text").GetString());
        // A new Start continues the same session after the gap.
        await rig.Start();
        rig.Advance(2);
        var resumed = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
        Assert.Equal(crashed, resumed.SessionId); Assert.True(resumed.LiveFrame > timeline.LiveFrame);
        // Discard (host only): the recovered audio goes; recording carries on into a fresh history.
        Assert.False((await bridge.Execute(Cmd("historyDiscard"), "guest")).Ok);
        await Ok(bridge.Execute(Cmd("historyDiscard"), "host"));
        Assert.Equal(0, bridge.Snapshot.Mimic!.History!.RecoveredMs);
        Assert.NotEqual(crashed, bridge.Snapshot.Mimic.History.SessionId);
        Assert.False(Directory.Exists(Path.Combine(folder, "history", crashed)));
        Assert.False(Directory.Exists(Path.Combine(folder, "transcripts", crashed)));
        rig.Advance(2);
        Assert.InRange(Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host"))).LiveFrame, S(1), S(4));
        // An ordinary quit deletes everything; the next launch has nothing to recover.
        bridge.Dispose();
        Assert.Empty(Directory.GetDirectories(Path.Combine(folder, "history")));
        await using var next = new Rig(folder);
        Assert.Equal(0, next.Bridge.RecoverHistory());
    }

    [Fact]
    public async Task GuestsSeeTheHistoryOnlyFromTheirJoinTimeOnEveryQuery()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge; await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        await Ok(bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host"));
        await rig.Start();
        await Ok(bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host"));
        rig.Advance(20);
        Participant alice = new("alice-id", "Alice"), bob = new("bob-id", "Bob");
        bridge.NoteGuest(alice, "peer-a", rig.Now); // the owner page admitted Alice now
        rig.Advance(10);
        string session = bridge.Snapshot.Mimic!.History!.SessionId!;
        // The host sees (and gets recognised) everything; wait for words on both sides of Alice's join.
        JsonElement hostWords = default;
        for (int i = 0; i < 100; i++)
        {
            long live = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host"))).LiveFrame;
            hostWords = Body(await Ok(bridge.Execute(Cmd("historyTranscript") with { StartFrame = 0, EndFrame = live }, "host"))).GetProperty("words");
            if (hostWords.EnumerateArray().Any(w => w.GetProperty("start").GetInt64() < S(18)) && hostWords.EnumerateArray().Any(w => w.GetProperty("start").GetInt64() > S(22))) break;
            await Task.Delay(100);
        }
        Assert.Contains(hostWords.EnumerateArray(), w => w.GetProperty("start").GetInt64() < S(18));
        Task<Reply> AsAlice(Command c) => bridge.Execute(c, "guest", alice, "peer-a");
        // Timeline: from her join frame only, also for spans.
        var mine = Data<HistoryTimelineReply>(await Ok(AsAlice(Cmd("historyTimeline"))));
        long from = mine.FromFrame!.Value;
        Assert.InRange(from, S(20), S(23));
        Assert.All(mine.Available, r => Assert.True(r.StartFrame >= from));
        Assert.All(mine.Spans, s => Assert.True(s.StartFrame >= from));
        // Levels: nothing before her join, although the host's waveform has tones there.
        var hostLevels = Body(await Ok(bridge.Execute(Cmd("historyLevels") with { StartFrame = 0, EndFrame = S(30), Bins = 30 }, "host"))).GetProperty("levels").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var herLevels = Body(await Ok(AsAlice(Cmd("historyLevels") with { StartFrame = 0, EndFrame = S(30), Bins = 30 }))).GetProperty("levels").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        Assert.Contains(hostLevels.Take(2 * 19), v => v != 0);
        Assert.All(herLevels.Take(2 * 20), v => Assert.Equal(0, v));
        Assert.Contains(herLevels.Skip(2 * 24), v => v != 0);
        // Transcript: only words whose audio she may hear.
        var herWords = Body(await Ok(AsAlice(Cmd("historyTranscript") with { StartFrame = 0, EndFrame = S(30) }))).GetProperty("words");
        Assert.All(herWords.EnumerateArray(), w => Assert.True(w.GetProperty("start").GetInt64() >= from));
        // The pre-2.38 peek is scoped the same way (levels and transcript lines).
        var peek = Data<MimicPeek>(await Ok(AsAlice(Cmd("mimicPeek"))));
        long windowStart = peek.End - S(30);
        Assert.All(peek.Levels.Take((int)((from - windowStart) * Bridge.MimicBuckets / S(30)) - 1), l => Assert.Equal(0, l));
        Assert.All(peek.Transcript!, l => Assert.True(l.StartMs >= (from - windowStart) * 1000 / Rate - 1));
        // Clips: before her join is refused, after it works.
        var early = await AsAlice(Cmd("mimicClip") with { StartFrame = S(10), EndFrame = S(10.5), SourceId = session });
        Assert.False(early.Ok); Assert.Contains("before you joined", early.Error);
        Assert.True((await AsAlice(Cmd("mimicClip") with { StartFrame = S(26), EndFrame = S(26.5), SourceId = session, Name = "After" })).Ok);
        // Her join time cannot be lowered later, whatever is sent.
        bridge.NoteGuest(alice, "peer-a", 0);
        Assert.Equal(from, Data<HistoryTimelineReply>(await Ok(AsAlice(Cmd("historyTimeline")))).FromFrame);
        // The host shares an earlier range: exactly that opens for guests, on every query.
        Assert.False((await AsAlice(Cmd("historyShare") with { StartFrame = S(10), EndFrame = S(12) })).Ok);
        await Ok(bridge.Execute(Cmd("historyShare") with { StartFrame = S(10), EndFrame = S(12) }, "host"));
        Assert.Contains(new HistoryRange(S(10), S(12)), Data<HistoryTimelineReply>(await Ok(AsAlice(Cmd("historyTimeline")))).Shared);
        Assert.True((await AsAlice(Cmd("mimicClip") with { StartFrame = S(10), EndFrame = S(10.5), SourceId = session, Name = "Shared" })).Ok);
        Assert.False((await AsAlice(Cmd("mimicClip") with { StartFrame = S(12), EndFrame = S(12.5), SourceId = session })).Ok);
        var sharedLevels = Body(await Ok(AsAlice(Cmd("historyLevels") with { StartFrame = S(10), EndFrame = S(12), Bins = 4 }))).GetProperty("levels").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        Assert.Contains(sharedLevels, v => v != 0);
        await Ok(bridge.Execute(Cmd("historyUnshare"), "host"));
        Assert.False((await AsAlice(Cmd("mimicClip") with { StartFrame = S(10), EndFrame = S(10.5), SourceId = session })).Ok);
        // A guest the companion first hears of now sees from now (the safe default for an older owner page).
        var bobs = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "guest", bob, "peer-b")));
        Assert.InRange(bobs.FromFrame!.Value, S(29), mine.LiveFrame + S(3));
        // A new hosting session forgets who was admitted: Alice's next query counts from then.
        await Ok(bridge.Execute(Cmd("endSession"), "host"));
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        await rig.Start();
        rig.Advance(2);
        Assert.True(Data<HistoryTimelineReply>(await Ok(AsAlice(Cmd("historyTimeline")))).FromFrame > from + S(5));
    }

    [Fact]
    public async Task OnlyOutputsAreEverCaptured()
    {
        // The explicit check (2.38), without opening any device: an input endpoint id or data flow is refused.
        Assert.Throws<InvalidDataException>(() => WasapiMimicCapture.RequireOutput("{0.0.1.00000000}.{6a1c0d2e-1111-2222-3333-444455556666}", DataFlow.Capture));
        Assert.Throws<InvalidDataException>(() => WasapiMimicCapture.RequireOutput("{0.0.0.00000000}.{6a1c0d2e-1111-2222-3333-444455556666}", DataFlow.Capture));
        Assert.Throws<InvalidDataException>(() => WasapiMimicCapture.RequireOutput("{0.0.1.00000000}.{6a1c0d2e-1111-2222-3333-444455556666}", DataFlow.Render));
        WasapiMimicCapture.RequireOutput("{0.0.0.00000000}.{6a1c0d2e-1111-2222-3333-444455556666}", DataFlow.Render);
        // Start refuses a capture endpoint before touching anything (the data flow comes from a stand-in, not a device).
        using var capture = new WasapiMimicCapture(_ => DataFlow.Capture);
        var microphone = new MimicSource("output:{0.0.1.00000000}.{6a1c0d2e-1111-2222-3333-444455556666}", "Output: Microphone (Wave Link)", "output");
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => capture.Start(microphone, new MimicRing(48000, 1)));
        Assert.Contains("never a microphone", refused.Message);
        Assert.False(capture.Running); Assert.Equal("Off", capture.Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.Start(new MimicSource("input:{0.0.1.00000000}.{x}", "Microphone", "input"), new MimicRing(48000, 1)));
        // The companion only starts what its source list offers, and that list never holds inputs.
        await using var rig = new Rig(folder); await rig.Bridge.Poll(true);
        var notListed = await rig.Bridge.Execute(Cmd("mimicStart") with { SourceId = microphone.Id }, "host");
        Assert.False(notListed.Ok); Assert.Contains("no longer available", notListed.Error);
        Assert.Null(rig.Bridge.Snapshot.Mimic!.History!.SessionId); // nothing was created
    }

    [Fact]
    public async Task ClipsComeFromAnywhereInTheHistoryAndExpiredMomentsAreAnsweredPlainly()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge; await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        await rig.Start();
        rig.Advance(100);
        string session = bridge.Snapshot.Mimic!.History!.SessionId!;
        // 90 s old: far older than the 45 s buffer before 2.38.
        var old = await bridge.Execute(Cmd("mimicClip") with { StartFrame = S(10), EndFrame = S(10.5), SourceId = session, Name = "Ninety seconds ago" }, "host");
        Assert.True(old.Ok, old.Error);
        var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), SoundpadWeb.Json.Options)!;
        using (var wave = new NAudio.Wave.WaveFileReader(journal.Assets.Single().Variants.Single().Path))
        {
            Assert.InRange(wave.TotalTime.TotalMilliseconds, 495, 505);
            var samples = new float[wave.SampleCount]; NAudio.Wave.WaveExtensionMethods.ToSampleProvider(wave).Read(samples.AsSpan());
            Assert.InRange(samples.Max(Math.Abs), 0.45f, 0.55f); // the tone that was there, not something else
        }
        // Host preview of old history works too; more than 30 s, or a range from another session, is refused.
        Assert.True((await bridge.Execute(Cmd("mimicPreview") with { StartFrame = S(20), EndFrame = S(21) }, "host")).Ok);
        Assert.Contains("30 seconds", (await bridge.Execute(Cmd("mimicClip") with { StartFrame = S(10), EndFrame = S(41) }, "host")).Error);
        Assert.Contains("restarted", (await bridge.Execute(Cmd("mimicClip") with { StartFrame = S(10), EndFrame = S(11), SourceId = "other" }, "host")).Error);
        // Once the window has moved past it, the answer says so (and the socket stays: a reply, not an exception).
        await Ok(bridge.Execute(Cmd("historySettings") with { Seconds = 60 }, "host"));
        var expired = await bridge.Execute(Cmd("mimicClip") with { StartFrame = S(10), EndFrame = S(10.5), SourceId = session }, "host");
        Assert.False(expired.Ok); Assert.Equal("That moment is no longer in the history. Select it again.", expired.Error);
        var preview = await bridge.Execute(Cmd("mimicPreview") with { StartFrame = S(10), EndFrame = S(10.5) }, "host");
        Assert.Contains("no longer in the history", preview.Error);
        var future = await bridge.Execute(Cmd("mimicClip") with { StartFrame = S(500), EndFrame = S(501) }, "host");
        Assert.Contains("not been recorded yet", future.Error);
        // Levels never refuse: expired bins are simply empty.
        var levels = Body(await Ok(bridge.Execute(Cmd("historyLevels") with { StartFrame = 0, EndFrame = S(20), Bins = 10 }, "host"))).GetProperty("levels").EnumerateArray().Select(v => v.GetInt32());
        Assert.All(levels, v => Assert.Equal(0, v));
        Assert.False((await bridge.Execute(Cmd("historyLevels") with { StartFrame = 0, EndFrame = S(20), Bins = 5000 }, "host")).Ok);
    }

    [Fact]
    public async Task TheHotkeyAndTheOldViewWorkOnTopOfTheHistory()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge; await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        await rig.Start();
        rig.Advance(40);
        // The 30 s view of older websites: the newest 30 s, with the session id as its ring id.
        var peek = Data<MimicPeek>(await Ok(bridge.Execute(Cmd("mimicPeek"), "host")));
        Assert.Equal((Bridge.MimicWindowMs, Bridge.MimicBuckets, bridge.Snapshot.Mimic!.History!.SessionId), (peek.WindowMs, peek.Levels.Length, peek.RingId));
        Assert.Contains(peek.Levels, l => l > 400); Assert.Contains(0, peek.Levels);
        var clip = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 0, EndMs = 3000, SourceId = peek.RingId }, "host");
        Assert.True(clip.Ok, clip.Error);
        await bridge.HotkeyPressed();
        Assert.StartsWith("Saved the last 10 s", bridge.Snapshot.Mimic!.HotkeyStatus);
        // Stopped: the hotkey does nothing (as before), the history is still there.
        await Ok(bridge.Execute(Cmd("mimicStop"), "host"));
        await bridge.HotkeyPressed();
        Assert.Contains("not listening", bridge.Snapshot.Mimic!.HotkeyStatus);
    }
}
