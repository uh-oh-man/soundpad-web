using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// 2.35: one arbitration path for playback intents, Stop and Panic without the gate, the short path for prepared
/// Temp sounds, deferred playback statistics, and a loopback socket that never waits on a command.
/// </summary>
public sealed class PlaybackIntentTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-intent-test-" + Guid.NewGuid().ToString("N"));
    public PlaybackIntentTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static Command Cmd(string op, string? sound = null) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op, sound);
    private static JsonElement Data(Reply reply) => JsonSerializer.SerializeToElement(reply.Data ?? new { }, Json.Options);
    private static bool Flag(Reply reply, string name) => Data(reply).TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? Text(Reply reply, string name) => Data(reply).TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Played(Reply reply) => reply.Ok && Flag(reply, "played") && Text(reply, "epoch") != null;
    private static bool Quiet(Reply reply, string reason) => reply.Ok && Flag(reply, "superseded") && Text(reply, "reason") == reason && !Flag(reply, "played");
    private static string[] Plays(MockSoundpad mock) { lock (mock.Commands) return mock.Commands.Where(c => c.StartsWith("DoPlaySound(")).ToArray(); }
    private static string[] Sent(MockSoundpad mock) { lock (mock.Commands) return [.. mock.Commands]; }
    private static async Task<long> Total(Bridge bridge, string soundId) => Data(await bridge.Execute(Cmd("statistics", soundId), "host")).GetProperty("statistics").GetProperty("total").GetInt64();
    private static byte[] Tone(float step = 0.2f, int samples = 16000) => MimicRing.Wav(Enumerable.Range(0, samples).Select(i => 0.3f * MathF.Sin(i * step)).ToArray(), 16000);
    private JsonElement StateOnDisk()
    {
        // The companion may be replacing the file at this very moment; read again rather than fail the test on that.
        for (int attempt = 0; ; attempt++)
        {
            try { return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(folder, "state.json"))); }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(10); }
        }
    }

    /// <summary>Holds the first request with a given prefix before it reaches the pipe, as a slow Soundpad would.</summary>
    private sealed class HoldingPipe(SoundpadPipe inner) : ISoundpad, IAtomicOwnedEntryRemoval
    {
        private volatile string? prefix;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Arms the hold for the next request starting with this text; can be used again after a release.</summary>
        public string HoldPrefix { set { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); prefix = value; } }
        public bool CanRemoveOwnedAtomically => inner.CanRemoveOwnedAtomically;
        public Task<bool> RemoveOwnedEntry(string path, string expectedCatalogFingerprint) => inner.RemoveOwnedEntry(path, expectedCatalogFingerprint);
        public async Task<string> Request(string command, CancellationToken cancel = default)
        {
            if (prefix is { } held && command.StartsWith(held)) { prefix = null; var release = Release; Entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            return await inner.Request(command, cancel);
        }
    }

    [Fact]
    public async Task OnlyTheNewestOfManyConcurrentPlaysStartsAndTheOthersAreSupersededQuietly()
    {
        await using var mock = new MockSoundpad { LatencyMs = 20 }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; lock (mock.Commands) mock.Commands.Clear();
        // Ten presses on different sounds, faster than Soundpad answers one request; host and guest alternate.
        var commands = Enumerable.Range(0, 10).Select(i => Cmd("play", sounds[i % 4].Id)).ToArray();
        var replies = await Task.WhenAll(commands.Select((c, i) => bridge.Execute(c, i % 2 == 0 ? "host" : "guest")));
        Assert.All(replies, r => Assert.True(r.Ok, r.Error));
        // The newest is what Soundpad plays last (the tenth press is on sounds[1], index 2), and it says so.
        Assert.True(Played(replies[^1])); Assert.Equal("DoPlaySound(2)", Plays(mock)[^1]);
        Assert.Equal(sounds[1].Id, bridge.Snapshot.Playback.SoundId); Assert.Equal(Text(replies[^1], "epoch"), bridge.Snapshot.Playback.Epoch);
        // Every other press either started before the next one arrived or was superseded: none is an error, none
        // waits its turn to play later, and the queue never forms (at most the very first can slip through).
        int started = replies.Count(Played);
        Assert.InRange(started, 1, 2); Assert.Equal(started, Plays(mock).Length);
        Assert.All(replies.Where(r => !Played(r)), r => Assert.True(Quiet(r, "newer")));
        // Statistics count only what started.
        long total = 0; foreach (var sound in sounds) total += await Total(bridge, sound.Id);
        Assert.Equal(started, total);
        // A command id sent again gets the first answer and neither plays nor counts again.
        var again = await bridge.Execute(commands[^1], "guest");
        Assert.True(Played(again)); Assert.Equal(Text(replies[^1], "epoch"), Text(again, "epoch")); Assert.Equal(started, Plays(mock).Length);
        var supersededAgain = await bridge.Execute(commands[3], "guest"); Assert.True(Quiet(supersededAgain, "newer")); Assert.Equal(started, Plays(mock).Length);
    }

    [Fact]
    public async Task AFloodOfStopsSharesRequestsSoTheHostsStopIsAnsweredAtOnce()
    {
        // Review finding (2.35): a guest's page can send 100 Stops a second. Each used to queue its own
        // DoStopSound on the pipe, so the host's Stop waited seconds behind them.
        await using var mock = new MockSoundpad { LatencyMs = 15, DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds;
        Assert.True(Played(await bridge.Execute(Cmd("play", sounds[0].Id), "host")));
        lock (mock.Commands) mock.Commands.Clear();
        var flood = Enumerable.Range(0, 200).Select(_ => bridge.Execute(Cmd("stop"), "guest")).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hostStop = await bridge.Execute(Cmd("stop"), "host").WaitAsync(TimeSpan.FromSeconds(5));
        long hostMs = clock.ElapsedMilliseconds;
        Assert.True(hostStop.Ok, hostStop.Error);
        Assert.All(await Task.WhenAll(flood), r => Assert.True(r.Ok, r.Error));
        int stops = Sent(mock).Count(c => c == "DoStopSound()");
        // One request in flight plus one batch behind it, whatever the size of the flood.
        Assert.InRange(stops, 1, 3);
        Assert.True(hostMs < 1000, $"host Stop took {hostMs} ms behind the flood");
        Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status);
        // A Play after the flood still plays.
        Assert.True(Played(await bridge.Execute(Cmd("play", sounds[1].Id), "host")));
    }

    [Fact]
    public async Task AFloodOfSeeksDoesNotQueueAndAPlayIsNotDelayedByIt()
    {
        // Review finding (2.35): Seeks for the live epoch each cost several pipe requests. A newer one replaces
        // one still waiting, so a flood cannot build a queue a Play would wait behind.
        await using var mock = new MockSoundpad { LatencyMs = 15, DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds;
        var first = await bridge.Execute(Cmd("play", sounds[0].Id), "host"); Assert.True(Played(first));
        var epoch = Text(first, "epoch");
        lock (mock.Commands) mock.Commands.Clear();
        var seeks = Enumerable.Range(0, 100).Select(i => bridge.Execute(Cmd("seek", sounds[0].Id) with { PositionMs = 100 + i, Epoch = epoch }, "guest")).ToArray();
        var pauses = Enumerable.Range(0, 10).Select(_ => bridge.Execute(Cmd("pause", sounds[0].Id) with { Epoch = epoch }, "guest")).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var play = await bridge.Execute(Cmd("play", sounds[1].Id), "host").WaitAsync(TimeSpan.FromSeconds(5));
        long playMs = clock.ElapsedMilliseconds;
        Assert.True(Played(play)); Assert.True(playMs < 1000, $"play took {playMs} ms behind the seeks");
        var answers = await Task.WhenAll(seeks.Concat(pauses));
        Assert.All(answers, r => Assert.True(r.Ok, r.Error));
        // Only a handful of seeks reached Soundpad; the rest were replaced or found their playback gone.
        Assert.InRange(Sent(mock).Count(c => c.StartsWith("DoSeekMs(")), 0, 4);
        Assert.InRange(Sent(mock).Count(c => c == "DoTogglePause()"), 0, 2);
        Assert.Equal(sounds[1].Id, bridge.Snapshot.Playback.SoundId);
    }

    [Fact]
    public async Task StopArrivingWhilePlaysAreQueuedWinsAndNothingPlaysAfterIt()
    {
        await using var mock = new MockSoundpad { LatencyMs = 20, DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds;
        Assert.True(Played(await bridge.Execute(Cmd("play", sounds[0].Id), "host")));
        lock (mock.Commands) mock.Commands.Clear();
        var plays = new[] { bridge.Execute(Cmd("play", sounds[1].Id), "guest"), bridge.Execute(Cmd("region", sounds[2].Id) with { PositionMs = 100, EndMs = 900 }, "host"), bridge.Execute(Cmd("startAt", sounds[3].Id) with { PositionMs = 300 }, "guest") };
        var stop = bridge.Execute(Cmd("stop"), "guest");
        Assert.True((await stop).Ok);
        var replies = await Task.WhenAll(plays);
        Assert.All(replies, r => Assert.True(r.Ok && (Played(r) || Quiet(r, "newer")), r.Error));
        var sent = Sent(mock); int stopped = Array.LastIndexOf(sent, "DoStopSound()");
        Assert.True(stopped >= 0); Assert.DoesNotContain(sent.Skip(stopped), c => c.StartsWith("DoPlaySound(") || c.StartsWith("DoSeekMs("));
        await bridge.Poll(false);
        Assert.Equal("STOPPED", mock.Status); Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status); Assert.Null(bridge.Snapshot.Playback.SoundId);
    }

    [Fact]
    public async Task StopDoesNotWaitBehindALibraryRefreshAndCancelsThePlayQueuedBehindIt()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var inner = new SoundpadPipe(mock.Name); var pipe = new HoldingPipe(inner); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds;
        Assert.True(Played(await bridge.Execute(Cmd("play", sounds[0].Id), "host")));
        // A library refresh holds the gate, stuck in its categories request.
        pipe.HoldPrefix = "GetCategories("; var refresh = bridge.Poll(true); await pipe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var play = bridge.Execute(Cmd("play", sounds[1].Id), "guest");
        var stop = await bridge.Execute(Cmd("stop"), "host").WaitAsync(TimeSpan.FromSeconds(5)); // answered while the refresh still holds the gate
        Assert.True(stop.Ok, stop.Error); Assert.Equal("STOPPED", mock.Status); Assert.False(refresh.IsCompleted);
        // The play that was waiting for the gate was released by the Stop at once, also before the refresh ends.
        Assert.True(Quiet(await play.WaitAsync(TimeSpan.FromSeconds(5)), "newer")); Assert.False(refresh.IsCompleted);
        pipe.Release.TrySetResult(); await refresh;
        Assert.Single(Plays(mock)); Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status);
    }

    [Fact]
    public async Task PanicSuspendsGuestsQuietlyButNotTheHostAndResumeRestoresThem()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; string audience = "fixture-audience";
        Assert.True((await bridge.Execute(Cmd("beginSession") with { AudienceId = audience }, "host")).Ok);
        Task<Reply> Guest(Command c) => bridge.Execute(c, "guest", new(Guid.NewGuid().ToString(), "Alice"), "peer-alice", audience);
        var first = await Guest(Cmd("play", sounds[0].Id)); Assert.True(Played(first)); Assert.False(bridge.Snapshot.PlaybackSuspended);
        // Only the host may panic.
        var refused = await Guest(Cmd("panic") with { Action = "enable" }); Assert.False(refused.Ok); Assert.Equal("Host permission required.", refused.Error); Assert.Equal("PLAYING", mock.Status);
        var panic = await bridge.Execute(Cmd("panic") with { Action = "enable" }, "host");
        Assert.True(panic.Ok, panic.Error); Assert.Equal("STOPPED", mock.Status); Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status);
        Assert.True(bridge.Snapshot.PlaybackSuspended); Assert.Contains("\"playbackSuspended\":true", Json.Write(bridge.Snapshot));
        Assert.True(bridge.Snapshot.Mimic != null && (await bridge.Execute(Cmd("audioAssets"), "host")).Ok); // the session goes on
        // Guests: every playback input is answered quietly and nothing reaches Soundpad.
        lock (mock.Commands) mock.Commands.Clear();
        Assert.True(Quiet(await Guest(Cmd("play", sounds[1].Id)), "suspended"));
        Assert.True(Quiet(await Guest(Cmd("region", sounds[1].Id) with { PositionMs = 0, EndMs = 500 }), "suspended"));
        Assert.True(Quiet(await Guest(Cmd("stop")), "suspended"));
        Assert.True(Quiet(await Guest(Cmd("seek", sounds[0].Id) with { PositionMs = 10, Epoch = Text(first, "epoch") }), "suspended"));
        Assert.True(Quiet(await Guest(Cmd("pause", sounds[0].Id) with { Epoch = Text(first, "epoch") }), "suspended"));
        Assert.DoesNotContain(Sent(mock), c => c.StartsWith("Do"));
        // The host still plays, and a suspended guest cannot cancel the host's sound or its pending start.
        var hostPlay = await bridge.Execute(Cmd("play", sounds[2].Id), "host"); Assert.True(Played(hostPlay)); Assert.Equal("DoPlaySound(3)", Plays(mock)[^1]);
        Assert.True(Quiet(await Guest(Cmd("play", sounds[1].Id)), "suspended")); Assert.Equal(sounds[2].Id, bridge.Snapshot.Playback.SoundId);
        // Resume: guests play again.
        Assert.True((await bridge.Execute(Cmd("panic") with { Action = "disable" }, "host")).Ok); Assert.False(bridge.Snapshot.PlaybackSuspended);
        Assert.True(Played(await Guest(Cmd("play", sounds[1].Id)))); Assert.Equal("DoPlaySound(2)", Plays(mock)[^1]);
        Assert.False((await bridge.Execute(Cmd("panic") with { Action = "sideways" }, "host")).Ok);
        // In memory only: a restart starts unsuspended, and nothing about it is written to state.json.
        Assert.True((await bridge.Execute(Cmd("panic") with { Action = "enable" }, "host")).Ok); await bridge.FlushState();
        Assert.DoesNotContain("uspended", File.ReadAllText(Path.Combine(folder, "state.json")));
        using var restarted = new Bridge(pipe, new StateStore(folder)); Assert.False(restarted.Snapshot.PlaybackSuspended);
    }

    [Fact]
    public async Task ExpiredPlaybackInputIsQuietAndCancelsNothingWhileOtherCommandsStillFail()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var play = await bridge.Execute(Cmd("play", sounds[0].Id), "host"); Assert.True(Played(play));
        Assert.True(Quiet(await bridge.Execute(Cmd("play", sounds[1].Id) with { At = now - 6000 }, "guest"), "expired"));
        Assert.True(Quiet(await bridge.Execute(Cmd("startAt", sounds[1].Id) with { At = now + 6000, PositionMs = 5 }, "host"), "expired"));
        Assert.True(Quiet(await bridge.Execute(Cmd("seek", sounds[0].Id) with { At = now - 6000, PositionMs = 5, Epoch = Text(play, "epoch") }, "host"), "expired"));
        Assert.True(Quiet(await bridge.Execute(Cmd("stop") with { At = now - 31000 }, "host"), "expired"));
        Assert.Single(Plays(mock)); Assert.Equal("PLAYING", mock.Status); Assert.Equal(sounds[0].Id, bridge.Snapshot.Playback.SoundId);
        // Not playback: still an error, as before.
        Assert.False((await bridge.Execute(Cmd("counter") with { Delta = 1, At = now - 31000 }, "host")).Ok);
        // A guest without permission is refused with the reason (not quietly) and cancels nobody's sound.
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(RemotePlayback: false) }, "host")).Ok);
        var denied = await bridge.Execute(Cmd("play", sounds[1].Id), "guest"); Assert.False(denied.Ok); Assert.Equal("Remote playback is disabled.", denied.Error);
        Assert.Single(Plays(mock));
    }

    [Fact]
    public async Task SeekAndPauseForAPlaybackThatWasReplacedOrStoppedAreAnsweredQuietly()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds;
        var first = await bridge.Execute(Cmd("play", sounds[0].Id), "host"); var old = Text(first, "epoch"); Assert.Equal(old, bridge.Snapshot.Playback.Epoch);
        Assert.True((await bridge.Execute(Cmd("seek", sounds[0].Id) with { PositionMs = 1000, Epoch = old }, "guest")).Ok); Assert.Contains("DoSeekMs(1000)", Sent(mock));
        var second = await bridge.Execute(Cmd("play", sounds[1].Id), "guest"); Assert.NotEqual(old, Text(second, "epoch"));
        lock (mock.Commands) mock.Commands.Clear();
        Assert.True(Quiet(await bridge.Execute(Cmd("seek", sounds[0].Id) with { PositionMs = 2000, Epoch = old }, "guest"), "stale"));
        Assert.True(Quiet(await bridge.Execute(Cmd("pause", sounds[0].Id) with { Epoch = old }, "guest"), "stale"));
        Assert.DoesNotContain(Sent(mock), c => c.StartsWith("Do")); Assert.Equal("PLAYING", bridge.Snapshot.Playback.Status);
        // The current playback is still controllable, until it is stopped.
        Assert.True((await bridge.Execute(Cmd("pause", sounds[1].Id) with { Epoch = Text(second, "epoch") }, "host")).Ok); Assert.Equal("PAUSED", bridge.Snapshot.Playback.Status);
        Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok);
        Assert.True(Quiet(await bridge.Execute(Cmd("pause", sounds[1].Id) with { Epoch = Text(second, "epoch") }, "host"), "stale"));
    }

    [Fact]
    public async Task SeekAndPauseDoNotCancelAPendingAutoplay()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var inner = new SoundpadPipe(mock.Name, true); var pipe = new HoldingPipe(inner); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var upload = await bridge.Upload(Tone(), "Source.wav", "Host", true, "host"); Assert.True(upload.Ok, upload.Error);
        var sounds = bridge.Snapshot.Library.Sounds;
        var playing = await bridge.Execute(Cmd("play", sounds[0].Id), "host"); Assert.True(Played(playing));
        // A copy "and play" is being made (its import is held) while someone scrubs and pauses the current sound.
        // Seek and Pause are not intents (they used to count as one): the copy still plays when it is ready.
        pipe.HoldPrefix = "DoAddSound(";
        var copy = bridge.Execute(Cmd("trimAudio") with { AssetId = Text(upload, "assetId"), PositionMs = 0, EndMs = 600, Autoplay = true }, "host");
        await pipe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var seek = bridge.Execute(Cmd("seek", sounds[0].Id) with { PositionMs = 50, Epoch = Text(playing, "epoch") }, "guest");
        var pause = bridge.Execute(Cmd("pause", sounds[0].Id) with { Epoch = Text(playing, "epoch") }, "guest");
        pipe.Release.TrySetResult();
        Assert.True((await seek).Ok); Assert.True((await pause).Ok);
        var made = await copy; Assert.True(made.Ok, made.Error); Assert.True(Played(made));
        Assert.Equal(Text(made, "epoch"), bridge.Snapshot.Playback.Epoch);
        Assert.True(bridge.Snapshot.Library.Sounds.Single(s => s.Id == bridge.Snapshot.Playback.SoundId).Temporary);
    }

    [Fact]
    public async Task ASupersededAutoplayNeverPlaysAfterANewerPlayOrStop()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var inner = new SoundpadPipe(mock.Name, true); var pipe = new HoldingPipe(inner); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var upload = await bridge.Upload(Tone(), "Source.wav", "Host", true, "host"); Assert.True(upload.Ok, upload.Error);
        var sounds = bridge.Snapshot.Library.Sounds; string source = Text(upload, "assetId")!;
        // 1. A newer Stop while the copy is still being imported: the copy is made, and stays silent.
        pipe.HoldPrefix = "DoAddSound(";
        var copy = bridge.Execute(Cmd("trimAudio") with { AssetId = source, PositionMs = 0, EndMs = 600, Autoplay = true }, "host");
        await pipe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await bridge.Execute(Cmd("stop"), "guest").WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        pipe.Release.TrySetResult();
        var made = await copy; Assert.True(made.Ok, made.Error);
        Assert.False(Flag(made, "played")); Assert.NotNull(Text(made, "assetId")); Assert.Null(Text(made, "epoch"));
        Assert.Empty(Plays(mock)); Assert.Equal(2, bridge.Snapshot.Library.Sounds.Count(s => s.Temporary));
        // 2. A newer Play of a library sound while another copy is being imported: only the newer sound plays,
        // and the copy does not take over when it is ready.
        pipe.HoldPrefix = "DoAddSound(";
        var second = bridge.Execute(Cmd("trimAudio") with { AssetId = source, PositionMs = 100, EndMs = 800, Autoplay = true }, "guest", new(Guid.NewGuid().ToString(), "Alice"), "peer-alice");
        await pipe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newer = bridge.Execute(Cmd("play", sounds[1].Id), "host");
        pipe.Release.TrySetResult();
        Assert.True(Played(await newer));
        var secondMade = await second; Assert.True(secondMade.Ok, secondMade.Error); Assert.False(Flag(secondMade, "played"));
        Assert.Equal(new[] { "DoPlaySound(2)" }, Plays(mock)); Assert.Equal(sounds[1].Id, bridge.Snapshot.Playback.SoundId);
        Assert.Equal(3, bridge.Snapshot.Library.Sounds.Count(s => s.Temporary));
        // 3. With nothing newer, "and play" plays the new copy, once, and says which playback it started.
        var third = await bridge.Execute(Cmd("trimAudio") with { AssetId = source, PositionMs = 200, EndMs = 900, Autoplay = true }, "host");
        Assert.True(Played(third)); Assert.Equal(2, Plays(mock).Length); Assert.Equal(Text(third, "epoch"), bridge.Snapshot.Playback.Epoch);
        var playing = bridge.Snapshot.Library.Sounds.Single(s => s.Id == bridge.Snapshot.Playback.SoundId); Assert.True(playing.Temporary); Assert.Equal(Text(third, "assetId"), playing.AssetId);
        // Without autoplay nothing changes: the copy is made and the answer has no "played".
        var plain = await bridge.Execute(Cmd("trimAudio") with { AssetId = source, PositionMs = 300, EndMs = 950 }, "host");
        Assert.True(plain.Ok, plain.Error); Assert.False(Data(plain).TryGetProperty("played", out _)); Assert.Equal(2, Plays(mock).Length);
        // A guest who may not play gets the copy but no playback, and cancels nothing.
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(RemotePlayback: false) }, "host")).Ok);
        var silent = await bridge.Execute(Cmd("trimAudio") with { AssetId = source, PositionMs = 350, EndMs = 990, Autoplay = true }, "guest", new(Guid.NewGuid().ToString(), "Alice"), "peer-alice");
        Assert.True(silent.Ok, silent.Error); Assert.False(Flag(silent, "played")); Assert.Equal(2, Plays(mock).Length); Assert.Equal(Text(third, "epoch"), bridge.Snapshot.Playback.Epoch);
    }

    [Fact]
    public async Task MimicAndPlayAndImportAndPlayStartTheNewClipInOneCommand()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture);
        var vrs = Path.Combine(folder, "vrs"); Directory.CreateDirectory(Path.Combine(vrs, "Clips"));
        string wav = Path.Combine(vrs, "Clips", "HELLO.wav"); File.WriteAllBytes(wav, MimicRing.Wav(Enumerable.Range(0, 57600).Select(i => 0.3f * MathF.Sin(i * 2 * MathF.PI * 440 / 48000)).ToArray(), 48000));
        var hello = Guid.NewGuid();
        File.WriteAllText(Path.Combine(vrs, "library.json"), JsonSerializer.Serialize(new object[] { new { Id = hello, AudioPath = wav, Title = "Hello there", Transcript = "hello there friend", CapturedAt = DateTimeOffset.Now, Duration = 1.2 } }));
        bridge.UseVoiceReplayLibrary(new VoiceReplayLibrary(vrs)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(31));
        var peek = JsonSerializer.Deserialize<MimicPeek>(JsonSerializer.Serialize((await bridge.Execute(Cmd("mimicPeek"), "host")).Data, Json.Options), Json.Options)!;
        long windowStart = peek.End - MimicFixture.Rate * 30L;
        long burst = windowStart + (2 * MimicFixture.Rate - windowStart % (2 * MimicFixture.Rate)) % (2 * MimicFixture.Rate), fromMs = (burst - windowStart) * 1000 / MimicFixture.Rate;
        var clip = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs, EndMs = fromMs + 500, Name = "Said it", Autoplay = true }, "host");
        Assert.True(Played(clip), clip.Error); Assert.Single(Plays(mock));
        var playing = bridge.Snapshot.Library.Sounds.Single(s => s.Id == bridge.Snapshot.Playback.SoundId); Assert.StartsWith("Said it", playing.Name); Assert.Equal(Text(clip, "assetId"), playing.AssetId);
        var imported = await bridge.Execute(Cmd("mimicLibraryImport") with { SourceId = hello.ToString(), Autoplay = true }, "host");
        Assert.True(Played(imported), imported.Error); Assert.Equal(2, Plays(mock).Length); Assert.Equal(Text(imported, "epoch"), bridge.Snapshot.Playback.Epoch);
    }

    [Fact]
    public async Task APreparedTempSoundPlaysOnTheShortPathAndRapidPlaysAreNeverRefusedAsBusy()
    {
        await using var mock = new MockSoundpad { LatencyMs = 5, DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var upload = await bridge.Upload(Tone(), "Clip.wav", "Alice", false, "peer-alice"); Assert.True(upload.Ok, upload.Error);
        var clip = bridge.Snapshot.Library.Sounds.Single(s => s.Temporary); string asset = Text(upload, "assetId")!;
        long journal = bridge.Snapshot.ManagedAudioVersion; lock (mock.Commands) mock.Commands.Clear();
        // Thirty presses at once, by tile (play, cue, region) and by source (audioPlay), from the host and a guest.
        var commands = Enumerable.Range(0, 30).Select(i => (i % 4) switch
        {
            0 => Cmd("play", clip.Id), 1 => Cmd("audioPlay") with { AssetId = asset, Orientation = "normal" },
            2 => Cmd("startAt", clip.Id) with { PositionMs = 100 }, _ => Cmd("region", clip.Id) with { PositionMs = 50, EndMs = 400 },
        }).ToArray();
        var replies = await Task.WhenAll(commands.Select((c, i) => bridge.Execute(c, i % 2 == 0 ? "host" : "guest", new(Guid.NewGuid().ToString(), "Alice"), "peer-alice")));
        Assert.All(replies, r => Assert.True(r.Ok && (Played(r) || Quiet(r, "newer")), r.Error)); // never "Audio preparation is busy"
        Assert.True(Played(replies[^1])); Assert.Equal(asset, Text(replies[^1], "assetId")); Assert.Equal("temporary", Text(replies[^1], "state"));
        Assert.Equal(clip.Id, bridge.Snapshot.Playback.SoundId);
        int started = replies.Count(Played); Assert.Equal(started, Plays(mock).Length); Assert.InRange(started, 1, 3);
        Assert.Equal(started, await Total(bridge, clip.Id));
        // The short path: the sound list and the play, like a library sound. No categories were read, no entry
        // was imported or looked for, and the journal was not written (its revision is unchanged).
        var sent = Sent(mock);
        Assert.DoesNotContain(sent, c => c.StartsWith("GetCategories(") || c.StartsWith("DoAdd"));
        Assert.Equal(journal, bridge.Snapshot.ManagedAudioVersion);
        // A file changed behind the companion's back is not played on trust: the play takes the full preparation, which refuses it.
        var path = Directory.GetFiles(Path.Combine(folder, "managed-audio", "sessions"), "*.wav", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 0x55; File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok); int before = Plays(mock).Length;
        var tampered = await bridge.Execute(Cmd("play", clip.Id), "host"); Assert.False(tampered.Ok); Assert.Equal(before, Plays(mock).Length);
    }

    [Fact]
    public async Task APlayWaitingForPreparationIsReleasedAtOnceWhenANewerIntentArrives()
    {
        var wave = Path.Combine(folder, "library.wav"); File.WriteAllBytes(wave, Tone(0.25f)); var other = Path.Combine(folder, "other.wav"); File.WriteAllBytes(other, Tone(0.35f));
        await using var mock = new MockSoundpad { DurationMs = 60000, AllXml = MockSoundpad.Sounds.Replace(@"C:\Private\airhorn.wav", wave).Replace(@"C:\Private\laugh.wav", other), CategoryXml = MockSoundpad.Categories.Replace(@"C:\Private\airhorn.wav", wave).Replace(@"C:\Private\laugh.wav", other) };
        using var inner = new SoundpadPipe(mock.Name, true); var pipe = new HoldingPipe(inner); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok); var sounds = bridge.Snapshot.Library.Sounds;
        // The first reverse is being prepared (its import is held); a second one waits for its turn.
        pipe.HoldPrefix = "DoAddSound(";
        var first = bridge.Execute(Cmd("reverse", sounds[0].Id), "host"); await pipe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = bridge.Execute(Cmd("reverse", sounds[1].Id), "guest");
        await Task.Delay(50); Assert.False(waiting.IsCompleted);
        // A newer Stop releases the waiting one at once, while the first is still held. Never "busy", never later.
        Assert.True((await bridge.Execute(Cmd("stop"), "host").WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        Assert.True(Quiet(await waiting.WaitAsync(TimeSpan.FromSeconds(5)), "newer")); Assert.False(first.IsCompleted);
        pipe.Release.TrySetResult();
        // Preparation that had begun finishes and is kept, but does not play: its intent is no longer the newest.
        var prepared = await first; Assert.True(prepared.Ok, prepared.Error); Assert.False(Flag(prepared, "played")); Assert.NotNull(Text(prepared, "assetId"));
        Assert.True(Flag(prepared, "superseded")); Assert.Equal("newer", Text(prepared, "reason")); // quiet, and it says what was prepared
        Assert.Empty(Plays(mock)); Assert.Single(bridge.Snapshot.Library.Sounds, s => s.Temporary);
        // The prepared reverse now plays at once, and a third and fourth press in a row are not refused.
        var third = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => bridge.Execute(Cmd("reverse", sounds[0].Id), "guest")));
        Assert.All(third, r => Assert.True(r.Ok && (Played(r) || Quiet(r, "newer")), r.Error)); Assert.True(Played(third[^1]));
    }

    [Fact]
    public async Task UndoOfThePlayingClipStopsThroughTheSameArbitration()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var upload = await bridge.Upload(Tone(), "Clip.wav", "Host", true, "host"); string asset = Text(upload, "assetId")!;
        var played = await bridge.Execute(Cmd("audioPlay") with { AssetId = asset, Orientation = "normal" }, "host"); Assert.True(Played(played));
        var removed = await bridge.Execute(Cmd("removeAudio") with { AssetId = asset, Action = "stopIfPlaying" }, "host");
        Assert.True(removed.Ok, removed.Error);
        // The record follows the stop at once (it used to say "playing" until the next poll).
        Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status); Assert.Null(bridge.Snapshot.Playback.SoundId); Assert.Equal("STOPPED", mock.Status);
        // Clear Temp does the same for a playing Temp sound.
        var second = await bridge.Upload(Tone(0.3f), "Other.wav", "Host", true, "host"); Assert.True(second.Ok, second.Error);
        Assert.True(Played(await bridge.Execute(Cmd("audioPlay") with { AssetId = Text(second, "assetId"), Orientation = "normal" }, "host")));
        var cleared = await bridge.Execute(Cmd("clearTemp"), "host"); Assert.True(cleared.Ok, cleared.Error);
        Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status); Assert.DoesNotContain(bridge.Snapshot.Library.Sounds, s => s.Temporary);
        // And the window-close stop leaves library sounds alone.
        var library = await bridge.Execute(Cmd("play", bridge.Snapshot.Library.Sounds[0].Id), "host"); Assert.True(Played(library));
        await bridge.StopTemporaryPlayback(); Assert.Equal("PLAYING", mock.Status);
    }

    [Fact]
    public async Task CounterIsOnDiskBeforeItsAnswerAndDeduplicatesAcrossRestart()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var up = Cmd("counter") with { Delta = 1 };
        Assert.True((await bridge.Execute(up, "host")).Ok);
        // Read straight after the answer, with no flush and no wait: the count and the receipt are already there.
        var disk = StateOnDisk(); Assert.Equal(1, disk.GetProperty("counter").GetInt32()); Assert.True(disk.GetProperty("receipts").TryGetProperty(up.Id, out _));
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestCounter: true) }, "host")).Ok);
        Assert.True(StateOnDisk().GetProperty("permissions").GetProperty("guestCounter").GetBoolean());
        // A play in between (deferred) and another durable change: the later durable write is not undone by the flush.
        Assert.True((await bridge.Execute(Cmd("play", bridge.Snapshot.Library.Sounds[0].Id), "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("counter") with { Delta = 1 }, "guest")).Ok); Assert.Equal(2, StateOnDisk().GetProperty("counter").GetInt32());
        await Task.Delay(700); Assert.Equal(2, StateOnDisk().GetProperty("counter").GetInt32());
        using var restarted = new Bridge(pipe, new StateStore(folder));
        Assert.True((await restarted.Execute(up, "host")).Ok); Assert.Equal(2, restarted.Snapshot.Counter);
        // schemaVersion is unchanged and nothing new is required of state.json (rolling back to 2.3.1 must keep working).
        Assert.Equal(2, StateOnDisk().GetProperty("schemaVersion").GetInt32());
        Assert.Equal(new[] { "activity", "counter", "iconFiles", "icons", "identityKey", "permissions", "receipts", "schemaVersion", "spotifyEnabled", "statistics", "statisticsVersion", "website" }, StateOnDisk().EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task PlaybackStatisticsReachTheDiskAfterTheDeferredWriteAndOnACleanExit()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        string first, second;
        long Stored(string id) => StateOnDisk().GetProperty("statistics").TryGetProperty(id, out var stats) ? stats.GetProperty("total").GetInt64() : 0;
        using (var bridge = new Bridge(pipe, new StateStore(folder)))
        {
            await bridge.Poll(true); first = bridge.Snapshot.Library.Sounds[0].Id; second = bridge.Snapshot.Library.Sounds[1].Id;
            var play = Cmd("play", first); Assert.True(Played(await bridge.Execute(play, "guest", new(Guid.NewGuid().ToString(), "Alice"))));
            Assert.Equal(1, await Total(bridge, first)); // in memory at once
            // On disk a moment later, without anything else happening.
            for (int i = 0; i < 40 && Stored(first) == 0; i++) await Task.Delay(50);
            Assert.Equal(1, Stored(first)); Assert.True(StateOnDisk().GetProperty("receipts").TryGetProperty(play.Id, out _)); Assert.Equal(1, StateOnDisk().GetProperty("activity").GetArrayLength());
            // A play right before a clean exit is written by the exit itself.
            Assert.True(Played(await bridge.Execute(Cmd("play", second), "host")));
            await bridge.CleanupSession(); Assert.Equal(1, Stored(second));
            Assert.True(Played(await bridge.Execute(Cmd("play", second), "host")));
        }
        Assert.Equal(2, Stored(second)); // Dispose wrote the last one
        using var restarted = new Bridge(pipe, new StateStore(folder)); Assert.Equal(2, await Total(restarted, second)); Assert.Equal(1, await Total(restarted, first));
    }

    [Fact]
    public void AStaleDeferredImageNeverReplacesANewerDurableSave()
    {
        var store = new StateStore(folder); var state = store.Load();
        state.Counter = 1; var older = store.Capture(state);
        state.Counter = 2; store.Save(state);
        Assert.False(store.Commit(older, pruneIcons: false, createFolder: false)); Assert.Equal(2, store.Load().Counter);
        state.Counter = 3; Assert.True(store.Commit(store.Capture(state), pruneIcons: false, createFolder: false)); Assert.Equal(3, store.Load().Counter);
        // A deferred write never brings back a data folder that was removed.
        var gone = new StateStore(Path.Combine(folder, "gone")); var image = gone.Capture(new DiskState());
        Assert.False(gone.Commit(image, pruneIcons: false, createFolder: false)); Assert.False(Directory.Exists(Path.Combine(folder, "gone")));
    }

    [Fact]
    public async Task ThePollLoopSurvivesSoundpadRefusingTheStopAtTheEndOfARegion()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sound = bridge.Snapshot.Library.Sounds[0];
        Assert.True(Played(await bridge.Execute(Cmd("region", sound.Id) with { PositionMs = 100, EndMs = 250 }, "host")));
        mock.RejectPrefix = "DoStopSound"; await Task.Delay(250);
        await bridge.Poll(false); await bridge.Poll(false); // used to throw InvalidDataException, which ended the companion
        Assert.True(bridge.Snapshot.Available); Assert.Contains("DoStopSound()", Sent(mock));
        mock.RejectPrefix = null; await bridge.Poll(false);
        Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status);
        // The same for a refused seek at the end of a repeating region.
        Assert.True(Played(await bridge.Execute(Cmd("region", sound.Id) with { PositionMs = 100, EndMs = 250, Repeat = true }, "host")));
        mock.RejectPrefix = "DoSeekMs"; await Task.Delay(250); await bridge.Poll(false); mock.RejectPrefix = null;
        Assert.True(bridge.Snapshot.Available); Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok);
    }

    [Fact]
    public async Task ARequestWithdrawnBeforeItIsSentNeverReachesSoundpadAndKeepsTheConnection()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        Assert.Equal("R-200", await pipe.Request("IsAlive()"));
        using var withdrawn = new CancellationTokenSource(); withdrawn.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipe.Request("DoPlaySound(1)", withdrawn.Token));
        Assert.Equal("R-200", await pipe.Request("IsAlive()"));
        Assert.Equal(new[] { "IsAlive()", "IsAlive()" }, Sent(mock)); Assert.Equal("STOPPED", mock.Status);
    }

    [Fact]
    public async Task GateLanesOrderWaitersAndACancelledWaiterLeavesAtOnce()
    {
        var gate = new PriorityGate(int.MaxValue, int.MaxValue); Assert.True(gate.TryEnter()); Assert.False(gate.TryEnter());
        var background = gate.Enter(PriorityGate.Lane.Background); var normal = gate.Enter(PriorityGate.Lane.Normal);
        var urgent = gate.Enter(PriorityGate.Lane.Urgent); var later = gate.Enter(PriorityGate.Lane.Urgent); var dropped = gate.Enter(PriorityGate.Lane.Urgent);
        Assert.True(gate.Cancel(dropped)); Assert.False(await dropped.Entered); Assert.False(gate.Cancel(dropped));
        gate.Release(); Assert.True(await urgent.Entered.WaitAsync(TimeSpan.FromSeconds(2))); Assert.False(later.Entered.IsCompleted); Assert.False(gate.Cancel(urgent)); // it holds the gate now
        gate.Release(); Assert.True(await later.Entered.WaitAsync(TimeSpan.FromSeconds(2))); Assert.False(normal.Entered.IsCompleted);
        gate.Release(); Assert.True(await normal.Entered.WaitAsync(TimeSpan.FromSeconds(2))); Assert.False(background.Entered.IsCompleted);
        gate.Release(); Assert.True(await background.Entered.WaitAsync(TimeSpan.FromSeconds(2)));
        gate.Release();
        // An ordinary command that has waited long enough (here: at all) gets one turn between urgent holders, never two in a row.
        gate = new PriorityGate(0, int.MaxValue); Assert.True(gate.TryEnter());
        var old = gate.Enter(PriorityGate.Lane.Normal); var older = gate.Enter(PriorityGate.Lane.Normal);
        var press = gate.Enter(PriorityGate.Lane.Urgent); var press2 = gate.Enter(PriorityGate.Lane.Urgent);
        gate.Release(); Assert.True(await old.Entered.WaitAsync(TimeSpan.FromSeconds(2))); Assert.False(press.Entered.IsCompleted);
        gate.Release(); Assert.True(await press.Entered.WaitAsync(TimeSpan.FromSeconds(2))); Assert.False(older.Entered.IsCompleted);
        gate.Release(); Assert.True(await older.Entered.WaitAsync(TimeSpan.FromSeconds(2)));
        gate.Release(); Assert.True(await press2.Entered.WaitAsync(TimeSpan.FromSeconds(2)));
        gate.Release(); Assert.True(gate.TryEnter()); Assert.False(await gate.EnterWithin(PriorityGate.Lane.Background, 30)); gate.Release(); Assert.True(await gate.EnterWithin(PriorityGate.Lane.Background, 30));
    }

    private static async Task<ClientWebSocket> Connect(LocalServer server)
    {
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", "https://example.test");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/bridge"), CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes(Json.Write(new { token = server.Token, protocol = 2 })), WebSocketMessageType.Text, true, CancellationToken.None);
        return socket;
    }
    private static async Task<JsonElement> Next(ClientWebSocket socket, CancellationToken cancel)
    {
        using var data = new MemoryStream(); var buffer = new byte[16384]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer, cancel); data.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(data.ToArray());
    }
    private static Task Send(ClientWebSocket socket, string actor, Command command) => socket.SendAsync(Encoding.UTF8.GetBytes(Json.Write(new { actor, command })), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public async Task LoopbackKeepsReadingWhileAStopWaitsForSoundpad()
    {
        await using var mock = new MockSoundpad(); using var inner = new SoundpadPipe(mock.Name); var pipe = new HoldingPipe(inner); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        await using var server = new LocalServer(bridge, "https://example.test"); await server.Start();
        using var socket = await Connect(server); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stop = Cmd("stop"); var counter = Cmd("counter") with { Delta = 1 }; var statistics = Cmd("statistics");
        pipe.HoldPrefix = "DoStopSound";
        await Send(socket, "host", stop); await pipe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The Stop is stuck in Soundpad. Later messages are still read and answered (the loop used to wait for it).
        await Send(socket, "host", counter); await Send(socket, "guest", statistics);
        var answered = new List<string>();
        while (answered.Count < 2)
        {
            var message = await Next(socket, timeout.Token);
            if (message.GetProperty("type").GetString() == "ack") { Assert.True(message.GetProperty("ok").GetBoolean()); answered.Add(message.GetProperty("id").GetString()!); }
        }
        Assert.Equal(new[] { counter.Id, statistics.Id }.Order(), answered.Order()); Assert.Equal(1, bridge.Snapshot.Counter);
        pipe.Release.TrySetResult();
        while (true)
        {
            var message = await Next(socket, timeout.Token);
            if (message.GetProperty("type").GetString() == "ack") { Assert.Equal(stop.Id, message.GetProperty("id").GetString()); Assert.True(message.GetProperty("ok").GetBoolean()); break; }
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task LoopbackNeverRateLimitsABurstOfPlaybackControlButStillLimitsQueries()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        await using var server = new LocalServer(bridge, "https://example.test"); await server.Start();
        using var socket = await Connect(server); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sounds = bridge.Snapshot.Library.Sounds;
        // A hundred control messages at once from two people: plays, cue starts, regions and stops.
        var burst = Enumerable.Range(0, 100).Select(i => (i % 10) switch
        {
            9 => Cmd("stop"), 3 or 7 => Cmd("startAt", sounds[i % 4].Id) with { PositionMs = 50 }, 5 => Cmd("region", sounds[i % 4].Id) with { PositionMs = 10, EndMs = 600 }, _ => Cmd("play", sounds[i % 4].Id),
        }).ToArray();
        for (int i = 0; i < burst.Length; i++) await Send(socket, i % 2 == 0 ? "host" : "guest", burst[i]);
        var acks = new Dictionary<string, JsonElement>();
        while (acks.Count < burst.Length)
        {
            var message = await Next(socket, timeout.Token);
            if (message.GetProperty("type").GetString() == "ack") acks[message.GetProperty("id").GetString()!] = message;
        }
        Assert.All(burst, c => { var ack = acks[c.Id]; Assert.True(ack.GetProperty("ok").GetBoolean(), ack.TryGetProperty("error", out var error) ? error.GetString() : null); });
        // The last one is a Stop: nothing plays after the burst.
        await bridge.Poll(false); Assert.Equal("STOPPED", mock.Status);
        // Queries keep their limit: abuse protection for the expensive classes stays.
        var queries = Enumerable.Range(0, 60).Select(_ => Cmd("statistics")).ToArray();
        foreach (var query in queries) await Send(socket, "guest", query);
        int limited = 0, seen = 0;
        while (seen < queries.Length)
        {
            var message = await Next(socket, timeout.Token);
            if (message.GetProperty("type").GetString() != "ack") continue;
            seen++; if (!message.GetProperty("ok").GetBoolean() && message.GetProperty("error").GetString() == "Too many requests. Try again shortly.") limited++;
        }
        Assert.True(limited >= 20, $"only {limited} of 60 queries were limited");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
