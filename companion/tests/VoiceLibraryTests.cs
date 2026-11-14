using System.Security.Cryptography;
using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// Stage F1 (2.41): the voice library (its files, Keep → recording with provenance and words, edits with audit and
/// Undo, permissions), the word index and search, the Soundpad category word index, wanted-word notices, history
/// search, and the speaker grouping prototype. Mock Soundpad, fixture capture and fixture transcripts only.
/// </summary>
[Collection("History")]
public sealed class VoiceLibraryTests : IDisposable
{
    private const int Rate = MimicFixture.Rate;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-voices-" + Guid.NewGuid().ToString("N"));
    public VoiceLibraryTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static JsonElement Body(Reply reply) => JsonSerializer.SerializeToElement(reply.Data, SoundpadWeb.Json.Options);
    private static long S(double seconds) => (long)Math.Round(seconds * Rate);
    private static async Task<Reply> Ok(Task<Reply> reply) { var r = await reply; Assert.True(r.Ok, r.Error); return r; }
    private static async Task<string> Refused(Task<Reply> reply) { var r = await reply; Assert.False(r.Ok, "expected a refusal"); return r.Error!; }
    private static readonly Participant Alice = new("11111111-1111-1111-1111-111111111111", "Alice");
    private static readonly Participant Bob = new("22222222-2222-2222-2222-222222222222", "Bob");

