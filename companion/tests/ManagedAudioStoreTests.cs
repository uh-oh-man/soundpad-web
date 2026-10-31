using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class ManagedAudioStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-managed-v3-" + Guid.NewGuid().ToString("N"));
    public ManagedAudioStoreTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static string Id(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
    private ManagedAudioStore Store(ISoundpad pipe) => new(folder, pipe, Id, async work => await work());
    private byte[] Wave()
    {
        var path = Path.Combine(folder, "fixture.wav");
        using (var wave = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) wave.Write([16, 0, 32, 0, 48, 0, 64, 0, 80, 0, 96, 0], 0, 12);
        return File.ReadAllBytes(path);
    }
    [Fact]
    public async Task ReverseTwiceReusesOneLogicalSourceAndItsExactNormalFrames()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); var store = Store(pipe); store.BeginSession();
        var asset = await store.Upload(Wave(), "Stereo.wav", "Alice", "peer-a", false);
        var normal = (await store.Prepare(asset.Id, "normal", false)).Variant;
        var first = (await store.Prepare(asset.Id, "reversed", true)).Variant;
        var back = (await store.Prepare(asset.Id, "normal", false)).Variant;
        var again = (await store.Prepare(asset.Id, "reversed", false)).Variant;
        Assert.Equal(normal.Id, back.Id); Assert.Equal(first.Id, again.Id); Assert.Equal(2, asset.Variants.Count);
        using var reader = new WaveFileReader(first.Path); var data = new byte[12]; Assert.Equal(12, reader.Read(data, 0, 12));
        Assert.Equal(new byte[] { 80, 0, 96, 0, 48, 0, 64, 0, 16, 0, 32, 0 }, data);
        Assert.DoesNotContain("(reversed)", asset.Name); Assert.Single(store.Public.Assets);
        Assert.Equal(2, mock.Commands.Count(c => c.StartsWith("DoAddSound(")));
    }
    [Fact]
    public async Task PromotionCopiesOutsideTempAndIsIdempotentBeforeAndAfterRecovery()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); var store = Store(pipe); store.BeginSession();
        var asset = await store.Upload(Wave(), "Keep.wav", "Alice", "peer-a", false); var original = asset.Variants.Single();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Promote(asset.Id, original.Id, null, null, null, false));
        await store.Promote(asset.Id, original.Id, null, null, null, true);
        var permanent = Assert.Single(asset.Variants, v => v.Permanent);
        Assert.StartsWith(Path.Combine(folder, "library"), permanent.Path); Assert.False(File.Exists(original.Path)); Assert.True(File.Exists(permanent.Path));
        Assert.Contains(permanent.Path, mock.AllXml); int imports = mock.Commands.Count(c => c.StartsWith("DoAddSound("));
        await store.Promote(asset.Id, original.Id, null, null, null, true); Assert.Equal(imports, mock.Commands.Count(c => c.StartsWith("DoAddSound(")));
        store.RetireSession(); await store.Cleanup(); Assert.False(File.Exists(original.Path)); Assert.True(File.Exists(permanent.Path));
        var reopened = Store(pipe); await reopened.Recover(true); Assert.True(File.Exists(permanent.Path)); Assert.Contains(permanent.Path, mock.AllXml);
    }
    [Fact]
    public async Task ProductionStyleSelectionOnlyAdapterPreservesOrphansAndReportsThem()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); var store = Store(pipe); store.BeginSession();
        var asset = await store.Upload(Wave(), "Protected.wav", "Alice", "peer-a", false); var file = asset.Variants.Single().Path;
        store.RetireSession(); await store.Cleanup();
        Assert.True(File.Exists(file)); Assert.Contains(file, mock.AllXml); Assert.Contains(store.Public.Issues, i => i.Code == "native-removal-required");
        Assert.DoesNotContain(mock.Commands, c => c.StartsWith("DoSelect") || c.StartsWith("DoRemoveSelected"));
    }
    [Fact]
    public async Task ConcurrentNativeChangeAndPlayingAudioPreventCleanup()
    {
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name, true); var store = Store(pipe); store.BeginSession();
        var asset = await store.Upload(Wave(), "Race.wav", "Alice", "peer-a", false); var file = asset.Variants.Single().Path;
        store.RetireSession(); mock.ChangeLibraryBeforeOwnedRemoval = true; await store.Cleanup(); Assert.True(File.Exists(file)); Assert.Contains(file, mock.AllXml);
        mock.BeginFixturePlayback(); await store.Cleanup(); Assert.True(File.Exists(file)); Assert.Contains(store.Public.Issues, i => i.Code == "playback-active");
        await pipe.Request("DoStopSound()"); await store.Cleanup(); Assert.False(File.Exists(file)); Assert.Equal(4, System.Xml.Linq.XDocument.Parse(mock.AllXml).Descendants("Sound").Count());
    }
    [Fact]
    public async Task ExistingTempCategoryIsNeverClaimedOrDeleted()
    {
        await using var mock = new MockSoundpad { CategoryXml = MockSoundpad.Categories.Replace("</Categories>", "<Category index=\"9\" name=\"temp\"><Sound index=\"1\" url=\"C:\\Private\\airhorn.wav\"/></Category></Categories>") };
        using var pipe = new SoundpadPipe(mock.Name, true); var store = Store(pipe); store.BeginSession(); await store.Upload(Wave(), "Separate.wav", "Alice", "peer-a", false);
        Assert.NotEqual(9, store.Public.TempCategoryId); Assert.StartsWith("Temp · Soundpad Web", store.Public.TempCategoryName); store.RetireSession(); await store.Cleanup();
        Assert.Contains("name=\"temp\"", mock.CategoryXml); Assert.Contains("airhorn.wav", mock.CategoryXml); Assert.DoesNotContain(mock.Commands, c => c.StartsWith("DoRemoveCategory("));
    }
    [Fact]
    public void MalformedJournalIsPreservedAndManagedMutationsAreBlocked()
    {
        var path = Path.Combine(folder, "managed-audio"); Directory.CreateDirectory(path); var file = Path.Combine(path, "journal.json"); File.WriteAllText(file, "{broken");
        using var pipe = new SoundpadPipe("soundpad-managed-unused"); var store = Store(pipe);
        Assert.True(store.Public.Blocked); Assert.Equal("{broken", File.ReadAllText(file)); Assert.Throws<InvalidDataException>(store.BeginSession);
    }
    [Fact]
    public async Task ExactDuplicateUploadDoesNotCreateAnotherFileOrNativeEntry()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); var store = Store(pipe); store.BeginSession(); var bytes = Wave();
        var first = await store.Upload(bytes, "One.wav", "Alice", "peer-a", false); var again = await store.Upload(bytes, "One.wav", "Alice", "peer-a", false);
        Assert.Equal(first.Id, again.Id); Assert.Single(store.Public.Assets); Assert.Single(mock.Commands, c => c.StartsWith("DoAddSound("));
    }

    private sealed class FaultPipe(ISoundpad inner) : ISoundpad
    {
        public string? FailPrefix; public bool After; public string? HoldPrefix;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> Request(string command, CancellationToken cancel = default)
        {
            if (HoldPrefix != null && command.StartsWith(HoldPrefix)) { HoldPrefix = null; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            bool fail = FailPrefix != null && command.StartsWith(FailPrefix); if (fail) FailPrefix = null;
            if (fail && !After) throw new IOException("fixture interrupted");
            var result = await inner.Request(command, cancel); if (fail) throw new IOException("fixture response lost"); return result;
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InterruptedPromotionRecoversWithoutDuplicateFilesOrImports(bool after)
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name,true); var faults = new FaultPipe(pipe); var store=Store(faults); store.BeginSession();
        var asset=await store.Upload(Wave(),"Recover.wav","Alice","peer",false);var source=asset.Variants.Single();
        faults.FailPrefix="DoAddSound(";faults.After=after;
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.Promote(asset.Id,source.Id,null,null,null,true));
        Assert.True(File.Exists(source.Path)); var reopened=Store(faults);await reopened.Recover();
        var current=reopened.AssetById(asset.Id);Assert.Equal("committed",current.Promotion!.State);var accepted=Assert.Single(current.Variants,v=>v.Permanent);Assert.True(File.Exists(accepted.Path));
        Assert.Equal(2,mock.Commands.Count(c=>c.StartsWith("DoAddSound(")));await reopened.Recover();Assert.Equal(2,mock.Commands.Count(c=>c.StartsWith("DoAddSound(")));
    }
    [Fact]
    public async Task UnavailableSoundpadRetainsPreparedUploadAndReconnectImportsOnce()
    {
        await using var mock=new MockSoundpad();using var pipe=new SoundpadPipe(mock.Name,true);var faults=new FaultPipe(pipe){FailPrefix="GetSoundlist()"};var store=Store(faults);store.BeginSession();
        var asset=await store.Upload(Wave(),"Offline.wav","Alice","peer",false);Assert.Equal("waiting-soundpad",asset.State);Assert.True(File.Exists(asset.Variants.Single().Path));
        await store.Recover();Assert.True(store.Public.Assets.Single().Normal!.Ready);Assert.Single(mock.Commands,c=>c.StartsWith("DoAddSound("));await store.Recover();Assert.Single(mock.Commands,c=>c.StartsWith("DoAddSound("));
    }
    [Fact]
    public async Task FileLockAndPlaybackStartingDuringCleanupPreserveFilesAndNativeOriginals()
    {
        await using var mock=new MockSoundpad();using var pipe=new SoundpadPipe(mock.Name,true);var store=Store(pipe);store.BeginSession();var asset=await store.Upload(Wave(),"Locked.wav","Alice","peer",false);var source=asset.Variants.Single();store.RetireSession();
        mock.StartPlaybackBeforeOwnedRemoval=true;await store.Cleanup();Assert.True(File.Exists(source.Path));Assert.Contains(source.Path,mock.AllXml);await pipe.Request("DoStopSound()");
        using(var file=new FileStream(source.Path,FileMode.Open,FileAccess.Read,FileShare.Read)){await store.Cleanup();Assert.True(File.Exists(source.Path));Assert.Contains(store.Public.Issues,i=>i.Code=="cleanup-file");}
        await store.Cleanup();Assert.False(File.Exists(source.Path));Assert.Equal(4,System.Xml.Linq.XDocument.Parse(mock.AllXml).Descendants("Sound").Count());
    }
    [Fact]
    public async Task ChangedNativeSourceInvalidatesGeneratedReverseWithoutChangingOriginal()
    {
        var bytes=Wave();var path=Path.Combine(folder,"fixture.wav");await using var mock=new MockSoundpad{AllXml=MockSoundpad.Sounds.Replace(@"C:\Private\airhorn.wav",path)};using var pipe=new SoundpadPipe(mock.Name,true);var store=Store(pipe);store.BeginSession();
        var asset=(await store.NativeSource(Id(path),path,"Native","Host")).Asset;var first=(await store.Prepare(asset.Id,"reversed",true)).Variant;
        bytes[44]=90;File.WriteAllBytes(path,bytes);await Task.Delay(1050);var next=(await store.Prepare(asset.Id,"reversed",true)).Variant;
        Assert.NotEqual(first.Id,next.Id);Assert.True(first.Retired);Assert.False(first.Current);Assert.Equal(bytes,File.ReadAllBytes(path));
    }
    [Fact]
    public void LegacyReverseChainsResolveToRootAndAcceptedCopiesArePreserved()
    {
        var bytes=Wave();var legacy=Path.Combine(folder,"session-audio");Directory.CreateDirectory(legacy);string Name()=>Guid.NewGuid().ToString("N")+".wav";
        var normal=Name();var reversed=Name();var twice=Name();foreach(var name in new[]{normal,reversed,twice})File.WriteAllBytes(Path.Combine(legacy,name),bytes);
        string Source(string name)=>Path.Combine(legacy,name)+":10:100";
        var rows=new[]{new ManagedAudio(twice,"Tone (reversed) (reversed)","Alice",false,Source(reversed)),new ManagedAudio(reversed,"Tone (reversed)","Alice",true,Source(normal)),new ManagedAudio(normal,"Tone","Alice")};
        File.WriteAllText(Path.Combine(legacy,"journal.json"),Json.Write(rows));using var pipe=new SoundpadPipe("soundpad-web-unused");var store=Store(pipe);Assert.False(store.Public.Blocked);var asset=store.Public.Assets.Single();Assert.Equal("Tone",asset.Name);Assert.Equal(3,asset.Variants.Length);Assert.Equal("normal",asset.Normal!.Orientation);Assert.Contains(asset.Variants,v=>v.LegacyApproval&&v.Orientation=="reversed");Assert.Contains(asset.Variants,v=>!v.Current&&v.Orientation=="normal");Assert.True(File.Exists(Path.Combine(legacy,"journal.v2.backup.json")));
    }
    [Fact]
    public void TraversalJournalCannotClaimExternalFilesForCleanup()
    {
        var source=Path.Combine(folder,"fixture.wav");Wave();var bytes=File.ReadAllBytes(source);var root=Path.Combine(folder,"managed-audio");Directory.CreateDirectory(root);
        var data=new AudioJournal();data.Assets.Add(new(){Variants=[new(){Path=source,Storage="session",SessionId=Guid.NewGuid().ToString(),OwnsFile=true}]});var text=Json.Write(data);File.WriteAllText(Path.Combine(root,"journal.json"),text);
        using var pipe=new SoundpadPipe("soundpad-web-unused");var store=Store(pipe);Assert.True(store.Public.Blocked);Assert.Equal(text,File.ReadAllText(Path.Combine(root,"journal.json")));Assert.Equal(bytes,File.ReadAllBytes(source));
    }
    [Fact]
    public async Task NewerStopCancelsPendingReverseAutoplayAndGuestCannotPromote()
    {
        Wave();var path=Path.Combine(folder,"fixture.wav");await using var mock=new MockSoundpad{AllXml=MockSoundpad.Sounds.Replace(@"C:\Private\airhorn.wav",path),CategoryXml=MockSoundpad.Categories.Replace(@"C:\Private\airhorn.wav",path)};using var pipe=new SoundpadPipe(mock.Name,true);var faults=new FaultPipe(pipe);using var bridge=new Bridge(faults,new StateStore(folder));await bridge.Poll(true);
        Command Cmd(string op)=>new(2,Guid.NewGuid().ToString(),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),op);
        Assert.True((await bridge.Execute(Cmd("beginSession") with{AudienceId="fixture"},"host")).Ok);var sound=bridge.Snapshot.Library.Sounds[0];faults.HoldPrefix="DoAddSound(";
        var reverse=bridge.Execute(Cmd("reverse") with{SoundId=sound.Id,LibraryVersion=bridge.Snapshot.Library.Version},"guest",requester:"peer",audience:"fixture");await faults.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var stop=bridge.Execute(Cmd("stop"),"host");faults.Release.TrySetResult();Assert.True((await stop).Ok);Assert.True((await reverse).Ok);Assert.DoesNotContain(mock.Commands,c=>c.StartsWith("DoPlaySound("));
        Assert.False((await bridge.Execute(Cmd("keepAudio"),"guest",requester:"peer",audience:"fixture")).Ok);Assert.False((await bridge.Execute(Cmd("audioPlay"),"guest",requester:"peer",audience:"old")).Ok);
        var reply=await bridge.Execute(Cmd("audioAssets"),"guest");Assert.DoesNotContain(folder,Json.Write(reply.Data));
    }

    [Fact]
    public async Task PartialPermanentCopyResumesAndConflictingDestinationNeverOverwritesSource()
    {
        await using var mock=new MockSoundpad();using var pipe=new SoundpadPipe(mock.Name,true);var faults=new FaultPipe(pipe);var store=Store(faults);store.BeginSession();var asset=await store.Upload(Wave(),"Partial.wav","Alice","peer",false);var source=asset.Variants.Single();
        faults.FailPrefix="DoAddSound(";await Assert.ThrowsAsync<InvalidDataException>(()=>store.Promote(asset.Id,source.Id,null,null,null,true));var target=asset.Variants.Single(v=>v.Storage=="permanent");var bytes=File.ReadAllBytes(target.Path);File.Delete(target.Path);File.WriteAllBytes(target.Path+".copying",bytes[..25]);
        await store.Recover();Assert.Equal(bytes,File.ReadAllBytes(target.Path));Assert.False(File.Exists(target.Path+".copying"));Assert.True(target.Permanent);
        var other=await store.Upload(Wave(),"Conflict.wav","Alice","peer-other",false);var original=other.Variants.Single();faults.FailPrefix="DoAddSound(";await Assert.ThrowsAsync<InvalidDataException>(()=>store.Promote(other.Id,original.Id,null,null,null,true));var conflicting=other.Variants.Single(v=>v.Storage=="permanent");File.WriteAllBytes(conflicting.Path,[7,8,9]);
        await store.Recover();Assert.Equal(new byte[]{7,8,9},File.ReadAllBytes(conflicting.Path));Assert.True(File.Exists(original.Path));Assert.False(conflicting.Permanent);Assert.Contains(store.Public.Issues,i=>i.AssetId==other.Id&&i.Code=="promotion");
    }

    [Fact]
    public async Task PromotionRetainsIconIdentityAndStatisticsAndAllowsRemovingTheInheritedIcon()
    {
        await using var mock=new MockSoundpad();using var pipe=new SoundpadPipe(mock.Name,true);using var bridge=new Bridge(pipe,new StateStore(folder));await bridge.Poll(true);
        Command Cmd(string op)=>new(2,Guid.NewGuid().ToString(),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),op);
        Assert.True((await bridge.Execute(Cmd("beginSession"),"host")).Ok);Assert.True((await bridge.Upload(Wave(),"Metadata.wav","Host",true)).Ok);var original=bridge.Snapshot.Library.Sounds.Single(s=>s.Temporary);
        using var bitmap=new System.Drawing.Bitmap(12,12);using var image=new MemoryStream();bitmap.Save(image,System.Drawing.Imaging.ImageFormat.Png);
        Assert.True((await bridge.Execute(Cmd("icon") with{SoundId=original.Id,Image="data:image/png;base64,"+Convert.ToBase64String(image.ToArray())},"host")).Ok);
        Assert.True((await bridge.Execute(Cmd("play") with{SoundId=original.Id,LibraryVersion=bridge.Snapshot.Library.Version},"host")).Ok);Assert.True((await bridge.Execute(Cmd("stop"),"host")).Ok);
        var data=JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder,"managed-audio","journal.json")),Json.Options)!;var asset=data.Assets.Single();
        Assert.True((await bridge.Execute(Cmd("keepAudio") with{AssetId=asset.Id,VariantId=asset.Variants.Single().Id,Confirmed=true},"host")).Ok);var promoted=bridge.Snapshot.Library.Sounds.Single(s=>s.AssetId==asset.Id&&!s.InternalVariant);Assert.NotEqual(original.Id,promoted.Id);Assert.NotNull(promoted.Icon);Assert.Equal(original.Id,promoted.WorkspaceId);Assert.Contains(original.Id,promoted.Aliases!);
        Assert.True((await bridge.Execute(Cmd("play") with{SoundId=promoted.Id,LibraryVersion=bridge.Snapshot.Library.Version},"host")).Ok);
        var stats=await bridge.Execute(Cmd("statistics") with{SoundId=promoted.Id},"host");using var document=JsonDocument.Parse(Json.Write(stats.Data));Assert.Equal(2,document.RootElement.GetProperty("statistics").GetProperty("total").GetInt64());
        Assert.True((await bridge.Execute(Cmd("icon") with{SoundId=promoted.Id,Image=""},"host")).Ok);Assert.Null(bridge.Snapshot.Library.Sounds.Single(s=>s.Id==promoted.Id).Icon);
    }
}
