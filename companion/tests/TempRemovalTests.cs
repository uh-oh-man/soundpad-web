using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// Removing Temp audio (2.46, from the 2.45 feedback: "It did not let me delete temp files", "there should be no
/// pending"). The real Soundpad cannot remove one entry safely, so a removed file waits for the app's Temp category
/// to go at quit or Clear Temp: that is internal, not a pending item or a warning, and a removal no longer waits for
/// Soundpad to be quiet. These tests use the production-style pipe (no test-only entry removal) unless they say so.
/// </summary>
public sealed class TempRemovalTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-temp-removal-" + Guid.NewGuid().ToString("N"));
    public TempRemovalTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static JsonElement Body(Reply reply) => JsonSerializer.SerializeToElement(reply.Data, Json.Options);
    private static async Task<Reply> Ok(Task<Reply> reply) { var r = await reply; Assert.True(r.Ok, r.Error); return r; }
    private static async Task<string> Refused(Task<Reply> reply) { var r = await reply; Assert.False(r.Ok, "expected a refusal"); return r.Error!; }
    private static readonly Participant Alice = new("11111111-1111-1111-1111-111111111111", "Alice");
    private static readonly Participant Bob = new("22222222-2222-2222-2222-222222222222", "Bob");
    private static byte[] Tone(float step) => MimicRing.Wav([.. Enumerable.Range(0, 16000).Select(i => 0.3f * MathF.Sin(i * step))], 16000);

    private sealed class Rig : IAsyncDisposable
    {
        public readonly MockSoundpad Mock = new() { DurationMs = 60000 };
        public readonly SoundpadPipe Pipe;
        public readonly Bridge Bridge;
        private readonly string folder;
        public Rig(string folder, bool entryRemoval = false) { this.folder = folder; Pipe = new SoundpadPipe(Mock.Name, entryRemoval); Bridge = new Bridge(Pipe, new StateStore(folder)); }
        public async Task Start(Permissions? permissions = null)
        {
            await Bridge.Poll(true);
            await Ok(Bridge.Execute(Cmd("beginSession"), "host"));
            if (permissions != null) await Ok(Bridge.Execute(Cmd("permissions") with { Permissions = permissions }, "host"));
        }
        public async Task<string> Clip(Participant? who, float step, bool mimic = false)
        {
            var name = (who?.Name ?? "Host") + " clip " + step + ".wav";
            var reply = who == null ? await Bridge.Upload(Tone(step), name, "Host", true, "host")
                : await Bridge.Upload(Tone(step), name, who.Name, false, "peer-" + who.Name, mimic: mimic, guest: who.Id);
            Assert.True(reply.Ok, reply.Error);
            return Body(reply).GetProperty("assetId").GetString()!;
        }
        public Task<Reply> As(Participant who, Command c) => Bridge.Execute(c, "guest", who, "peer-" + who.Name);
        public async Task<JsonElement> Temp(Participant? who = null) =>
            Body(await Ok(who == null ? Bridge.Execute(Cmd("audioAssets"), "host") : As(who, Cmd("audioAssets"))));
        public async Task<JsonElement> Source(string id) => (await Temp()).GetProperty("manager").GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("id").GetString() == id);
        public async Task<string[]> Issues() => [.. (await Temp()).GetProperty("manager").GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()!)];
        public string FileOf(string id) => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!
            .Assets.Single(a => a.Id == id).Variants.First(v => v.OwnsFile).Path;
        public async ValueTask DisposeAsync() { Bridge.Dispose(); Pipe.Dispose(); await Mock.DisposeAsync(); }
    }

    [Fact]
    public async Task AGuestRemovesTheirClipWhileAnotherSoundPlaysAndNothingIsLeftPending()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var mine = await rig.Clip(Alice, 0.2f);
        var hosts = await rig.Clip(null, 0.4f);
        // The host plays something else: until 2.46 every removal was refused while anything played.
        Assert.True((await rig.Bridge.Execute(Cmd("audioPlay") with { AssetId = hosts, Orientation = "normal" }, "host")).Ok);
        Assert.Equal("PLAYING", rig.Mock.Status);
        await Ok(rig.As(Alice, Cmd("removeAudio") with { AssetId = mine, Action = "stopIfPlaying" }));
        Assert.Equal("PLAYING", rig.Mock.Status); // the host's sound keeps playing
        var source = await rig.Source(mine);
        Assert.False(source.GetProperty("hasTemporary").GetBoolean());
        // No warning asking the host to remove an entry by hand, no "paused" notice, and nothing for the poll to retry.
        Assert.DoesNotContain("native-removal-required", await rig.Issues());
        Assert.DoesNotContain("playback-active", await rig.Issues());
        await rig.Bridge.Poll(true);
        Assert.DoesNotContain(rig.Bridge.Snapshot.Library.Sounds, s => s.AssetId == mine && !s.InternalVariant);
        Assert.DoesNotContain(mine, (await rig.Temp(Alice)).GetProperty("mine").EnumerateArray().Select(e => e.GetString()));
        // Once Soundpad is quiet the file simply waits for the Temp category: nothing to report or retry.
        await rig.Pipe.Request("DoStopSound()");
        await rig.Bridge.Poll(true);
        Assert.False(rig.Bridge.AudioRecoveryDue);
        Assert.Empty(await rig.Issues());
        var file = rig.FileOf(mine);
        Assert.True(File.Exists(file));
        // Quitting removes the app's Temp category with its entries, and the file.
        await rig.Bridge.CleanupSession();
        Assert.False(File.Exists(file)); Assert.DoesNotContain(file, rig.Mock.AllXml);
    }

    [Fact]
    public async Task AGuestAllowedOnlyMimicMayRemoveTheirOwnClips()
    {
        // The owner's friend could make Mimic clips but not remove them: removal followed guest uploads only.
        await using var rig = new Rig(folder); await rig.Start(new(GuestUploads: false, GuestMimic: true));
        var mine = await rig.Clip(Alice, 0.2f, mimic: true);
        var bobs = await rig.Clip(Bob, 0.3f, mimic: true);
        Assert.Equal([mine], (await rig.Temp(Alice)).GetProperty("mine").EnumerateArray().Select(e => e.GetString()!).ToArray());
        await Ok(rig.As(Alice, Cmd("removeAudio") with { AssetId = mine, Action = "stopIfPlaying" }));
        Assert.Contains("only temporary audio they added", await Refused(rig.As(Alice, Cmd("removeAudio") with { AssetId = bobs })));
        // With neither allowed, removing is refused as before.
        await Ok(rig.Bridge.Execute(Cmd("permissions") with { Permissions = new(GuestUploads: false, GuestMimic: false) }, "host"));
        Assert.Contains("not allowed guests", await Refused(rig.As(Bob, Cmd("removeAudio") with { AssetId = bobs })));
    }

    [Fact]
    public async Task RemoveAllMyTempClipsTakesOnlyTheGuestsOwnAndUndoBringsThemBack()
    {
        await using var rig = new Rig(folder); await rig.Start();
        string[] mine = [await rig.Clip(Alice, 0.2f), await rig.Clip(Alice, 0.25f)];
        var bobs = await rig.Clip(Bob, 0.3f); var hosts = await rig.Clip(null, 0.4f);
        Assert.Equal(mine.Order(), (await rig.Temp(Alice)).GetProperty("mine").EnumerateArray().Select(e => e.GetString()!).Order());
        Assert.Equal(JsonValueKind.Null, (await rig.Temp()).GetProperty("mine").ValueKind); // the host's answer has none
        Assert.Contains("Clear all", await Refused(rig.Bridge.Execute(Cmd("tempRemoveMine"), "host")));
        var removed = Body(await Ok(rig.As(Alice, Cmd("tempRemoveMine") with { Action = "stopIfPlaying" })));
        Assert.Equal(2, removed.GetProperty("removed").GetInt32());
        Assert.Equal(mine.Order(), removed.GetProperty("assetIds").EnumerateArray().Select(e => e.GetString()!).Order());
        foreach (var id in mine) Assert.False((await rig.Source(id)).GetProperty("hasTemporary").GetBoolean());
        Assert.True((await rig.Source(bobs)).GetProperty("hasTemporary").GetBoolean());
        Assert.True((await rig.Source(hosts)).GetProperty("hasTemporary").GetBoolean());
        Assert.Empty((await rig.Temp(Alice)).GetProperty("mine").EnumerateArray());
        // Nothing left of hers: removing all again removes nothing, and is no error.
        Assert.Equal(0, Body(await Ok(rig.As(Alice, Cmd("tempRemoveMine")))).GetProperty("removed").GetInt32());
        // Undo: only her own may be named.
        Assert.Contains("only temporary audio", await Refused(rig.As(Bob, Cmd("tempRestore") with { AssetIds = mine })));
        var back = Body(await Ok(rig.As(Alice, Cmd("tempRestore") with { AssetIds = mine })));
        Assert.Equal(2, back.GetProperty("restored").GetInt32());
        foreach (var id in mine)
        {
            var source = await rig.Source(id);
            Assert.True(source.GetProperty("hasTemporary").GetBoolean()); Assert.Equal("temporary", source.GetProperty("state").GetString());
        }
        await rig.Bridge.Poll(true);
        Assert.Equal(2, rig.Bridge.Snapshot.Library.Sounds.Count(s => mine.Contains(s.AssetId) && !s.InternalVariant));
        // They play again.
        Assert.True((await rig.Bridge.Execute(Cmd("audioPlay") with { AssetId = mine[0], Orientation = "normal" }, "host")).Ok);
        // A second Undo has nothing left to bring back.
        Assert.Contains("already gone", await Refused(rig.As(Alice, Cmd("tempRestore") with { AssetIds = mine })));
    }

    [Fact]
    public async Task TheHostsRemoveAndUndoWorkWhileSomethingPlaysButNotAfterClearTemp()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var clip = await rig.Clip(null, 0.2f); var other = await rig.Clip(null, 0.3f);
        Assert.True((await rig.Bridge.Execute(Cmd("audioPlay") with { AssetId = other, Orientation = "normal" }, "host")).Ok);
        await Ok(rig.Bridge.Execute(Cmd("removeAudio") with { AssetId = clip }, "host"));
        Assert.Empty(await rig.Issues());
        // The source that is playing itself is still refused without "stopIfPlaying", and stopped with it.
        Assert.Contains("Stop playback", await Refused(rig.Bridge.Execute(Cmd("removeAudio") with { AssetId = other }, "host")));
        await Ok(rig.Bridge.Execute(Cmd("removeAudio") with { AssetId = other, Action = "stopIfPlaying" }, "host"));
        Assert.Equal("STOPPED", rig.Mock.Status);
        await Ok(rig.Bridge.Execute(Cmd("tempRestore") with { AssetId = clip }, "host"));
        Assert.True((await rig.Source(clip)).GetProperty("hasTemporary").GetBoolean());
        // Clear Temp removes the category and every file: an Undo after it has nothing to bring back.
        await Ok(rig.Bridge.Execute(Cmd("clearTemp"), "host"));
        Assert.Contains("already gone", await Refused(rig.Bridge.Execute(Cmd("tempRestore") with { AssetId = other }, "host")));
    }

    [Fact]
    public async Task WithEntryRemovalTheFileGoesOnceSoundpadIsQuiet()
    {
        // The test pipes can remove one entry: then the removal completes as soon as nothing plays.
        await using var rig = new Rig(folder, entryRemoval: true); await rig.Start();
        var clip = await rig.Clip(null, 0.2f); var other = await rig.Clip(null, 0.3f); var file = rig.FileOf(clip);
        Assert.True((await rig.Bridge.Execute(Cmd("audioPlay") with { AssetId = other, Orientation = "normal" }, "host")).Ok);
        await Ok(rig.Bridge.Execute(Cmd("removeAudio") with { AssetId = clip }, "host"));
        Assert.True(File.Exists(file)); Assert.DoesNotContain("playback-active", await rig.Issues());
        Assert.False((await rig.Source(clip)).GetProperty("hasTemporary").GetBoolean());
        Assert.True(rig.Bridge.AudioRecoveryDue);
        await rig.Pipe.Request("DoStopSound()");
        await rig.Bridge.Poll(true);
        Assert.False(File.Exists(file)); Assert.DoesNotContain(file, rig.Mock.AllXml);
        Assert.False(rig.Bridge.AudioRecoveryDue);
        Assert.Contains("already gone", await Refused(rig.Bridge.Execute(Cmd("tempRestore") with { AssetId = clip }, "host")));
    }
}
