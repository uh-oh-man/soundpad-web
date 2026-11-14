using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// 2.37: exact cue starts and regions from reusable slot files in the Temp category, through the same newest-intent
/// arbitration as every other start, with the playback still reported as the original sound.
/// </summary>
public sealed class ExactPlaybackTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-exact-test-" + Guid.NewGuid().ToString("N"));
    public ExactPlaybackTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);

    private static Command Cmd(string op, string? sound = null) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op, sound);
    private static JsonElement Data(Reply reply) => JsonSerializer.SerializeToElement(reply.Data ?? new { }, Json.Options);
    private static bool Flag(Reply reply, string name) => Data(reply).TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? Text(Reply reply, string name) => Data(reply).TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Played(Reply reply) => reply.Ok && Flag(reply, "played") && Text(reply, "epoch") != null;
    private static bool Quiet(Reply reply, string reason) => reply.Ok && Flag(reply, "superseded") && Text(reply, "reason") == reason && !Flag(reply, "played");
    private static string[] Sent(MockSoundpad mock) { lock (mock.Commands) return [.. mock.Commands]; }
    private static void Clear(MockSoundpad mock) { lock (mock.Commands) mock.Commands.Clear(); }
    private static MockSoundpad.SlotPlay[] SlotPlays(MockSoundpad mock) { lock (mock.SlotPlays) return [.. mock.SlotPlays]; }
    private static async Task<long> Total(Bridge bridge, string soundId) => Data(await bridge.Execute(Cmd("statistics", soundId), "host")).GetProperty("statistics").GetProperty("total").GetInt64();

    private sealed record Fixture(string Path, int Rate, int Channels, short[] Samples)
    {
        public long Frames => Samples.Length / Channels;
        /// <summary>The source's own samples of [startMs, endMs) (to the end when endMs is null), as the slice must hold them.</summary>
        public short[] Range(long startMs, long? endMs)
        {
            long from = SliceRenderer.FrameOf(startMs, Rate), to = endMs is long end ? Math.Min(SliceRenderer.FrameOf(end, Rate), Frames) : Frames;
            return Samples[(int)(from * Channels)..(int)(to * Channels)];
        }
    }
    /// <summary>Noise at a given rate and channel count: every sample differs, so an offset of one frame would show.</summary>
    private Fixture Make(string name, int rate, int channels, int ms)
    {
        var random = new Random(rate * 10 + channels);
        var samples = Enumerable.Range(0, rate * ms / 1000 * channels).Select(_ => (short)random.Next(-20000, 20000)).ToArray();
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, SliceWav.Encode(samples.Select(s => s / 32768f).ToArray(), rate, channels));
        return new(path, rate, channels, samples);
    }
    private static MockSoundpad Mock(params string[] paths)
    {
        var all = new XElement("Soundlist", paths.Select((p, i) => new XElement("Sound", new XAttribute("index", i + 1), new XAttribute("url", p), new XAttribute("title", Path.GetFileNameWithoutExtension(p)))));
        var cats = new XElement("Categories", new XElement("Category", new XAttribute("index", 2), new XAttribute("name", "Fixtures"), paths.Select((p, i) => new XElement("Sound", new XAttribute("index", i + 1), new XAttribute("url", p)))));
        return new MockSoundpad { AllXml = all.ToString(), CategoryXml = cats.ToString(), FollowFileLength = true };
    }
    /// <summary>
    /// The lazy registration as it happens in a session: the first cue start plays the old way at once and asks for
    /// slots; they are registered at the next quiet moment, never while something plays.
    /// </summary>
    private static async Task Ready(Bridge bridge, MockSoundpad mock, Sound sound)
    {
        Assert.False(bridge.SlotsReady);
        var first = await bridge.Execute(Cmd("startAt", sound.Id) with { PositionMs = 100 }, "host");
        Assert.True(Played(first), first.Error); Assert.Contains("DoSeekMs(100)", Sent(mock));
        int added = Sent(mock).Count(c => c.StartsWith("DoAdd"));
        for (int i = 0; i < 4; i++) { await Task.Delay(100); await bridge.Poll(false); }
        Assert.False(bridge.SlotsReady); // still playing: nothing is added to Soundpad meanwhile
        Assert.Equal(added, Sent(mock).Count(c => c.StartsWith("DoAdd")));
        Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok);
        for (int i = 0; i < 60 && !bridge.SlotsReady; i++) { await Task.Delay(50); await bridge.Poll(false); }
        Assert.True(bridge.SlotsReady);
    }
    private static bool HasTemp(MockSoundpad mock) => XDocument.Parse(mock.CategoryXml).Descendants("Category").Any(c => ((string?)c.Attribute("name"))?.StartsWith("Temp - Soundpad Web") == true);

    [Fact]
    public async Task CueStartsAndRegionsPlayExactlyTheirFramesFromASlotAt16And48kHz()
    {
        var low = Make("low.wav", 16000, 1, 2000); var high = Make("high.wav", 48000, 2, 1500);
        await using var mock = Mock(low.Path, high.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; Assert.Equal(2, sounds.Length);
        await Ready(bridge, mock, sounds[0]);
        // The slots are the app's own: not in the library the website sees, in no category, not playable by id.
        var library = bridge.Snapshot.Library;
        Assert.Equal(sounds.Select(s => s.Id), library.Sounds.Select(s => s.Id));
        static IEnumerable<Category> All(Category c) => c.Children.SelectMany(All).Prepend(c);
        Assert.All(library.Categories.SelectMany(All), c => Assert.All(c.SoundIds, id => Assert.Contains(id, sounds.Select(s => s.Id))));
        Assert.Equal(3, XDocument.Parse(mock.AllXml).Root!.Elements("Sound").Count(e => SlotPool.IsSlotFile((string)e.Attribute("url")!)));
        foreach (var slot in bridge.SlotPaths) Assert.Equal(SlotFiles.LongPath(slot), slot);
        // slots.json records them next to the files (never in state.json or the journal).
        using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(bridge.SlotPaths[0])!, "slots.json"))))
            Assert.Equal(bridge.SlotPaths, record.RootElement.GetProperty("slots").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.DoesNotContain("slot-", File.ReadAllText(Path.Combine(folder, "state.json")));
        Assert.DoesNotContain("slot-", File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")));

        foreach (var (fixture, sound) in new[] { (low, sounds[0]), (high, sounds[1]) })
        {
            int before = SlotPlays(mock).Length; Clear(mock);
            var cue = await bridge.Execute(Cmd("startAt", sound.Id) with { PositionMs = 250 }, "host");
            Assert.True(Played(cue), cue.Error);
            var played = SlotPlays(mock)[before..]; Assert.Single(played);
            Assert.Equal((fixture.Rate, fixture.Channels), (played[0].Rate, played[0].Channels));
            Assert.Equal(fixture.Range(250, null), SliceWav.Pcm16(played[0].Bytes));
            var region = await bridge.Execute(Cmd("region", sound.Id) with { PositionMs = 300, EndMs = 700 }, "guest");
            Assert.True(Played(region), region.Error);
            played = SlotPlays(mock)[before..]; Assert.Equal(2, played.Length);
            Assert.Equal(fixture.Range(300, 700), SliceWav.Pcm16(played[1].Bytes));
            // No play-then-seek: the slot holds nothing but the wanted frames.
            Assert.DoesNotContain(Sent(mock), c => c.StartsWith("DoSeekMs("));
            Assert.DoesNotContain(Sent(mock), c => c == $"DoPlaySound({Array.IndexOf(sounds, sound) + 1})");
            // What the website sees is the original sound: its id, its duration, its own positions.
            var now = bridge.Snapshot.Playback;
            Assert.Equal((sound.Id, "PLAYING", Text(region, "epoch")), (now.SoundId, now.Status, now.Epoch));
            Assert.Equal((long)Math.Round(fixture.Frames * 1000.0 / fixture.Rate), now.DurationMs);
            Assert.Equal(700, now.RegionEndMs); Assert.InRange(now.PositionMs!.Value, 300, 700);
            await Task.Delay(120); await bridge.Poll(false);
            now = bridge.Snapshot.Playback; Assert.Equal(sound.Id, now.SoundId); Assert.InRange(now.PositionMs!.Value, 360, 700);
            Assert.Equal((long)Math.Round(fixture.Frames * 1000.0 / fixture.Rate), now.DurationMs);
        }
        Assert.Equal(4, bridge.ExactStarts);
    }

    [Fact]
    public async Task ARepeatingRegionPlaysItsSlotAgainWithoutASeekAndCountsTheOriginalOnce()
    {
        var tone = Make("tone.wav", 16000, 1, 1000); var other = Make("other.wav", 48000, 2, 1000);
        await using var mock = Mock(tone.Path, other.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; await Ready(bridge, mock, sounds[1]);
        long before = await Total(bridge, sounds[0].Id); Clear(mock); int first = SlotPlays(mock).Length;
        var region = await bridge.Execute(Cmd("region", sounds[0].Id) with { PositionMs = 100, EndMs = 250, Repeat = true }, "guest", new(Guid.NewGuid().ToString(), "Alice"));
        Assert.True(Played(region), region.Error);
        // Poll often, as the companion does near a slice's end, until it has looped a few times; a fixed number of
        // 60 ms polls was too few on a slow CI runner (2.37.0), where a stretched delay skipped the reading near the end.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (SlotPlays(mock).Length - first < 3 && clock.ElapsedMilliseconds < 8000) { await Task.Delay(20); await bridge.Poll(false); }
        var plays = SlotPlays(mock)[first..];
        Assert.True(plays.Length >= 3, $"only {plays.Length} plays of the slot");
        // The same slot, holding the same slice every time: it is played again, never rewritten or sought.
        Assert.Single(plays.Select(p => p.Index).Distinct()); Assert.Single(plays.Select(p => p.Hash).Distinct());
        Assert.Equal(tone.Range(100, 250), SliceWav.Pcm16(plays[^1].Bytes));
        Assert.DoesNotContain(Sent(mock), c => c.StartsWith("DoSeekMs(") || c.StartsWith("DoStopSound"));
        var now = bridge.Snapshot.Playback;
        Assert.Equal((sounds[0].Id, Text(region, "epoch"), true, 250L), (now.SoundId, now.Epoch, now.Repeat, now.RegionEndMs));
        Assert.True(now.PositionRevision >= plays.Length - 1);
        // Statistics and activity count the original sound, once, however often the region repeats; never a slot.
        Assert.Equal(before + 1, await Total(bridge, sounds[0].Id));
        var recent = Data(await bridge.Execute(Cmd("statistics"), "host")).GetProperty("recent").EnumerateArray().Select(a => a.GetProperty("soundId").GetString()).ToArray();
        Assert.All(recent, id => Assert.Contains(id, sounds.Select(s => s.Id)));
        // Stop ends the repeat: nothing plays again.
        Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok); int stopped = SlotPlays(mock).Length;
        for (int i = 0; i < 5; i++) { await Task.Delay(60); await bridge.Poll(false); }
        Assert.Equal(stopped, SlotPlays(mock).Length); Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status);
        // A region that does not repeat simply ends with its slot, exactly, and the record says stopped.
        Assert.True(Played(await bridge.Execute(Cmd("region", sounds[0].Id) with { PositionMs = 100, EndMs = 200 }, "host")));
        for (int i = 0; i < 5; i++) { await Task.Delay(80); await bridge.Poll(false); }
        Assert.Equal(stopped + 1, SlotPlays(mock).Length); Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status); Assert.Null(bridge.Snapshot.Playback.SoundId);
    }

    [Fact]
    public async Task ANewerPlayOrAStopArrivingWhileASliceRendersWinsAndTheSliceNeverPlays()
    {
        var a = Make("a.wav", 48000, 2, 1500); var b = Make("b.wav", 16000, 1, 1500);
        await using var mock = Mock(a.Path, b.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; await Ready(bridge, mock, sounds[1]);
        using var release = new ManualResetEventSlim();
        PcmCache Held() => new(PcmCache.DefaultByteBudget, (path, max, cancel) => { release.Wait(cancel); return PcmCache.Decode(path, max, cancel); });
        bridge.UsePcmCache(Held()); int slots = SlotPlays(mock).Length; Clear(mock);
        var region = bridge.Execute(Cmd("region", sounds[0].Id) with { PositionMs = 100, EndMs = 600 }, "host");
        await Task.Delay(40);
        var play = await bridge.Execute(Cmd("play", sounds[1].Id), "guest");
        Assert.True(Played(play), play.Error); Assert.True(Quiet(await region, "newer"));
        release.Set(); await Task.Delay(300); await bridge.Poll(false);
        Assert.Equal(slots, SlotPlays(mock).Length);
        Assert.Equal(new[] { "DoPlaySound(2)" }, Sent(mock).Where(c => c.StartsWith("DoPlaySound(") || c.StartsWith("DoSeekMs(")).ToArray());
        Assert.Equal(sounds[1].Id, bridge.Snapshot.Playback.SoundId);

        // Stop while a cue start renders: nothing plays after it.
        release.Reset(); bridge.UsePcmCache(Held()); Clear(mock);
        var cue = bridge.Execute(Cmd("startAt", sounds[1].Id) with { PositionMs = 200 }, "guest");
        await Task.Delay(40);
        Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok);
        Assert.True(Quiet(await cue, "newer"));
        release.Set(); await Task.Delay(300); await bridge.Poll(false);
        Assert.Equal(slots, SlotPlays(mock).Length);
        Assert.DoesNotContain(Sent(mock), c => c.StartsWith("DoPlaySound(") || c.StartsWith("DoSeekMs("));
        Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status);
    }

    [Fact]
    public async Task ASlowFirstDecodeStartsTheOldWayAtOnceNeverSwapsAndTheNextClickIsExact()
    {
        var a = Make("a.wav", 48000, 2, 2000); var b = Make("b.wav", 16000, 1, 1000);
        await using var mock = Mock(a.Path, b.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; await Ready(bridge, mock, sounds[1]);
        using var release = new ManualResetEventSlim(); int decodes = 0;
        bridge.UsePcmCache(new(PcmCache.DefaultByteBudget, (path, max, cancel) => { Interlocked.Increment(ref decodes); release.Wait(cancel); return PcmCache.Decode(path, max, cancel); }));
        int slots = SlotPlays(mock).Length; Clear(mock);
        var clock = Stopwatch.StartNew();
        var cue = await bridge.Execute(Cmd("startAt", sounds[0].Id) with { PositionMs = 500 }, "host");
        long waited = clock.ElapsedMilliseconds;
        // The old way, about 150 ms after the click (never an error), and the decode goes on for the next click.
        Assert.True(Played(cue), cue.Error); Assert.InRange(waited, 130, 1500);
        Assert.Equal(new[] { "DoPlaySound(1)", "DoSeekMs(500)" }, Sent(mock).Where(c => c.StartsWith("DoPlaySound(") || c.StartsWith("DoSeekMs(")).ToArray());
        release.Set(); await Task.Delay(300); await bridge.Poll(false);
        // No swap to a slice mid-playback.
        Assert.Equal(slots, SlotPlays(mock).Length); Assert.Equal(sounds[0].Id, bridge.Snapshot.Playback.SoundId);
        Assert.Equal(2, bridge.ExactFallbacks); // this one, and the registration click in Ready
        var again = await bridge.Execute(Cmd("startAt", sounds[0].Id) with { PositionMs = 500 }, "host");
        Assert.True(Played(again)); Assert.Equal(slots + 1, SlotPlays(mock).Length); Assert.Equal(1, decodes);
        Assert.Equal(a.Range(500, null), SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
    }

    [Fact]
    public async Task ThePlayingSlotIsNeverRewrittenAndLockedSlotsFallBackQuietly()
    {
        var a = Make("a.wav", 48000, 2, 1500); var b = Make("b.wav", 16000, 1, 1500);
        await using var mock = Mock(a.Path, b.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; var fixtures = new[] { a, b }; await Ready(bridge, mock, sounds[1]);
        int first = SlotPlays(mock).Length;
        for (int i = 0; i < 7; i++)
        {
            var reply = await bridge.Execute(Cmd("region", sounds[i % 2].Id) with { PositionMs = 100 + i * 10, EndMs = 1200 }, i % 2 == 0 ? "host" : "guest");
            Assert.True(Played(reply), reply.Error);
            Assert.Equal(fixtures[i % 2].Range(100 + i * 10, 1200), SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
        }
        var indexes = SlotPlays(mock)[first..].Select(p => p.Index).ToArray();
        Assert.Equal(7, indexes.Length);
        for (int i = 1; i < indexes.Length; i++) Assert.NotEqual(indexes[i - 1], indexes[i]); // alternating, never the one playing
        // The playing slot is held open the way Soundpad holds it: it really cannot be written now.
        var playing = SlotPlays(mock)[^1].Path;
        Assert.False((await SlotPool.WriteInPlace(playing, SliceWav.Silence(10, 48000, 2), 1, 1, default)).Ok);

        // Another program holds one of the free slots: the slice goes to the last one.
        var others = bridge.SlotPaths.Where(p => !p.Equals(playing, StringComparison.OrdinalIgnoreCase)).ToArray();
        using (new FileStream(others[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(Played(await bridge.Execute(Cmd("startAt", sounds[0].Id) with { PositionMs = 300 }, "host")));
            Assert.Equal(others[1], SlotPlays(mock)[^1].Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(a.Range(300, null), SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
        }
        // Every slot locked beyond its retries: the old way, at once and without an error.
        int slots = SlotPlays(mock).Length; Clear(mock);
        var locks = bridge.SlotPaths.Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read)).ToArray();
        try
        {
            var fallback = await bridge.Execute(Cmd("startAt", sounds[1].Id) with { PositionMs = 400 }, "guest");
            Assert.True(Played(fallback), fallback.Error);
            Assert.Equal(new[] { "DoPlaySound(2)", "DoSeekMs(400)" }, Sent(mock).Where(c => c.StartsWith("DoPlaySound(") || c.StartsWith("DoSeekMs(")).ToArray());
            Assert.Equal(slots, SlotPlays(mock).Length);
        }
        finally { foreach (var file in locks) file.Dispose(); }
        // Free again: the next one is exact.
        Assert.True(Played(await bridge.Execute(Cmd("startAt", sounds[1].Id) with { PositionMs = 400 }, "guest")));
        Assert.Equal(b.Range(400, null), SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
    }

    [Fact]
    public async Task SeekInsideASliceStartsANewSliceThereAndPauseStaysNative()
    {
        var a = Make("a.wav", 48000, 2, 2000); var b = Make("b.wav", 16000, 1, 1000);
        await using var mock = Mock(a.Path, b.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; await Ready(bridge, mock, sounds[1]);
        var cue = await bridge.Execute(Cmd("region", sounds[0].Id) with { PositionMs = 200, EndMs = 900, Repeat = true }, "host");
        Assert.True(Played(cue)); var epoch = Text(cue, "epoch"); Clear(mock);
        Assert.True((await bridge.Execute(Cmd("pause", sounds[0].Id) with { Epoch = epoch }, "guest")).Ok);
        Assert.Equal("PAUSED", bridge.Snapshot.Playback.Status); Assert.Contains("DoTogglePause()", Sent(mock));
        int slots = SlotPlays(mock).Length;
        var seek = await bridge.Execute(Cmd("seek", sounds[0].Id) with { Epoch = epoch, PositionMs = 1500 }, "guest");
        Assert.True(seek.Ok, seek.Error); Assert.False(Flag(seek, "superseded"));
        Assert.Equal(slots + 1, SlotPlays(mock).Length);
        Assert.Equal(a.Range(1500, null), SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
        Assert.DoesNotContain(Sent(mock), c => c.StartsWith("DoSeekMs("));
        Assert.Equal(2, Sent(mock).Count(c => c == "DoTogglePause()")); // paused stays paused at its new position
        var now = bridge.Snapshot.Playback;
        Assert.Equal((sounds[0].Id, epoch, "PAUSED", 1500L, (long?)null, false), (now.SoundId, now.Epoch, now.Status, now.PositionMs!.Value, now.RegionEndMs, now.Repeat));
        Assert.True((await bridge.Execute(Cmd("pause", sounds[0].Id) with { Epoch = epoch }, "host")).Ok);
        await Task.Delay(100); await bridge.Poll(false);
        now = bridge.Snapshot.Playback; Assert.Equal(("PLAYING", sounds[0].Id), (now.Status, now.SoundId)); Assert.InRange(now.PositionMs!.Value, 1500, 2000);
        Assert.Equal(2000, now.DurationMs);
    }

    [Fact]
    public async Task SessionEndRemovesTheTempCategoryWithItsSlotsOnlyWhenStoppedAndQuietForASecond()
    {
        var a = Make("a.wav", 48000, 2, 3000);
        await using var mock = Mock(a.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; await Ready(bridge, mock, sounds[0]);
        Assert.True(Played(await bridge.Execute(Cmd("region", sounds[0].Id) with { PositionMs = 100, EndMs = 2900 }, "host")));
        // Playing: the category and its slots stay.
        await bridge.CleanupSession(); await bridge.Poll(true);
        Assert.True(HasTemp(mock)); Assert.True(bridge.SlotsReady);
        Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok);
        var clock = Stopwatch.StartNew();
        await bridge.CleanupSession();
        Assert.True(clock.ElapsedMilliseconds >= 950, $"removed {clock.ElapsedMilliseconds} ms after Stop");
        Assert.False(HasTemp(mock));
        Assert.DoesNotContain(XDocument.Parse(mock.AllXml).Root!.Elements("Sound"), e => SlotPool.IsSlotFile((string)e.Attribute("url")!));
        Assert.Equal(0, mock.RiskyRemovals);
        (long At, string Command)[] timeline; lock (mock.Timeline) timeline = [.. mock.Timeline];
        long stop = timeline.Last(e => e.Command == "DoStopSound()").At, removal = timeline.Last(e => e.Command.StartsWith("DoRemoveCategory(")).At;
        Assert.True((removal - stop) * 1000.0 / Stopwatch.Frequency >= 1000);
        // The library sound and its file are untouched; the slot files stay on disk for the next session.
        Assert.Contains(a.Path, mock.AllXml); Assert.True(File.Exists(a.Path)); Assert.All(bridge.SlotPaths, p => Assert.True(File.Exists(p)));
        await bridge.Poll(true); Assert.False(bridge.SlotsReady);
    }

    [Fact]
    public async Task CrashLeftoversAreRemovedAtTheNextStartOrKeptAndReusedWhenTheCategoryHoldsSomethingElse()
    {
        var a = Make("a.wav", 48000, 2, 1500);
        await using var mock = Mock(a.Path); using var pipe = new SoundpadPipe(mock.Name); var foreign = Path.Combine(folder, "mine.wav");
        // A session that registered its slots, played a slice and then crashed (no clean-up at all).
        var crashed = new Bridge(pipe, new StateStore(folder)); await crashed.Poll(true);
        var sound = crashed.Snapshot.Library.Sounds[0]; await Ready(crashed, mock, sound);
        Assert.True(Played(await crashed.Execute(Cmd("region", sound.Id) with { PositionMs = 100, EndMs = 400 }, "host")));
        await Task.Delay(500); crashed.Dispose();
        using (var next = new Bridge(pipe, new StateStore(folder)))
        {
            await next.Poll(true);
            await next.CleanupSession(); // what every start does before anything else
            Assert.False(HasTemp(mock)); Assert.Equal(0, mock.RiskyRemovals);
            Assert.DoesNotContain(XDocument.Parse(mock.AllXml).Root!.Elements("Sound"), e => SlotPool.IsSlotFile((string)e.Attribute("url")!));

            // Again, but this time the person put one of their own sounds into that Temp category.
            await Ready(next, mock, sound);
            File.Copy(a.Path, foreign);
            var all = XDocument.Parse(mock.AllXml); int index = all.Root!.Elements("Sound").Max(e => (int)e.Attribute("index")!) + 1;
            all.Root.Add(new XElement("Sound", new XAttribute("index", index), new XAttribute("url", foreign), new XAttribute("title", "Mine")));
            var cats = XDocument.Parse(mock.CategoryXml);
            cats.Descendants("Category").Single(c => ((string)c.Attribute("name")!).StartsWith("Temp - Soundpad Web")).Add(new XElement("Sound", new XAttribute("index", index), new XAttribute("url", foreign)));
            mock.AllXml = all.ToString(); mock.CategoryXml = cats.ToString();
        }
        Clear(mock);
        using (var later = new Bridge(pipe, new StateStore(folder)))
        {
            await later.Poll(true);
            await later.CleanupSession();
            // Kept as before (something in it is not the app's own), and nothing of it was removed.
            Assert.True(HasTemp(mock)); Assert.Contains("mine.wav", mock.AllXml);
            Assert.DoesNotContain(Sent(mock), c => c.StartsWith("DoRemove"));
            // The slots left in it are reused as they are: exact at once, nothing added to Soundpad.
            await later.Poll(true); Assert.True(later.SlotsReady);
            var sounds = later.Snapshot.Library.Sounds; Assert.Equal(2, sounds.Length); // the library sound and the person's own; no slot
            Assert.True(Played(await later.Execute(Cmd("startAt", sound.Id) with { PositionMs = 700 }, "host")));
            Assert.Equal(a.Range(700, null), SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
            Assert.DoesNotContain(Sent(mock), c => c.StartsWith("DoAdd") || c.StartsWith("DoSeekMs("));
        }
    }

    [Fact]
    public async Task TempSoundsPlayExactSlicesOfTheirOwnFile()
    {
        var a = Make("a.wav", 48000, 2, 1000);
        await using var mock = Mock(a.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var tone = Enumerable.Range(0, 24000).Select(i => (short)(i * 7 % 30000 - 15000)).ToArray();
        var upload = await bridge.Upload(SliceWav.Encode(tone.Select(s => s / 32768f).ToArray(), 16000, 1), "Clip.wav", "Alice", false, "peer-alice");
        Assert.True(upload.Ok, upload.Error);
        var clip = bridge.Snapshot.Library.Sounds.Single(s => s.Temporary);
        await Ready(bridge, mock, bridge.Snapshot.Library.Sounds.Single(s => !s.Temporary));
        var cue = await bridge.Execute(Cmd("startAt", clip.Id) with { PositionMs = 400 }, "guest", new(Guid.NewGuid().ToString(), "Alice"), "peer-alice");
        Assert.True(Played(cue), cue.Error); Assert.Equal(Text(upload, "assetId"), Text(cue, "assetId"));
        Assert.Equal(tone[6400..], SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
        Assert.Equal((clip.Id, 1500L), (bridge.Snapshot.Playback.SoundId, bridge.Snapshot.Playback.DurationMs!.Value)); Assert.InRange(bridge.Snapshot.Playback.PositionMs!.Value, 400, 700);
        // Clear Temp stops the slot (it lives in Temp), then removes the category with the clip and the slots together.
        var cleared = await bridge.Execute(Cmd("clearTemp"), "host");
        Assert.True(cleared.Ok, cleared.Error); Assert.False(HasTemp(mock)); Assert.Equal(0, mock.RiskyRemovals);
        Assert.Equal("STOPPED", bridge.Snapshot.Playback.Status);
    }

    [Fact]
    public async Task WithExactSlicesOffACueStartStillPlaysThenSeeksAndMakesNoSlots()
    {
        var a = Make("a.wav", 48000, 2, 1000);
        await using var mock = Mock(a.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        bridge.UseExactSlices(false);
        var sound = bridge.Snapshot.Library.Sounds[0];
        Assert.True(Played(await bridge.Execute(Cmd("startAt", sound.Id) with { PositionMs = 300 }, "host")));
        Assert.True((await bridge.Execute(Cmd("stop"), "host")).Ok);
        for (int i = 0; i < 8; i++) { await Task.Delay(60); await bridge.Poll(false); }
        Assert.Equal(new[] { "DoPlaySound(1)", "DoSeekMs(300)" }, Sent(mock).Where(c => c.StartsWith("DoPlaySound(") || c.StartsWith("DoSeekMs(")).ToArray());
        Assert.DoesNotContain(Sent(mock), c => c.StartsWith("DoAdd")); Assert.False(Directory.Exists(Path.Combine(folder, "slots")));
        // A plain Play is always the sound itself, slices on or off.
        bridge.UseExactSlices(true); Clear(mock);
        Assert.True(Played(await bridge.Execute(Cmd("play", sound.Id), "host")));
        Assert.Equal(new[] { "DoPlaySound(1)" }, Sent(mock).Where(c => c.StartsWith("DoPlaySound(") || c.StartsWith("DoSeekMs(")).ToArray());
    }

    /// <summary>Soundpad's list with one sound added at its top: every index (the slots' too) moves down one.</summary>
    private static void InsertAtTop(MockSoundpad mock, string path)
    {
        XDocument Shift(string xml) { var doc = XDocument.Parse(xml); foreach (var e in doc.Descendants("Sound")) e.SetAttributeValue("index", (int)e.Attribute("index")! + 1); return doc; }
        var all = Shift(mock.AllXml); var cats = Shift(mock.CategoryXml);
        all.Root!.AddFirst(new XElement("Sound", new XAttribute("index", 1), new XAttribute("url", path), new XAttribute("title", "Added")));
        mock.AllXml = all.ToString(); mock.CategoryXml = cats.ToString();
    }

    [Fact]
    public async Task AStopInSoundpadEndsARepeatingSliceAndAnotherSoundIsNeverReportedAsIt()
    {
        // Review, 2.37: a repeat was played again whenever the slot was found stopped after the slice's length,
        // also when the person had stopped it in Soundpad, and another sound started near the slice's end was
        // reported as the sliced sound.
        var tone = Make("tone.wav", 16000, 1, 1000); var other = Make("other.wav", 48000, 2, 700);
        await using var mock = Mock(tone.Path, other.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; await Ready(bridge, mock, sounds[1]);
        async Task Polls(int count) { for (int i = 0; i < count; i++) { await Task.Delay(50); await bridge.Poll(false); } }

        // Stopped in Soundpad 60 ms into a 300 ms repeating region: it stays stopped.
        var region = await bridge.Execute(Cmd("region", sounds[0].Id) with { PositionMs = 100, EndMs = 400, Repeat = true }, "host");
        Assert.True(Played(region), region.Error); int plays = SlotPlays(mock).Length;
        await Task.Delay(60); mock.StopFromSoundpad();
        await Polls(12);
        Assert.Equal(plays, SlotPlays(mock).Length);
        Assert.Equal(("STOPPED", (string?)null), (bridge.Snapshot.Playback.Status, bridge.Snapshot.Playback.SoundId));

        // Paused from the website for longer than the slice, then stopped in Soundpad: it stays stopped.
        region = await bridge.Execute(Cmd("region", sounds[0].Id) with { PositionMs = 100, EndMs = 400, Repeat = true }, "host");
        Assert.True(Played(region), region.Error); plays = SlotPlays(mock).Length;
        Assert.True((await bridge.Execute(Cmd("pause", sounds[0].Id) with { Epoch = Text(region, "epoch") }, "guest")).Ok);
        await Polls(8);
        Assert.Equal("PAUSED", bridge.Snapshot.Playback.Status);
        mock.StopFromSoundpad();
        await Polls(10);
        Assert.Equal(plays, SlotPlays(mock).Length);
        Assert.Equal(("STOPPED", (string?)null), (bridge.Snapshot.Playback.Status, bridge.Snapshot.Playback.SoundId));

        // Another sound started from Soundpad just before the slice's end: never reported as the sliced sound,
        // and when it ends, the region does not start again by itself.
        region = await bridge.Execute(Cmd("region", sounds[0].Id) with { PositionMs = 100, EndMs = 400, Repeat = true }, "host");
        Assert.True(Played(region), region.Error); plays = SlotPlays(mock).Length;
        await Task.Delay(265); mock.PlayFromSoundpad(2);
        await bridge.Poll(false);
        Assert.NotEqual(sounds[0].Id, bridge.Snapshot.Playback.SoundId);
        await Polls(20);
        Assert.Equal(plays, SlotPlays(mock).Length);
        Assert.Null(bridge.Snapshot.Playback.SoundId);
    }

    [Fact]
    public async Task ASeekInsideASliceReadsSoundpadsListAgainBeforeItPlaysASlot()
    {
        // Review, 2.37: the seek played the slot's cached index; a sound added at the top of Soundpad's list since the
        // last library read made that index another entry.
        var a = Make("a.wav", 48000, 2, 2000); var b = Make("b.wav", 16000, 1, 1000); var added = Make("added.wav", 16000, 1, 500);
        await using var mock = Mock(a.Path, b.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; await Ready(bridge, mock, sounds[1]);
        var cue = await bridge.Execute(Cmd("startAt", sounds[0].Id) with { PositionMs = 200 }, "host");
        Assert.True(Played(cue), cue.Error); var epoch = Text(cue, "epoch");
        InsertAtTop(mock, added.Path);
        int slots = SlotPlays(mock).Length;
        var seek = await bridge.Execute(Cmd("seek", sounds[0].Id) with { Epoch = epoch, PositionMs = 1500 }, "host");
        Assert.True(seek.Ok, seek.Error); Assert.False(Flag(seek, "superseded"));
        Assert.Equal(slots + 1, SlotPlays(mock).Length);
        Assert.Equal(a.Range(1500, null), SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
        Assert.Equal(mock.PlayingIndex, SlotPlays(mock)[^1].Index);
    }

    [Fact]
    public async Task OnlyOneSliceRendersAtATimeAndARenderNobodyWaitsForIsStopped()
    {
        // Review, 2.37: every cue start rendered its slice to the end, all at once, also when none of them was played.
        var a = Make("a.wav", 48000, 2, 1500); var b = Make("b.wav", 16000, 1, 1000);
        await using var mock = Mock(a.Path, b.Path); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        var sounds = bridge.Snapshot.Library.Sounds; await Ready(bridge, mock, sounds[1]);
        Assert.True(Played(await bridge.Execute(Cmd("startAt", sounds[0].Id) with { PositionMs = 50 }, "host"))); // decoded and cached now
        int running = 0, most = 0, started = 0, cancelled = 0;
        var gate = new object();
        bridge.UseRenderer((source, start, end, cancel) =>
        {
            Interlocked.Increment(ref started); int now = Interlocked.Increment(ref running);
            lock (gate) most = Math.Max(most, now);
            try
            {
                // A render far slower than the budget: it ends only when it is told to stop.
                if (cancel.WaitHandle.WaitOne(10000)) { Interlocked.Increment(ref cancelled); cancel.ThrowIfCancellationRequested(); }
                return SliceRenderer.Render(source, start, end);
            }
            finally { Interlocked.Decrement(ref running); }
        });
        var clicks = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => bridge.Execute(Cmd("startAt", sounds[0].Id) with { PositionMs = 100 + i * 50 }, "host")));
        Assert.All(clicks, reply => Assert.True(reply.Ok, reply.Error));
        for (int i = 0; i < 100 && Volatile.Read(ref running) > 0; i++) await Task.Delay(20);
        Assert.Equal(0, Volatile.Read(ref running)); // every render was stopped, none ran on for 10 s
        Assert.True(started >= 1); Assert.Equal(1, most); Assert.Equal(started, cancelled);
        // The slot is free again: with the usual renderer the next click is exact.
        bridge.UseRenderer((source, start, end, cancel) => SliceRenderer.Render(source, start, end, null, cancel));
        await Task.Delay(50);
        int slots = SlotPlays(mock).Length;
        Assert.True(Played(await bridge.Execute(Cmd("startAt", sounds[0].Id) with { PositionMs = 700 }, "host")));
        Assert.Equal(slots + 1, SlotPlays(mock).Length);
        Assert.Equal(a.Range(700, null), SliceWav.Pcm16(SlotPlays(mock)[^1].Bytes));
    }

    [Fact]
    public void OnlyTheAppsOwnSlotPathsCountAsRemovableEvenIfSlotsJsonIsTampered()
    {
        var files = new SlotFiles(folder);
        Directory.CreateDirectory(files.Folder);
        var outside = Path.Combine(folder, "slot-9.wav"); var notSlot = Path.Combine(files.Folder, "Airhorn.wav"); var extra = Path.Combine(files.Folder, "slot-7.wav");
        File.WriteAllText(Path.Combine(files.Folder, "slots.json"), JsonSerializer.Serialize(new { version = 1, slots = new[] { outside, notSlot, extra, "relative\\slot-2.wav" } }, Json.Options));
        var owned = files.Owned();
        Assert.Equal(files.Paths.Append(extra).Order(), owned.Order());
        Assert.All(files.Paths, p => Assert.Equal(files.Folder, Path.GetDirectoryName(p)));
        // A record that cannot be read is ignored: the three current slots still count.
        File.WriteAllText(Path.Combine(files.Folder, "slots.json"), "{ not json");
        Assert.Equal(files.Paths.Order(), new SlotFiles(folder).Owned().Order());
        files.Record("Temp - Soundpad Web [abc123-def456]");
        Assert.Equal(files.Paths.Order(), new SlotFiles(folder).Owned().Order());
    }
}
