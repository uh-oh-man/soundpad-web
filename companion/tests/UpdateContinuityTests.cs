using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// Session continuity across an update (2.44, WP2): Bridge A hosts, leaves through the update path, and Bridge B continues
/// its session from the hand-over, on the same data folder. Mock Soundpad, fixture capture, transcripts and hotkeys only.
/// </summary>
[Collection("History")]
public sealed class UpdateContinuityTests : IDisposable
{
    private const int Rate = MimicFixture.Rate;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-update-continuity-" + Guid.NewGuid().ToString("N"));
    private long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public UpdateContinuityTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static JsonElement Body(Reply reply) => JsonSerializer.SerializeToElement(reply.Data, SoundpadWeb.Json.Options);
    private static T Data<T>(Reply reply) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(reply.Data, SoundpadWeb.Json.Options), SoundpadWeb.Json.Options)!;
    private static long S(double seconds) => (long)Math.Round(seconds * Rate);
    private static async Task<Reply> Ok(Task<Reply> reply) { var r = await reply; Assert.True(r.Ok, r.Error); return r; }
    private static readonly Participant Alice = new("11111111-1111-1111-1111-111111111111", "Alice");

    private sealed class Rig : IAsyncDisposable
    {
        public readonly MockSoundpad Mock = new();
        public readonly SoundpadPipe Pipe;
        public readonly Bridge Bridge;
        public readonly MimicFixture Capture = new();
        private readonly UpdateContinuityTests owner;
        public Rig(UpdateContinuityTests owner)
        {
            this.owner = owner;
            Pipe = new SoundpadPipe(Mock.Name, true);
            Bridge = new Bridge(Pipe, new StateStore(owner.folder));
            Bridge.UseMimicCapture(Capture); Bridge.UseTranscriptEngine(new TranscriptFixture()); Bridge.UseHotkeys(key => new HotkeyFixture(key));
            Bridge.UseClock(() => Interlocked.Read(ref owner.now));
        }
        public void Advance(double seconds) { Capture.Advance(TimeSpan.FromSeconds(seconds)); Interlocked.Add(ref owner.now, (long)(seconds * 1000)); }
        public Task<Reply> AsAlice(Command c) => Bridge.Execute(c, "guest", Alice, "peer-a", "audience-1");
        public string Session => Bridge.Snapshot.Mimic!.History!.SessionId!;
        public async ValueTask DisposeAsync() { Bridge.Dispose(); Pipe.Dispose(); await Mock.DisposeAsync(); }
    }

    /// <summary>Bridge A with everything a session holds in memory, then the update path out. Returns what it handed over.</summary>
    private async Task<(BridgeHandoff Part, string Session, string Draft, long AliceFrom, string Clip, BudgetsHandoff Budgets)> HostAndLeave(bool capture = true)
    {
        await using var a = new Rig(this);
        var bridge = a.Bridge;
        await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession") with { AudienceId = "audience-1" }, "host"));
        await Ok(bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host"));
        await Ok(bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"));
        await Task.Delay(20); a.Capture.Pump();
        a.Advance(12);
        var session = a.Session;
        bridge.NoteGuest(Alice, "peer-a", Interlocked.Read(ref now) - 4000); // Alice joined 4 s ago
        var draft = Body(await Ok(bridge.Execute(Cmd("draftCreate") with { StartFrame = S(2), EndFrame = S(4), SourceId = session, Name = "Pinned" }, "host"))).GetProperty("id").GetString()!;
        await Ok(bridge.Execute(Cmd("historyShare") with { StartFrame = S(1), EndFrame = S(3), SourceId = session }, "host"));
        var clip = await Ok(a.AsAlice(Cmd("mimicClip") with { StartFrame = S(9), EndFrame = S(11), SourceId = session, Name = "Alice line" }));
        await Ok(bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F7" }, "host"));
        await Ok(bridge.Execute(Cmd("mimicHotkey") with { Action = "length", PositionMs = 15000 }, "host"));
        await Ok(bridge.Execute(Cmd("mimicHotkey") with { Action = "enable" }, "host"));
        await Ok(bridge.Execute(Cmd("counter") with { Delta = 1 }, "host"));
        await Ok(bridge.Execute(Cmd("panic") with { Action = "enable" }, "host"));
        if (!capture) await Ok(bridge.Execute(Cmd("mimicStop"), "host"));
        var aliceFrom = Data<HistoryTimelineReply>(await Ok(a.AsAlice(Cmd("historyTimeline")))).FromFrame!.Value;
        var budgets = bridge.Budgets.Export();
        var part = await bridge.EnterUpdateExit();
        Assert.True(bridge.UpdateExiting);
        Assert.False(bridge.Snapshot.Mimic!.Active);
        // Through JSON, as the hand-over file carries it.
        var carried = JsonSerializer.Deserialize<BridgeHandoff>(JsonSerializer.SerializeToUtf8Bytes(part, SoundpadWeb.Json.Options), SoundpadWeb.Json.Options)!;
        return (carried, session, draft, aliceFrom, Field(clip, "assetId"), budgets);
    }
    private static string Field(Reply r, string name) => Body(r).GetProperty(name).GetString()!;

    [Fact]
    public async Task TheNewProcessContinuesTheSameSessionWithAnUpdateGap()
    {
        var (part, session, draft, aliceFrom, clip, budgets) = await HostAndLeave();
        long exited = part.ExitedUtcMs;
        // The ordinary quit cleanup did not run: the history is on disk, Temp and the journal session stay.
        Assert.True(Directory.Exists(Path.Combine(folder, "history", session)));
        var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), SoundpadWeb.Json.Options)!;
        Assert.True(journal.SessionOpen);
        Assert.Contains(journal.Assets, x => x.Id == clip);
        Interlocked.Add(ref now, 7000); // the new version takes 7 s to start
        await using var b = new Rig(this);
        var bridge = b.Bridge;
        await bridge.ResumeFromHandoff(part);
        await bridge.Poll(true);
        await Task.Delay(20); b.Capture.Pump();
        b.Advance(2);
        Assert.Equal(session, b.Session);
        // Capture resumed on the same output, without a press; the timeline shows the update gap with its wall-clock bounds.
        Assert.True(bridge.Snapshot.Mimic!.Active);
        Assert.Equal(MimicFixture.Source.Name, bridge.Snapshot.Mimic.Source);
        var timeline = Data<HistoryTimelineReply>(await Ok(bridge.Execute(Cmd("historyTimeline"), "host")));
        Assert.True(timeline.Recording);
        var gap = Assert.Single(timeline.Gaps);
        Assert.Equal(HistoryGapReason.Update, gap.Reason);
        Assert.True(gap.WallFromUtcMs >= exited - 1500 && gap.WallFromUtcMs <= exited + 1500, $"from {gap.WallFromUtcMs - exited} to {gap.WallToUtcMs - exited}");
        // Compare the resume boundary with the simulated restart clock, independently of the prior packet timestamp.
        Assert.InRange(gap.WallToUtcMs!.Value, exited + 6500, exited + 8500);
        // Drafts and their pins.
        var drafts = Body(await Ok(bridge.Execute(Cmd("draftList"), "host"))).GetProperty("drafts").EnumerateArray().ToArray();
        Assert.Equal(draft, Assert.Single(drafts).GetProperty("id").GetString());
        Assert.True(bridge.History!.Pins(HistoryAccess.Host).ContainsKey("draft:" + draft));
        // Temp intact with no Recover or Discard question; guests' join time not moved to this launch; shares kept.
        Assert.Null(bridge.Snapshot.TempRecovery);
        var alice = Data<HistoryTimelineReply>(await Ok(b.AsAlice(Cmd("historyTimeline"))));
        Assert.Equal(aliceFrom, alice.FromFrame);
        Assert.Contains(alice.Shared, r => r.StartFrame == S(1) && r.EndFrame == S(3));
        Assert.Equal(Json.Write(budgets), Json.Write(bridge.Budgets.Export()));
        // The hotkey is back on F7 with its length; Kick's session count and Panic carried over.
        var mimic = bridge.Snapshot.Mimic;
        Assert.Equal(("F7", true, 15), (mimic.HotkeyKey, mimic.Hotkey, mimic.HotkeySeconds));
        Assert.Equal(1, bridge.Snapshot.CounterSession);
        Assert.True(bridge.Snapshot.PlaybackSuspended);
        // The session's audience is the same: a guest of this invitation can still use it.
        Assert.True((await bridge.Execute(Cmd("historyTimeline"), "guest", Alice, "peer-a", "audience-1")).Ok);
    }

    [Fact]
    public async Task CaptureThatWasStoppedStaysStoppedAndAMissingOutputIsReportedNotReplaced()
    {
        var (part, session, _, _, _, _) = await HostAndLeave(capture: false);
        await using (var b = new Rig(this))
        {
            await b.Bridge.ResumeFromHandoff(part);
            Assert.False(b.Bridge.Snapshot.Mimic!.Active); // never started without a press
            var gap = Assert.Single(Data<HistoryTimelineReply>(await Ok(b.Bridge.Execute(Cmd("historyTimeline"), "host"))).Gaps);
            Assert.Equal(HistoryGapReason.Stopped, gap.Reason); // nothing was lost to the update
            Assert.Equal(session, b.Session);
            b.Bridge.Dispose();
        }
        // A hand-over that says capture ran on an output that is gone: the session goes on, capture stays off and asks.
        var gone = part with { Capture = new(true, "output:{gone}", "Speakers", false) };
        await using var c = new Rig(this);
        await c.Bridge.ResumeFromHandoff(gone);
        Assert.False(c.Bridge.Snapshot.Mimic!.Active);
        Assert.True(c.Bridge.Snapshot.Mimic.History!.SourceLost);
    }

    [Fact]
    public async Task WithoutAUsableHandOverTheNextStartTakesTheCrashPathAndTheFileIsDeleted()
    {
        var (part, session, _, _, _, _) = await HostAndLeave();
        UpdateHandoffFile.Write(folder, new UpdateHandoff
        {
            Id = new string('e', 32), CreatedUtcMs = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeMilliseconds(), DataDir = folder, OldPid = 0, FromVersion = "2.44.0", ToVersion = "2.44.1",
            Server = new(new string('b', 64), 1, null, null, null, false, null), Bridge = part,
        });
        Assert.Null(UpdateHandoffFile.TryConsume(folder, new string('e', 32), null, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _ => false, out var problem));
        Assert.Contains("too old", problem);
        Assert.False(File.Exists(Path.Combine(folder, UpdateHandoffFile.Name)));
        await using var b = new Rig(this);
        // The ordinary startup: the cleanly closed history is not recovered (as after an ordinary quit), and Temp, whose
        // journal session is still open, waits for the host's Recover or Discard.
        b.Bridge.RecoverHistory();
        await b.Bridge.StartupCleanup(); // before the first poll, as Program.cs runs them
        await b.Bridge.Poll(true);
        Assert.Null(b.Bridge.History);
        Assert.False(Directory.Exists(Path.Combine(folder, "history", session)));
        Assert.True(b.Bridge.Snapshot.TempRecovery > 0, $"temp {b.Bridge.Snapshot.TempRecovery} clips {b.Bridge.TemporaryClipCount}");
        Assert.False(b.Bridge.Snapshot.Mimic!.Active);
    }

    [Fact]
    public async Task UpdateOpsAreTheHostsOnlyAndStrict()
    {
        await using var a = new Rig(this);
        var bridge = a.Bridge;
        foreach (var op in new[] { "updateStatus", "updateCheck", "updateDownload", "updateInstall", "updateLater", "updateSettings" })
        {
            Assert.Contains("Only the host", (await bridge.Execute(Cmd(op), "guest", Alice, "peer-a")).Error);
            Assert.Contains("Only the host", (await bridge.Execute(Cmd(op), "host", null, "peer-a")).Error); // relayed for a guest
        }
        JsonElement B(string json) => JsonDocument.Parse(json).RootElement.Clone();
        Assert.False((await bridge.Execute(Cmd("updateStatus") with { Body = B("""{"x":1}""") }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("updateInstall") with { Body = B("""{"version":"2.44.1"}""") }, "host")).Ok); // not confirmed
        Assert.False((await bridge.Execute(Cmd("updateInstall") with { Body = B("""{"version":"v2.44.1","confirm":true}""") }, "host")).Ok);
        Assert.Contains("not available", (await bridge.Execute(Cmd("updateInstall") with { Body = B("""{"version":"2.44.1","confirm":true}""") }, "host")).Error);
        var status = Body(await Ok(bridge.Execute(Cmd("updateStatus"), "host")));
        Assert.Equal("unsupported", status.GetProperty("update").GetProperty("state").GetString()); // no repository in tests
        await Ok(bridge.Execute(Cmd("updateSettings") with { Body = B("""{"checkAtLaunch":false}""") }, "host"));
        Assert.Contains("\"checkAtLaunch\":false", File.ReadAllText(Path.Combine(folder, "update-settings.v1.json")));
        Assert.False((await bridge.Execute(Cmd("updateSettings") with { Body = B("""{"checkAtLaunch":"no"}""") }, "host")).Ok);
        // A forced offer, Later and the snapshot (guests' copies have it stripped by the host page).
        bridge.Updates.Force(new("available", "", "2.44.1", "https://example.invalid/v2.44.1"));
        await Ok(bridge.Execute(Cmd("updateLater"), "host"));
        Assert.Equal(("available", true), (bridge.Snapshot.Update!.State, bridge.Snapshot.Update.Offered));
        // An install is refused while another Stage H step says it is busy.
        bridge.BlockUpdatesWhile(() => "The host is restoring the library.");
        Assert.Contains("restoring", (await bridge.Execute(Cmd("updateInstall") with { Body = B("""{"version":"2.44.1","confirm":true}""") }, "host")).Error);
    }

    [Fact]
    public async Task InstallingStopsTheOldSessionThroughTheUpdatePathOnly()
    {
        await using var a = new Rig(this);
        var bridge = a.Bridge;
        await bridge.Poll(true);
        await Ok(bridge.Execute(Cmd("beginSession"), "host"));
        await Ok(bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"));
        await Task.Delay(20); a.Capture.Pump(); a.Advance(3);
        var exits = new TaskCompletionSource<UpdateExit>();
        bridge.UpdateExitRequested += exit => { exits.TrySetResult(exit); return Task.CompletedTask; };
        bridge.Updates.Force(new("ready", "", "2.44.1"));
        await Ok(bridge.Execute(Cmd("updateInstall") with { Body = JsonDocument.Parse("""{"version":"2.44.1","confirm":true}""").RootElement.Clone() }, "host"));
        var exit = await exits.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("2.44.1", exit.Version);
        Assert.Null(exit.DownloadPath); // forced: no file
        Assert.Equal("installing", bridge.Snapshot.Update!.State);
        Assert.Contains("No backup was made", bridge.Snapshot.Update.Error); // no backup folder: said, and the update went on
        Assert.True(exit.Bridge.Capture.Running);
        Assert.Contains("installing", (await bridge.Execute(Cmd("updateInstall") with { Body = JsonDocument.Parse("""{"version":"2.44.1","confirm":true}""").RootElement.Clone() }, "host")).Error);
    }
}