    private sealed class Rig : IAsyncDisposable
    {
        public readonly MockSoundpad Mock = new();
        public readonly SoundpadPipe Pipe;
        public readonly Bridge Bridge;
        public readonly MimicFixture Capture = new();
        public readonly TranscriptFixture Engine = new();
        private long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long Now => Interlocked.Read(ref now);
        public Rig(string folder, Action<MockSoundpad>? setup = null)
        {
            setup?.Invoke(Mock);
            Pipe = new SoundpadPipe(Mock.Name, true);
            Bridge = new Bridge(Pipe, new StateStore(folder));
            Bridge.UseMimicCapture(Capture); Bridge.UseTranscriptEngine(Engine); Bridge.UseHotkey(new HotkeyFixture());
            Bridge.UseClock(() => Now);
            Bridge.WorkerPauseMs = 0; Bridge.BackgroundWorker = false;
        }
        public void Advance(double seconds) { Capture.Advance(TimeSpan.FromSeconds(seconds)); Interlocked.Add(ref now, (long)(seconds * 1000)); }
        public async Task Host(bool listen = true)
        {
            await Bridge.Poll(true);
            await Ok(Bridge.Execute(Cmd("beginSession"), "host"));
            await Ok(Bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host"));
            if (!listen) return;
            await Ok(Bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"));
            await Task.Delay(20); Capture.Pump();
        }
        public string Session => Bridge.Snapshot.Mimic!.History!.SessionId!;
        public Task<Reply> As(Participant who, Command c) => Bridge.Execute(c, "guest", who, "peer-" + who.Name);
        public async ValueTask DisposeAsync() { Bridge.Dispose(); Pipe.Dispose(); await Mock.DisposeAsync(); }
        /// <summary>Transcripts on, then waits until [from, to) of the history is recognised.</summary>
        public async Task Recognised(double from, double to)
        {
            for (int i = 0; i < 200; i++)
            {
                var t = Body(await Ok(Bridge.Execute(Cmd("historyTranscript") with { StartFrame = S(from), EndFrame = S(to), SourceId = Session }, "host")));
                if (t.GetProperty("words").GetArrayLength() > 0 && t.GetProperty("pending").GetArrayLength() == 0) return;
                await Task.Delay(50);
            }
            Assert.Fail("The history was not recognised in time.");
        }
    }

    // ---- The library's files ------------------------------------------------------------------------------------

    private static LibraryRecording Recording(string folder, string title) =>
        new() { Path = Path.Combine(folder, title + ".wav"), Title = title, DurationMs = 1000, Source = new LibrarySource { Kind = "upload" } };

    [Fact]
    public void TheLibrarySavesAtomicallyKeepsABackupAndValidatesStrictlyOnLoad()
    {
        var index = new WordIndex();
        var library = new VoiceLibrary(folder, index);
        var (voice, _) = library.CreateVoice("Alex", null, "host", "Host");
        var (one, added) = library.Add(Recording(folder, "One"), [new(0, 300, "hello", 0.9f, false), new(300, 700, "there", 0.9f, false)], "host", "Host");
        Assert.True(added);
        library.Edit([one.Id], new LibraryEdit(VoiceId: voice.Id, Tags: ["funny"]), "host", "Host");
        var main = Path.Combine(folder, "library", "voice-library.v1.json");
        Assert.True(File.Exists(main));
        Assert.True(File.Exists(Path.Combine(folder, "library", "voice-library.v1.bak.json"))); // the version before the last save
        Assert.False(File.Exists(main + ".tmp"));

        // Reloaded: the same recordings, voices and words; the words are searchable again.
        var again = new VoiceLibrary(folder, new WordIndex());
        var loaded = Assert.Single(again.List(new LibraryFilter(), 0, 10).Items);
        Assert.Equal(("One", voice.Id, "funny"), (loaded.Title, loaded.VoiceId, loaded.Tags.Single()));
        Assert.Equal(["hello", "there"], again.WordsOf(one.Id).Select(w => w.Text));
        Assert.Null(again.Problem);

        // A damaged main file: the previous version (the backup) loads, and says so.
        var good = File.ReadAllBytes(main);
        File.WriteAllText(main, "{ not json");
        var fromBackup = new VoiceLibrary(folder, new WordIndex());
        Assert.False(fromBackup.Blocked);
        Assert.Contains("previous version", fromBackup.Problem);

        // A valid-looking file that breaks a rule (a recording with an unknown voice) is refused as a whole.
        var bad = JsonSerializer.Deserialize<LibraryFile>(good, SoundpadWeb.Json.Options)!;
        bad.Recordings[0].VoiceId = new string('a', 32);
        Assert.Throws<InvalidDataException>(() => VoiceLibrary.Validate(bad));
        bad = JsonSerializer.Deserialize<LibraryFile>(good, SoundpadWeb.Json.Options)!;
        bad.Voices[0].Color = "red";
        Assert.Throws<InvalidDataException>(() => VoiceLibrary.Validate(bad));
        bad = JsonSerializer.Deserialize<LibraryFile>(good, SoundpadWeb.Json.Options)!;
        bad.SchemaVersion = 2;
        Assert.Throws<InvalidDataException>(() => VoiceLibrary.Validate(bad));

        // Both damaged: blocked, nothing written over them, and changes are refused with the reason.
        File.WriteAllText(Path.Combine(folder, "library", "voice-library.v1.bak.json"), "[]");
        var blocked = new VoiceLibrary(folder, new WordIndex());
        Assert.True(blocked.Blocked);
        Assert.Throws<InvalidDataException>(() => blocked.CreateVoice("Sam", null, "host", "Host"));
        Assert.Equal("{ not json", File.ReadAllText(main));
    }

    [Fact]
    public void EditsAreAuditedAndUndoneOnlyByTheirAuthorOrTheHostWhileUnchanged()
    {
        var library = new VoiceLibrary(folder, new WordIndex());
        var (a, _) = library.Add(Recording(folder, "A"), null, "host", "Host");
        var (b, _) = library.Add(Recording(folder, "B"), null, "host", "Host");
        var (voice, _) = library.CreateVoice("Alex", "#3fb68b", "alice", "Alice");
        // One bulk action: both recordings get the voice and a tag, as one Undo.
        long group = library.Edit([a.Id, b.Id], new LibraryEdit(VoiceId: voice.Id, AddTags: ["laugh"], Favorite: true), "alice", "Alice");
        var trail = library.Audit(a.Id);
        Assert.Contains(trail, e => e.Field == "voice" && e.ByName == "Alice" && e.After == JsonSerializer.Serialize(voice.Id));
        Assert.All(library.Audit().Where(e => e.Group == group), e => Assert.Equal("Alice", e.ByName));
        Assert.Contains("only your own", Assert.Throws<InvalidDataException>(() => library.Undo(group, "bob", "Bob", false)).Message);
        library.Undo(group, "alice", "Alice", false);
        Assert.All(new[] { a, b }, r => { var now = library.Get(r.Id)!; Assert.Null(now.VoiceId); Assert.Empty(now.Tags); Assert.False(now.Favorite); });
        Assert.Contains("already undone", Assert.Throws<InvalidDataException>(() => library.Undo(group, "alice", "Alice", false)).Message);

        // Changed again since: Undo refuses and changes nothing.
        long title = library.Edit([a.Id], new LibraryEdit(Title: "First title"), "bob", "Bob");
        library.Edit([a.Id], new LibraryEdit(Title: "Second title"), "alice", "Alice");
        Assert.Contains("changed again", Assert.Throws<InvalidDataException>(() => library.Undo(title, "bob", "Bob", false)).Message);
        Assert.Equal("Second title", library.Get(a.Id)!.Title);

        // Corrected text is kept apart from the words; the host may undo anyone's change.
        long corrected = library.Edit([b.Id], new LibraryEdit(CorrectedText: "what they really said"), "bob", "Bob");
        Assert.Equal("what they really said", library.Get(b.Id)!.CorrectedText);
        library.Undo(corrected, "host", "Host", true);
        Assert.Null(library.Get(b.Id)!.CorrectedText);

        // Taking a recording out of the library never deletes its file; Undo puts it back.
        File.WriteAllBytes(a.Path, [1, 2, 3]);
        long removed = library.Remove([a.Id], "alice", "Alice");
        Assert.Null(library.Get(a.Id));
        Assert.True(File.Exists(a.Path));
        library.Undo(removed, "alice", "Alice", false);
        Assert.Equal("Second title", library.Get(a.Id)!.Title);
        // A voice with recordings cannot be deleted; wanted words come and go with Undo.
        library.Edit([a.Id], new LibraryEdit(VoiceId: voice.Id), "alice", "Alice");
        Assert.Contains("still has recordings", Assert.Throws<InvalidDataException>(() => library.DeleteVoice(voice.Id, "host", "Host")).Message);
        var (wanted, added) = library.AddWanted(voice.Id, "Hello there!", "alice", "Alice");
        Assert.Equal(0, library.AddWanted(voice.Id, "hello THERE", "bob", "Bob").Group); // the same words: no duplicate
        library.Undo(added, "alice", "Alice", false);
        Assert.Empty(library.Wanted);
        // The audit trail survives a restart.
        Assert.Contains(new VoiceLibrary(folder, new WordIndex()).Audit(), e => e.Target == "wanted" && e.TargetId == wanted.Id);
    }

    // ---- The word index ----------------------------------------------------------------------------------------

    [Fact]
    public void TheWordIndexFindsExactWordsAndPhrasesAndUpdatesOneDocumentAtATime()
    {
        var index = new WordIndex();
        Assert.Equal(["don't", "stop", "now"], WordIndex.Query("  Don’t STOP, now! "));
        index.Set("r:a", ["Well,", "don't", "stop", "now."]);
        index.Set("r:b", ["stop", "the", "car"]);
        index.Set("s:c", ["now", "stop", "now"]);
        Assert.Equal([("r:a", 2), ("r:b", 0), ("s:c", 1)], index.Find(["stop"]).OrderBy(h => h.Key).ToList());
        Assert.Equal([("r:a", 2)], index.Find(["stop", "now"]).Where(h => h.Key == "r:a").ToList());
        Assert.Equal(2, index.Find(["stop", "now"]).Count); // r:a and s:c, not r:b
        Assert.Empty(index.Find(["stop", "car"]));
        Assert.Single(index.Find(["stop"], key => key.StartsWith("s:")));
        // Replacing one document changes only its hits; removing it takes them away.
        index.Set("r:b", ["drive", "the", "car"]);
        Assert.Equal(2, index.Find(["stop"]).Count);
        index.Remove("r:a");
        Assert.Equal([("s:c", 1)], index.Find(["stop"]).ToList());
        Assert.Equal(2, index.Vocabulary(_ => true)["now"]);
        Assert.Equal(2, index.Count);
        // Thousands of documents: adding one more does not rebuild anything (it is fast however big the index is).
        for (int i = 0; i < 5000; i++) index.Set("r:" + i, ["word" + (i % 50), "common", "phrase" + i]);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        index.Set("r:new", ["common", "phrase4999"]);
        Assert.True(clock.ElapsedMilliseconds < 50, $"one update took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(2, index.Find(["common", "phrase4999"]).Count);
        Assert.Equal(100, index.Find(["word7"]).Count);
    }

    // ---- Keep → recording -------------------------------------------------------------------------------------

    [Fact]
    public async Task ADraftKeptIntoTheLibraryBecomesARecordingWithProvenanceAndItsHistoryWords()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host();
        await Ok(bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host"));
        rig.Advance(12);
        await rig.Recognised(3.8, 5);
        var draft = Body(await Ok(bridge.Execute(Cmd("draftCreate") with { StartFrame = S(3.8), EndFrame = S(5), SourceId = rig.Session }, "host")));
        var kept = Body(await Ok(bridge.Execute(Cmd("draftKeep") with { DraftId = draft.GetProperty("id").GetString(), Action = "library" }, "host")));
        Assert.True(kept.GetProperty("closed").GetBoolean());
        string recordingId = kept.GetProperty("recordingId").GetString()!;
        var library = bridge.VoiceLibraryStore;
        var recording = library.Get(recordingId)!;
        Assert.Equal(("history", rig.Session, S(3.8), S(5), Rate), (recording.Source.Kind, recording.Source.SessionId, recording.Source.StartFrame, recording.Source.EndFrame, recording.Source.Rate));
        Assert.Equal("ready", recording.WordsState); // copied from the history's transcript, no second recognition
        var words = library.WordsOf(recordingId);
        Assert.Equal(["Fixture", "tone"], words.Take(2).Select(w => w.Text));
        Assert.InRange(words[0].StartMs, 150, 300); // the tone starts 0.2 s into the clip
        Assert.True(File.Exists(recording.Path));
        Assert.StartsWith(Path.Combine(folder, "library"), recording.Path);
        // Everyone sees it (without its path), with a playable sound id.
        var item = Body(await Ok(rig.As(Alice, Cmd("libraryGet") with { RecordingId = recordingId })));
        Assert.DoesNotContain(folder, item.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("history", item.GetProperty("recording").GetProperty("source").GetString());
        string soundId = item.GetProperty("recording").GetProperty("soundId").GetString()!;
        Assert.Contains(bridge.Snapshot.Library.Sounds, s => s.Id == soundId);

        // Search finds the word, with its time to play from (an exact slice through the existing region path).
        var found = Body(await Ok(rig.As(Alice, Cmd("librarySearch") with { Query = "tone" })));
        var hit = Assert.Single(found.GetProperty("results").EnumerateArray());
        Assert.Equal(("clip", recordingId), (hit.GetProperty("type").GetString(), hit.GetProperty("id").GetString()));
        var word = hit.GetProperty("context").EnumerateArray().Single(w => w.GetProperty("hit").GetBoolean());
        Assert.Equal("tone", word.GetProperty("t").GetString());
        await Ok(bridge.Execute(Cmd("region") with { SoundId = soundId, PositionMs = word.GetProperty("s").GetInt64(), EndMs = word.GetProperty("e").GetInt64() }, "host"));
        // A private preview of that one word, for the guest only (16 kHz).
        var wav = Convert.FromBase64String(Body(await Ok(rig.As(Alice, Cmd("libraryPreview") with { RecordingId = recordingId, PositionMs = word.GetProperty("s").GetInt64(), EndMs = word.GetProperty("e").GetInt64() }))).GetProperty("wav").GetString()!);
        Assert.Equal(Bridge.GuestPreviewRate, BitConverter.ToInt32(wav, 24));
    }

    [Fact]
    public async Task EveryKeepFromTempBecomesARecordingAndOtherFilesAreTranscribedInTheBackground()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host();
        rig.Advance(8);
        // A clip made from the history (transcripts off: its words are found from the kept file instead).
        var clip = Body(await Ok(bridge.Execute(Cmd("mimicClip") with { StartFrame = S(1.8), EndFrame = S(3), SourceId = rig.Session, Name = "From history" }, "host")));
        string assetId = clip.GetProperty("assetId").GetString()!;
        var variant = bridge.Snapshot.Library.Sounds.First(s => s.AssetId == assetId);
        var asset = Body(await Ok(bridge.Execute(Cmd("audioAssets"), "host"))).GetProperty("manager").GetProperty("assets").EnumerateArray().First(a => a.GetProperty("id").GetString() == assetId);
        string variantId = asset.GetProperty("normal").GetProperty("id").GetString()!;
        await Ok(bridge.Execute(Cmd("keepAudio") with { AssetId = assetId, VariantId = variantId, Confirmed = true }, "host"));
        var library = bridge.VoiceLibraryStore;
        var recording = library.FindByAsset(assetId)!;
        Assert.Equal(("history", "pending"), (recording.Source.Kind, recording.WordsState));
        Assert.Equal("From history", recording.Title);
        int calls = rig.Engine.Calls;
        Assert.True(await bridge.DrainLibraryWorker(TimeSpan.FromSeconds(20)));
        Assert.True(rig.Engine.Calls > calls);
        Assert.Equal("ready", library.Get(recording.Id)!.WordsState);
        Assert.Contains(library.WordsOf(recording.Id), w => w.Text == "tone");
        // Keeping it again changes nothing: one recording per kept clip.
        await Ok(bridge.Execute(Cmd("keepAudio") with { AssetId = assetId, VariantId = variantId, Confirmed = true }, "host"));
        Assert.Equal(1, library.Count);
        // A Soundpad sound kept before 2.41 (or any sound) is added by hand, with "soundpad" provenance.
        var added = Body(await Ok(bridge.Execute(Cmd("libraryAdd") with { SoundId = bridge.Snapshot.Library.Sounds.First(s => s.Name == "Airhorn").Id }, "host")));
        Assert.True(added.GetProperty("added").GetBoolean());
        Assert.Equal("soundpad", library.Get(added.GetProperty("recordingId").GetString()!)!.Source.Kind);
    }

    // ---- Permissions -------------------------------------------------------------------------------------------

    [Fact]
    public async Task GuestsEditWithUndoUnlessTheHostTurnsLibraryEditingOff()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host(listen: false);
        var library = bridge.VoiceLibraryStore;
        var (r, _) = library.Add(Recording(folder, "Shared"), [new(0, 400, "yes", 1, false)], "host", "Host");
        Assert.True(bridge.Snapshot.Permissions.GuestLibraryEdit);
        var voice = Body(await Ok(rig.As(Alice, Cmd("libraryVoice") with { Action = "create", Name = "Sam" }))).GetProperty("voice").GetProperty("id").GetString()!;
        long group = Body(await Ok(rig.As(Alice, Cmd("libraryEdit") with { RecordingId = r.Id, VoiceId = voice, Tags = ["greeting"] }))).GetProperty("group").GetInt64();
        Assert.Contains("only your own", await Refused(rig.As(Bob, Cmd("libraryUndo") with { AuditId = group })));
        var audit = Body(await Ok(rig.As(Bob, Cmd("libraryAudit") with { RecordingId = r.Id })));
        Assert.Contains(audit.GetProperty("entries").EnumerateArray(), e => e.GetProperty("byName").GetString() == "Alice" && e.GetProperty("field").GetString() == "tags");
        await Ok(rig.As(Alice, Cmd("libraryUndo") with { AuditId = group }));
        Assert.Empty(library.Get(r.Id)!.Tags);

        // Off: guests still see and search the library, but cannot change it. The switch is not in state.json.
        await Ok(bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true, GuestLibraryEdit: false) }, "host"));
        Assert.False(bridge.Snapshot.Permissions.GuestLibraryEdit);
        Assert.Contains("not allowed guests to edit", await Refused(rig.As(Alice, Cmd("libraryEdit") with { RecordingId = r.Id, Favorite = true })));
        Assert.Contains("not allowed guests to edit", await Refused(rig.As(Alice, Cmd("libraryRemove") with { RecordingIds = [r.Id] })));
        await Ok(rig.As(Alice, Cmd("librarySearch") with { Query = "yes" }));
        Assert.DoesNotContain("guestLibraryEdit", File.ReadAllText(Path.Combine(folder, "state.json")), StringComparison.OrdinalIgnoreCase);
        Assert.False(new VoiceLibrary(folder, new WordIndex()).Settings.GuestLibraryEdit);
        // Host-only switches stay the host's.
        Assert.Contains("Only the host", await Refused(rig.As(Alice, Cmd("libraryIndex") with { Categories = ["Reactions"] })));
        Assert.Contains("Only the host", await Refused(rig.As(Alice, Cmd("librarySettings") with { GroupSpeakers = true })));
    }

    // ---- Wanted words ------------------------------------------------------------------------------------------

    [Fact]
    public async Task WantedWordsShowWhenAvailableAndGiveOneQuietNoticePerMatch()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host();
        var voice = Body(await Ok(bridge.Execute(Cmd("libraryVoice") with { Action = "create", Name = "Robo" }, "host"))).GetProperty("voice").GetProperty("id").GetString()!;
        await Ok(bridge.Execute(Cmd("libraryWanted") with { Action = "add", VoiceId = voice, Query = "tone" }, "host"));
        var status = Body(await Ok(bridge.Execute(Cmd("libraryStatus"), "host")));
        Assert.False(status.GetProperty("wanted")[0].GetProperty("available").GetBoolean());
        // The live history says it: one possible-match notice, with where (a jump marker), not repeated.
        await Ok(bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host"));
        rig.Advance(6);
        await rig.Recognised(1.8, 3);
        LibraryNotice[] notices = [];
        for (int i = 0; i < 100 && notices.Length == 0; i++)
        {
            await Task.Delay(50);
            notices = Body(await Ok(bridge.Execute(Cmd("libraryStatus"), "host"))).GetProperty("notices").Deserialize<LibraryNotice[]>(SoundpadWeb.Json.Options)!;
        }
        var first = notices.First();
        Assert.Equal(("history", "tone", rig.Session), (first.Kind, first.Text, first.SessionId));
        Assert.True(first.EndFrame > first.StartFrame);
        Assert.Equal(notices.Length, notices.Select(n => (n.StartFrame, n.Text)).Distinct().Count());
        // A guest who joined later is not told about what was said before they joined.
        bridge.NoteGuest(Alice, "peer-Alice", rig.Now);
        Assert.Empty(Body(await Ok(rig.As(Alice, Cmd("libraryStatus")))).GetProperty("notices").EnumerateArray());
        // A recording of that voice with the word: available now, and a notice for the recording.
        var library = bridge.VoiceLibraryStore;
        var (r, _) = library.Add(Recording(folder, "Robot line"), [new(0, 300, "a", 1, false), new(300, 600, "tone", 1, false)], "host", "Host");
        await Ok(bridge.Execute(Cmd("libraryEdit") with { RecordingId = r.Id, VoiceId = voice }, "host"));
        status = Body(await Ok(bridge.Execute(Cmd("libraryStatus"), "host")));
        Assert.True(status.GetProperty("wanted")[0].GetProperty("available").GetBoolean());
        Assert.Contains(status.GetProperty("notices").EnumerateArray(), n => n.GetProperty("kind").GetString() == "recording" && n.GetProperty("recordingId").GetString() == r.Id);
        // History search: the same words, newest first, within what the asker may see.
        var search = Body(await Ok(bridge.Execute(Cmd("historySearch") with { Query = "fixture tone" }, "host")));
        Assert.True(search.GetProperty("matches").GetArrayLength() >= 1);
        Assert.Empty(Body(await Ok(rig.As(Alice, Cmd("historySearch") with { Query = "fixture tone" }))).GetProperty("matches").EnumerateArray());
    }

    // ---- The Soundpad category word index --------------------------------------------------------------------

    private static string Burst(string path, double toneAt = 0.4)
    {
        var samples = new float[48000 * 2];
        for (int i = (int)(toneAt * 48000); i < (int)((toneAt + 0.6) * 48000); i++) samples[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 48000);
        File.WriteAllBytes(path, MimicRing.Wav(samples, 48000));
        return path;
    }
    private static (string Hash, DateTime Time) Signature(string path) => (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path));

    [Fact]
    public async Task ChosenCategoriesAreIndexedOnceResumeAfterARestartAndTheirFilesNeverChange()
    {
        var sounds = Path.Combine(folder, "sounds"); Directory.CreateDirectory(sounds);
        foreach (var name in new[] { "airhorn", "laugh", "other-laugh", "long" }) Burst(Path.Combine(sounds, name + ".wav"));
        void Point(MockSoundpad mock) { mock.AllXml = MockSoundpad.Sounds.Replace(@"C:\Private\", sounds + @"\"); mock.CategoryXml = MockSoundpad.Categories.Replace(@"C:\Private\", sounds + @"\"); }
        var before = Directory.GetFiles(sounds).ToDictionary(p => p, Signature);
        var data = Path.Combine(folder, "data"); Directory.CreateDirectory(data);
        await using (var rig = new Rig(data, Point))
        {
            await rig.Host(listen: false);
            // "Reactions" holds laugh.wav and, in its sub-category "Deep cuts", airhorn.wav.
            var progress = Body(await Ok(rig.Bridge.Execute(Cmd("libraryIndex") with { Categories = ["Reactions"], Action = "pause" }, "host")));
            Assert.Equal(2, progress.GetProperty("total").GetInt32());
            Assert.True(progress.GetProperty("paused").GetBoolean());
            int calls = rig.Engine.Calls;
            Assert.True(await rig.Bridge.DrainLibraryWorker(TimeSpan.FromSeconds(5)));
            Assert.Equal(calls, rig.Engine.Calls); // paused: nothing read
            await Ok(rig.Bridge.Execute(Cmd("libraryIndex") with { Action = "resume" }, "host"));
            Assert.True(await rig.Bridge.DrainLibraryWorker(TimeSpan.FromSeconds(20)));
            Assert.Equal(calls + 2, rig.Engine.Calls); // one file at a time, each once
            var status = Body(await Ok(rig.Bridge.Execute(Cmd("libraryStatus"), "host"))).GetProperty("index");
            Assert.Equal((2, 2, 0), (status.GetProperty("total").GetInt32(), status.GetProperty("indexed").GetInt32(), status.GetProperty("failed").GetInt32()));
            // Word search finds the Soundpad sound, linked by its sound id (no path), with the word's time.
            var found = Body(await Ok(rig.As(Alice, Cmd("librarySearch") with { Query = "fixture", Kind = "sound" })));
            Assert.Equal(2, found.GetProperty("total").GetInt32());
            var first = found.GetProperty("results")[0];
            Assert.Equal("sound", first.GetProperty("type").GetString());
            Assert.Contains(rig.Bridge.Snapshot.Library.Sounds, s => s.Id == first.GetProperty("soundId").GetString());
            Assert.DoesNotContain(sounds, found.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.InRange(first.GetProperty("context")[0].GetProperty("s").GetInt64(), 300, 500);
        }
        // Never written: same bytes, same times.
        Assert.All(before, p => Assert.Equal(p.Value, Signature(p.Key)));
        // After a restart, files already indexed (same length and time) are skipped; a changed file is read again.
        File.SetLastWriteTimeUtc(Burst(Path.Combine(sounds, "laugh.wav"), 1.0), DateTime.UtcNow.AddMinutes(1));
        await using (var rig = new Rig(data, Point))
        {
            await rig.Host(listen: false);
            Assert.True(await rig.Bridge.DrainLibraryWorker(TimeSpan.FromSeconds(20)));
            Assert.Equal(1, rig.Engine.Calls);
            var entry = rig.Bridge.SoundWordStore.Get(Path.Combine(sounds, "laugh.wav"))!;
            Assert.InRange(entry.Words![0].StartMs, 900, 1100);
        }
    }

    // ---- Speaker grouping --------------------------------------------------------------------------------------

    /// <summary>A synthetic voice: a pitch pulse train through three formant resonators, vowels changing every 150 ms.</summary>
    private static float[] Voice(double f0, double formants, int seed, double seconds = 2.0)
    {
        const int rate = 16000; var random = new Random(seed); var output = new float[(int)(seconds * rate)];
        double[][] vowels = [[730, 1090, 2440], [270, 2290, 3010], [300, 870, 2240], [530, 1840, 2480], [570, 840, 2410], [440, 1020, 2240]];
        double phase = 0, glottal = 0; var y1 = new double[3]; var y2 = new double[3];
        double[] vowel = vowels[0];
        for (int i = 0; i < output.Length; i++)
        {
            if (i % (rate * 3 / 20) == 0) vowel = vowels[random.Next(vowels.Length)];
            double t = (double)i / rate, f = f0 * (1 + 0.03 * Math.Sin(2 * Math.PI * 5 * t) + 0.04 * Math.Sin(2 * Math.PI * 0.7 * t + seed));
            phase += f / rate; double pulse = 0;
            if (phase >= 1) { phase -= 1; pulse = 1; }
            glottal = 0.9 * glottal + pulse;
            double x = glottal + 0.02 * (random.NextDouble() - 0.5);
            for (int k = 0; k < 3; k++)
            {
                double r = Math.Exp(-Math.PI * (80 + 30 * k) / rate), theta = 2 * Math.PI * vowel[k] * formants / rate;
                double y = (1 - r) * x + 2 * r * Math.Cos(theta) * y1[k] - r * r * y2[k];
                y2[k] = y1[k]; y1[k] = y; x = y;
            }
            bool gap = (i % (rate * 6 / 10)) < rate / 20; // a short pause between "words"
            output[i] = gap ? 0 : (float)x;
        }
        float peak = output.Max(Math.Abs);
        for (int i = 0; i < output.Length; i++) output[i] *= 0.5f / peak;
        return output;
    }

    [Fact]
    public void SpeakerGroupingSeparatesDistinctSyntheticVoicesAndLeavesTheUncertainUnassigned()
    {
        (string Name, double F0, double Formants)[] speakers = [("low", 110, 1.0), ("high", 220, 1.2), ("middle", 155, 0.88)];
        var random = new Random(3); var grouper = new SpeakerGrouper(); var results = new List<(string, int?)>();
        for (int n = 0; n < 45; n++)
        {
            var who = speakers[random.Next(speakers.Length)];
            results.Add((who.Name, grouper.Assign(SpeakerAnalysis.Analyze(Voice(who.F0, who.Formants, n)))));
        }
        var (purity, coverage, groups) = SpeakerGrouper.Score(results);
        Assert.True(purity >= 0.95, $"purity {purity:P0}");
        Assert.True(coverage >= 0.6, $"coverage {coverage:P0}");
        Assert.InRange(groups, 3, 5);
        // Too little voiced speech says nothing about who spoke: Unassigned, never a guess.
        Assert.Null(grouper.Assign(SpeakerAnalysis.Analyze(new float[16000])));
        Assert.Null(grouper.Assign(SpeakerAnalysis.Analyze(Voice(110, 1.0, 99, 0.2))));
    }

    [Fact]
    public async Task SpeakerGroupsAreOffByDefaultAndNamedOrLinkedOnlyByAPerson()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host();
        Assert.False(Body(await Ok(bridge.Execute(Cmd("speakerGroups"), "host"))).GetProperty("enabled").GetBoolean());
        await Ok(bridge.Execute(Cmd("librarySettings") with { GroupSpeakers = true }, "host"));
        await Ok(bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host"));
        rig.Advance(8);
        await rig.Recognised(1.8, 5);
        JsonElement groups = default;
        for (int i = 0; i < 100; i++)
        {
            groups = Body(await Ok(bridge.Execute(Cmd("speakerGroups"), "host")));
            if (groups.GetProperty("groups").GetArrayLength() + groups.GetProperty("unassigned").GetInt32() > 0) break;
            await Task.Delay(50);
        }
        Assert.True(groups.GetProperty("enabled").GetBoolean());
        // Whatever the fixture tone was grouped as, a group is named and linked only by a person.
        if (groups.GetProperty("groups").GetArrayLength() > 0)
        {
            int id = groups.GetProperty("groups")[0].GetProperty("id").GetInt32();
            Assert.StartsWith("Speaker", groups.GetProperty("groups")[0].GetProperty("label").GetString());
            var voice = Body(await Ok(bridge.Execute(Cmd("libraryVoice") with { Action = "create", Name = "Beeper" }, "host"))).GetProperty("voice").GetProperty("id").GetString()!;
            // A group number from another recording is another speaker (2.41 review).
            Assert.Contains("restarted", await Refused(bridge.Execute(Cmd("speakerLink") with { GroupId = id, VoiceId = voice, SourceId = "another-recording" }, "host")));
            await Ok(bridge.Execute(Cmd("speakerName") with { GroupId = id, Name = "The beeper", SourceId = rig.Session }, "host"));
            var linked = Body(await Ok(bridge.Execute(Cmd("speakerLink") with { GroupId = id, VoiceId = voice }, "host"))).GetProperty("groups")[0];
            Assert.Equal(("The beeper", voice), (linked.GetProperty("label").GetString(), linked.GetProperty("voiceId").GetString()));
        }
        await Ok(bridge.Execute(Cmd("librarySettings") with { GroupSpeakers = false }, "host"));
        Assert.Empty(Body(await Ok(bridge.Execute(Cmd("speakerGroups"), "host"))).GetProperty("groups").EnumerateArray());
    }

    // ---- Review of 2.41 (second pass) -------------------------------------------------------------------------

    [Fact]
    public async Task ALongBackgroundFileGivesTheSpeechEngineBackBetweenItsPieces()
    {
        // 60 s: three pieces of at most 25 s. A live subtitle (or copy naming) waiting meanwhile runs after the first.
        var gate = new SemaphoreSlim(1, 1); var order = new List<string>(); Task? live = null;
        var spans = await WhisperEngine.InPieces(new float[60 * MimicTranscriber.SpeechRate], gate, piece =>
        {
            lock (order) order.Add("piece " + piece.Length / MimicTranscriber.SpeechRate);
            live ??= Task.Run(async () => { await gate.WaitAsync(); lock (order) order.Add("live"); gate.Release(); });
            return Task.FromResult<IReadOnlyList<TranscribedSpan>>([new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "word")]);
        }, CancellationToken.None);
        await live!;
        Assert.Equal(["piece 25", "live", "piece 25", "piece 10"], order);
        Assert.Equal([0.0, 25.0, 50.0], spans.Select(s => s.Start.TotalSeconds));
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task KeepInVoiceLibrarySaysSoAndKeepsTheDraftWhenTheLibraryCannotTakeTheClip()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host();
        rig.Advance(6);
        var library = bridge.VoiceLibraryStore;
        // The library's file cannot be written (its temporary name is taken by a folder).
        var blocker = Path.Combine(library.Folder, "voice-library.v1.json.tmp");
        Directory.CreateDirectory(blocker);
        string draftId = Body(await Ok(bridge.Execute(Cmd("draftCreate") with { StartFrame = S(1.8), EndFrame = S(3), SourceId = rig.Session }, "host"))).GetProperty("id").GetString()!;
        var error = await Refused(bridge.Execute(Cmd("draftKeep") with { DraftId = draftId, Action = "library" }, "host"));
        Assert.Contains("could not be added to the voice library", error);
        Assert.Equal(0, library.Count);
        // The draft stays open and pinned, so the person can try again.
        Assert.Contains(Body(await Ok(bridge.Execute(Cmd("draftList"), "host"))).GetProperty("drafts").EnumerateArray(), d => d.GetProperty("id").GetString() == draftId);
        Assert.Contains("draft:" + draftId, bridge.History!.Pins(HistoryAccess.Host).Keys);
        // Once the library can save again, the same draft goes into it.
        Directory.Delete(blocker);
        var kept = Body(await Ok(bridge.Execute(Cmd("draftKeep") with { DraftId = draftId, Action = "library" }, "host")));
        Assert.NotNull(library.Get(kept.GetProperty("recordingId").GetString()!));
    }

    [Fact]
    public async Task TheSnapshotNeverWaitsForTheVoiceLibrary()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host(listen: false);
        var library = bridge.VoiceLibraryStore;
        // Hold the library's lock, as a save of a large library file does.
        var gate = typeof(VoiceLibrary).GetField("gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(library)!;
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } }) { IsBackground = true };
        holder.Start(); held.Wait();
        try
        {
            var snapshot = Task.Run(() => bridge.Snapshot);
            Assert.True(await Task.WhenAny(snapshot, Task.Delay(3000)) == snapshot, "the snapshot waited for the library's lock");
            Assert.True((await snapshot).Permissions.GuestLibraryEdit);
        }
        finally { release.Set(); holder.Join(); }
    }

    [Fact]
    public void WordsRecognisedForDiscardedAudioAreNeverAddedBack()
    {
        var transcript = TranscriptWords.Open(folder, "session");
        transcript.Add([new(0, S(1), S(1.5), "Old", 0.9f, 0, true, true, WordStatus.Done, 1)], new(0, S(2)));
        transcript.Add([new(0, S(5), S(5.5), "New", 0.9f, 0, true, true, WordStatus.Done, 2)], new(S(4), S(6)));
        Assert.Equal([1L], transcript.DropBefore(S(3)));
        // A recognition of the discarded audio that was already running finishes now: nothing of it is kept.
        Assert.Empty(transcript.Add([new(0, S(2.1), S(2.5), "Late", 0.9f, 0, true, true, WordStatus.Done, 3)], new(S(2), S(2.9))));
        // One that straddles the boundary keeps only what is after it.
        var added = transcript.Add([new(0, S(2.8), S(3.1), "Edge", 0.9f, 0, true, true, WordStatus.Done, 4), new(0, S(3.2), S(3.6), "After", 0.9f, 0, true, true, WordStatus.Done, 4)], new(S(2.5), S(4)));
        Assert.Equal(["After"], added.Select(w => w.Text));
        Assert.Equal(["After", "New"], transcript.Between(0, S(10)).Select(w => w.Word.Text));
        Assert.Equal([new HistoryRange(S(3), S(6))], transcript.Covered);
        var file = File.ReadAllText(Path.Combine(TranscriptWords.FolderFor(folder, "session"), "words.jsonl"));
        Assert.DoesNotContain("Late", file);
        Assert.DoesNotContain("Edge", file);
    }

    private static Action<MockSoundpad> PointAt(string sounds) => mock =>
    {
        mock.AllXml = MockSoundpad.Sounds.Replace(@"C:\Private\", sounds + @"\");
        mock.CategoryXml = MockSoundpad.Categories.Replace(@"C:\Private\", sounds + @"\");
    };

    [Fact]
    public async Task AFinishedIndexPassStartsAgainForFilesChangedSince()
    {
        var sounds = Path.Combine(folder, "sounds"); Directory.CreateDirectory(sounds);
        foreach (var name in new[] { "airhorn", "laugh", "other-laugh", "long" }) Burst(Path.Combine(sounds, name + ".wav"));
        await using var rig = new Rig(folder, PointAt(sounds)); var bridge = rig.Bridge;
        await rig.Host(listen: false);
        await Ok(bridge.Execute(Cmd("libraryIndex") with { Categories = ["Reactions"] }, "host"));
        Assert.True(await bridge.DrainLibraryWorker(TimeSpan.FromSeconds(20)));
        Assert.Equal(2, rig.Engine.Calls);
        // laugh.wav is saved again at the same path while the companion runs.
        var laugh = Path.Combine(sounds, "laugh.wav");
        File.SetLastWriteTimeUtc(Burst(laugh, 1.0), DateTime.UtcNow.AddMinutes(1));
        bridge.CategoryRescanMs = 0;
        Assert.True(await bridge.DrainLibraryWorker(TimeSpan.FromSeconds(20)));
        Assert.Equal(3, rig.Engine.Calls);
        Assert.InRange(bridge.SoundWordStore.Get(laugh)!.Words![0].StartMs, 900, 1100);
    }

    [Fact]
    public async Task OnlyTheCategoriesIndexedNowAreSearchedOrHeardByGuestsAndGuestsAddOnlyThose()
    {
        var sounds = Path.Combine(folder, "sounds"); Directory.CreateDirectory(sounds);
        foreach (var name in new[] { "airhorn", "laugh", "other-laugh", "long" }) Burst(Path.Combine(sounds, name + ".wav"));
        await using var rig = new Rig(folder, PointAt(sounds)); var bridge = rig.Bridge;
        await rig.Host(listen: false);
        await Ok(bridge.Execute(Cmd("libraryIndex") with { Categories = ["Reactions"] }, "host"));
        Assert.True(await bridge.DrainLibraryWorker(TimeSpan.FromSeconds(20)));
        // laugh.wav is in Reactions; other-laugh.wav (also titled Laugh) only in Favorites, with airhorn.wav.
        var catalog = bridge.Snapshot.Library;
        var reactions = catalog.Categories.First(c => c.Name == "Reactions");
        string laugh = catalog.Sounds.First(s => s.Name == "Laugh" && reactions.SoundIds.Contains(s.Id)).Id;
        string other = catalog.Sounds.First(s => s.Name == "Laugh" && s.Id != laugh).Id;
        string airhorn = catalog.Sounds.First(s => s.Name == "Airhorn").Id;
        Assert.Equal(2, Body(await Ok(rig.As(Alice, Cmd("librarySearch") with { Query = "fixture", Kind = "sound" }))).GetProperty("total").GetInt32());
        await Ok(rig.As(Alice, Cmd("libraryPreview") with { SoundId = laugh }));
        // A sound in no indexed category: a guest can neither hear it privately nor add it (and then hear it).
        Assert.Contains("Only the host", await Refused(rig.As(Alice, Cmd("libraryAdd") with { SoundId = other })));
        Assert.False((await rig.As(Alice, Cmd("libraryPreview") with { SoundId = other })).Ok);
        Assert.Equal(0, bridge.VoiceLibraryStore.Count);
        // The host takes Reactions out of the index (Favorites holds airhorn too): laugh is no longer found or heard.
        await Ok(bridge.Execute(Cmd("libraryIndex") with { Categories = ["Favorites"], Action = "pause" }, "host"));
        var found = Body(await Ok(rig.As(Alice, Cmd("librarySearch") with { Query = "fixture", Kind = "sound" })));
        Assert.Equal([airhorn], found.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("soundId").GetString()));
        Assert.False((await rig.As(Alice, Cmd("libraryPreview") with { SoundId = laugh })).Ok);
        // The host may still add any sound.
        await Ok(bridge.Execute(Cmd("libraryAdd") with { SoundId = other }, "host"));
    }

    [Fact]
    public async Task PossibleMatchesInTheHistoryReachOnlyGuestsAllowedMimic()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host();
        bridge.NoteGuest(Alice, "peer-Alice", rig.Now);
        var voice = Body(await Ok(bridge.Execute(Cmd("libraryVoice") with { Action = "create", Name = "Robo" }, "host"))).GetProperty("voice").GetProperty("id").GetString()!;
        await Ok(bridge.Execute(Cmd("libraryWanted") with { Action = "add", VoiceId = voice, Query = "tone" }, "host"));
        await Ok(bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host"));
        rig.Advance(6);
        await rig.Recognised(1.8, 3);
        static JsonElement[] History(JsonElement status) => [.. status.GetProperty("notices").EnumerateArray().Where(n => n.GetProperty("kind").GetString() == "history")];
        JsonElement[] seen = [];
        for (int i = 0; i < 100 && seen.Length == 0; i++) { await Task.Delay(50); seen = History(Body(await Ok(rig.As(Alice, Cmd("libraryStatus"))))); }
        Assert.NotEmpty(seen); // allowed Mimic, and it was said after she joined
        await Ok(bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: false) }, "host"));
        Assert.Empty(History(Body(await Ok(rig.As(Alice, Cmd("libraryStatus"))))));
        Assert.NotEmpty(History(Body(await Ok(bridge.Execute(Cmd("libraryStatus"), "host")))));
    }

    [Fact]
    public async Task TheAuditSentToPeopleCarriesNoFilePathsOrParticipantIds()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host(listen: false);
        var library = bridge.VoiceLibraryStore;
        var (r, _) = library.Add(Recording(folder, "Secret file name"), [new(0, 400, "yes", 1, false)], "host", "Host");
        var (voice, _) = library.CreateVoice("Sam", null, Alice.Id, "Alice");
        library.AddWanted(voice.Id, "hello", Alice.Id, "Alice");
        long removed = Body(await Ok(bridge.Execute(Cmd("libraryRemove") with { RecordingIds = [r.Id] }, "host"))).GetProperty("group").GetInt64();
        foreach (var reply in new[] { await Ok(rig.As(Bob, Cmd("libraryAudit"))), await Ok(rig.As(Bob, Cmd("libraryAudit") with { RecordingId = r.Id })) })
        {
            var raw = Body(reply).GetRawText();
            Assert.DoesNotContain("Secret file name.wav", raw);
            Assert.DoesNotContain("path", raw);
            Assert.DoesNotContain(Alice.Id, raw);
            Assert.Contains("Secret file name", raw); // which recording it was, by its title
        }
        // Undo still has everything (kept on this PC): the recording comes back with its file.
        await Ok(bridge.Execute(Cmd("libraryUndo") with { AuditId = removed }, "host"));
        Assert.Equal(Path.Combine(folder, "Secret file name.wav"), library.Get(r.Id)!.Path);
    }

    [Fact]
    public void TheLibraryNeverSavesAFileItWouldRefuseToLoadAndItsAuditStaysBounded()
    {
        var library = new VoiceLibrary(folder, new WordIndex()) { SaveLimit = 20_000, AuditLimit = 4_000 };
        var (one, _) = library.Add(Recording(folder, "One"), null, "host", "Host");
        var (two, _) = library.Add(Recording(folder, "Two"), null, "host", "Host");
        var main = Path.Combine(folder, "library", "voice-library.v1.json");
        var saved = File.ReadAllBytes(main);
        // 4,000 Cyrillic characters are written as \uXXXX: about 24 KB, more than this library may hold.
        var text = new string('\u0436', VoiceLibrary.MaxText);
        Assert.Contains("larger than", Assert.Throws<InvalidDataException>(() => library.Edit([one.Id], new LibraryEdit(CorrectedText: text), "host", "Host")).Message);
        Assert.Null(library.Get(one.Id)!.CorrectedText);
        Assert.Equal(saved, File.ReadAllBytes(main));
        Assert.Null(new VoiceLibrary(folder, new WordIndex()).Problem);
        // A corrected text is one recording's: never many at once.
        Assert.Throws<InvalidDataException>(() => library.Edit([one.Id, two.Id], new LibraryEdit(CorrectedText: "hi"), "host", "Host"));
        // The audit file starts over while the companion runs, once it passes its limit.
        var audit = Path.Combine(folder, "library", "voice-audit.v1.jsonl");
        for (int i = 0; i < 40; i++) library.Edit([one.Id], new LibraryEdit(Title: "Title " + i), "host", "Host");
        Assert.True(File.Exists(Path.Combine(folder, "library", "voice-audit.v1.old.jsonl")));
        Assert.True(!File.Exists(audit) || new FileInfo(audit).Length <= 4_000);
    }

    [Fact]
    public async Task AGuestChangesTheLibraryAtALimitedPace()
    {
        await using var rig = new Rig(folder); var bridge = rig.Bridge;
        await rig.Host(listen: false);
        var (r, _) = bridge.VoiceLibraryStore.Add(Recording(folder, "Shared"), null, "host", "Host");
        bridge.UseLibraryWriteLimit(new RateWindow(2, TimeSpan.FromMinutes(1)));
        await Ok(rig.As(Alice, Cmd("libraryEdit") with { RecordingId = r.Id, Favorite = true }));
        await Ok(rig.As(Alice, Cmd("libraryEdit") with { RecordingId = r.Id, Favorite = false }));
        Assert.Contains("Too many changes", await Refused(rig.As(Alice, Cmd("libraryEdit") with { RecordingId = r.Id, Favorite = true })));
        await Ok(rig.As(Bob, Cmd("libraryEdit") with { RecordingId = r.Id, Favorite = true })); // per guest
        await Ok(bridge.Execute(Cmd("libraryEdit") with { RecordingId = r.Id, Favorite = false }, "host")); // never the host
    }

    [Fact]
    public void AfterTheBackupIsLoadedNewChangesNeverReuseAnAuditId()
    {
        var library = new VoiceLibrary(folder, new WordIndex());
        var (r, _) = library.Add(Recording(folder, "Original"), null, "host", "Host");
        long renamed = library.Edit([r.Id], new LibraryEdit(Title: "Renamed"), "host", "Host");
        // The main file is damaged: the previous version loads, with an audit counter older than its audit trail.
        File.WriteAllText(Path.Combine(folder, "library", "voice-library.v1.json"), "{ not json");
        var again = new VoiceLibrary(folder, new WordIndex());
        Assert.Equal("Original", again.Get(r.Id)!.Title);
        long next = again.Edit([r.Id], new LibraryEdit(Title: "Again"), "host", "Host");
        Assert.True(next > renamed);
        // So Undo takes back exactly that change.
        again.Undo(next, "host", "Host", true);
        Assert.Equal("Original", again.Get(r.Id)!.Title);
    }
}
