using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// Stage F2 (2.42): the sentence builder (matching, rematch of only the changed words, phrase preference, the
/// Smooth–Chaotic seed, missing words, splices), the timeline render (gaps, bleeps, overlaps), Play into game from a
/// slot at a stable revision, kept versions with pinned sources, recorded performances and the absence of any
/// speech-generation path. Mock Soundpad and generated fixture audio only.
/// </summary>
public sealed class SentenceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-sentences-" + Guid.NewGuid().ToString("N"));
    public SentenceTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);

    // ---- A stand-in library --------------------------------------------------------------------------------------

    /// <summary>Recordings as word lists: word i lasts 300 ms from i × 400 ms (100 ms pauses between words).</summary>
    private sealed class FakeCorpus : ISentenceCorpus
    {
        public readonly List<(string Id, string? Voice, string[] Words, bool Altered)> Docs = [];
        public readonly List<CorpusNamed> NamedPieces = [];
        public string Add(string? voice, string text, bool altered = false)
        {
            var id = Guid.NewGuid().ToString("N");
            Docs.Add((id, voice, WordIndex.Query(text).Length > 0 ? text.Split(' ').Select(WordIndex.Token).ToArray() : [], altered));
            return id;
        }
        public IReadOnlyList<CorpusTake> Find(string[] tokens, TakeFilter filter, int max)
        {
            var result = new List<CorpusTake>();
            for (int d = 0; d < Docs.Count; d++)
            {
                var (id, voice, words, altered) = Docs[d];
                if (!filter.Allows(voice, altered)) continue;
                for (int i = 0; i + tokens.Length <= words.Length; i++)
                {
                    if (!tokens.Select((t, k) => words[i + k] == t).All(x => x)) continue;
                    long start = i * 400, end = (i + tokens.Length - 1) * 400 + 300;
                    result.Add(new(id, voice, i, tokens.Length, start, end, i + tokens.Length < words.Length ? 100 : 250, string.Join(' ', tokens), 1, altered, d));
                }
            }
            return result.Take(max).ToList();
        }
        public IReadOnlyList<CorpusNamed> Named(string name, TakeFilter filter) => [.. NamedPieces.Where(n => n.Name == name && filter.Allows(n.VoiceId, n.Altered))];
        public IReadOnlyDictionary<string, int> Vocabulary(TakeFilter filter)
        {
            var counts = new Dictionary<string, int>();
            foreach (var (_, voice, words, altered) in Docs) if (filter.Allows(voice, altered)) foreach (var w in words) counts[w] = counts.GetValueOrDefault(w) + 1;
            return counts;
        }
    }

    private static SentenceProject Project(int chaos = 0, int seed = 1) => new() { Id = Guid.NewGuid().ToString("N"), Settings = new() { Chaos = chaos, Seed = seed } };
    private static string Describe(SentenceProject p) => string.Join(" | ", p.Pieces.OrderBy(x => x.TokenStart).Select(x => $"{x.Kind}:{x.TokenStart}-{x.TokenEnd}:{x.Source?.RecordingId[..6]}:{x.Source?.StartMs}"));

    [Fact]
    public void TypingPrefersRecordedPhrasesAndRematchesOnlyTheChangedWords()
    {
        var corpus = new FakeCorpus();
        string a = corpus.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "alpha beta"), b = corpus.Add("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "gamma delta"), c = corpus.Add("cccccccccccccccccccccccccccccccc", "epsilon");
        corpus.Add("cccccccccccccccccccccccccccccccc", "alpha"); corpus.Add("cccccccccccccccccccccccccccccccc", "beta");
        var p = Project(chaos: 45);
        SentenceComposer.Rematch(p, "alpha beta gamma delta", corpus);
        // Phrase preference: two recorded phrases rather than four single words (even with the slider in the middle).
        Assert.Equal(2, p.Pieces.Count);
        var first = p.Pieces.Single(x => x.TokenStart == 0); var second = p.Pieces.Single(x => x.TokenStart == 2);
        Assert.Equal((a, 2), (first.Source!.RecordingId, first.TokenEnd!.Value));
        Assert.Equal((b, 4), (second.Source!.RecordingId, second.TokenEnd!.Value));
        // A person moves the second piece; the first piece's take is noted.
        second.OffsetMs = 5000; second.Manual = true;
        string firstId = first.Id, secondId = second.Id; var firstSource = Json.Write(first.Source);
        // Insert a word between them: only that word is matched; both pieces stay exactly as they were.
        SentenceComposer.Rematch(p, "Alpha, beta epsilon gamma delta!", corpus);
        Assert.Equal(3, p.Pieces.Count);
        Assert.Equal(firstSource, Json.Write(p.Pieces.Single(x => x.Id == firstId).Source));
        var moved = p.Pieces.Single(x => x.Id == secondId);
        Assert.Equal((3, 5, 5000.0, true), (moved.TokenStart!.Value, moved.TokenEnd!.Value, moved.OffsetMs, moved.Manual));
        Assert.Equal(c, p.Pieces.Single(x => x.TokenStart == 2).Source!.RecordingId);
        Assert.Equal("Alpha, beta", p.Pieces.Single(x => x.Id == firstId).Text);
        // Change the last word: the phrase it was part of goes, its other word is matched again, the new word is missing.
        SentenceComposer.Rematch(p, "Alpha, beta epsilon gamma omega", corpus);
        Assert.Contains(p.Pieces, x => x.Id == firstId);
        Assert.DoesNotContain(p.Pieces, x => x.Id == secondId);
        Assert.Equal(b, p.Pieces.Single(x => x.TokenStart == 3).Source!.RecordingId);
        var missing = p.Pieces.Single(x => x.TokenStart == 4);
        Assert.Equal(("missing", "gap"), (missing.Kind, missing.Missing));
        // Same words again (only punctuation and case): nothing changes at all.
        var before = Describe(p);
        SentenceComposer.Rematch(p, "alpha beta epsilon gamma OMEGA.", corpus);
        Assert.Equal(before, Describe(p));
    }

    [Fact]
    public void SmoothToChaoticIsDeterministicForASeedAndChaosSwitchesVoicesMore()
    {
        var corpus = new FakeCorpus();
        var voices = Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 32)).ToArray();
        foreach (var v in voices) { corpus.Add(v, "one two three four five six seven eight"); corpus.Add(v, "one two three four five six seven eight"); }
        const string text = "one two three four five six seven eight";
        int Switches(SentenceProject p) { var order = p.Pieces.OrderBy(x => x.TokenStart).Select(x => x.Source!.VoiceId).ToList(); return order.Zip(order.Skip(1)).Count(z => z.First != z.Second); }
        // Smooth: one phrase, whatever the seed.
        foreach (int seed in new[] { 1, 2, 3 }) { var p = Project(0, seed); SentenceComposer.Rematch(p, text, corpus); Assert.Single(p.Pieces); }
        // The same seed always gives the same choice.
        for (int chaos = 0; chaos <= 100; chaos += 25)
        {
            var x = Project(chaos, 42); var y = Project(chaos, 42);
            SentenceComposer.Rematch(x, text, corpus); SentenceComposer.Rematch(y, text, corpus);
            Assert.Equal(Describe(x), Describe(y));
        }
        // Chaotic switches voices (and splits phrases), and different seeds choose differently.
        var results = Enumerable.Range(1, 12).Select(seed => { var p = Project(100, seed); SentenceComposer.Rematch(p, text, corpus); return p; }).ToList();
        Assert.True(results.Average(Switches) >= 2, "chaotic should switch voices several times");
        Assert.True(results.Select(Describe).Distinct().Count() >= 3, "different seeds should choose differently");
        // In the middle, phrases still win: fewer pieces than chaotic.
        var middle = Enumerable.Range(1, 12).Select(seed => { var p = Project(45, seed); SentenceComposer.Rematch(p, text, corpus); return p.Pieces.Count; }).Average();
        Assert.True(middle < results.Average(p => p.Pieces.Count));
        // Shuffle is repeatable too: the same seed and shuffle count give the same voices.
        var s1 = Project(45, 9); var s2 = Project(45, 9);
        SentenceComposer.Rematch(s1, text, corpus); SentenceComposer.Rematch(s2, text, corpus);
        SentenceComposer.Shuffle(s1, null, corpus); SentenceComposer.Shuffle(s2, null, corpus);
        Assert.Equal(Describe(s1), Describe(s2));
    }

    [Fact]
    public void MissingWordsAreMarkedNeverSubstitutedAndSplicesNeedApproval()
    {
        var corpus = new FakeCorpus();
        string voice = new('a', 32);
        corpus.Add(voice, "my cats like sun"); corpus.Add(voice, "light the way");
        var p = Project(chaos: 30);
        SentenceComposer.Rematch(p, "my cat likes sunlight", corpus);
        // "cat", "likes" and "sunlight" were never recorded: each is missing, nothing similar is used for it.
        var missing = p.Pieces.Where(x => x.Kind == "missing").OrderBy(x => x.TokenStart).ToList();
        Assert.Equal(["cat", "likes", "sunlight"], missing.Select(x => x.Text));
        Assert.All(missing, x => Assert.Null(x.Source));
        Assert.Equal(voice, p.Pieces.Single(x => x.TokenStart == 0).Source!.VoiceId);
        // Similar recorded words are offered for a deliberate choice.
        var similar = SentenceComposer.Similar("cat", corpus, TakeFilter.Of(p.Settings));
        Assert.Contains(similar, x => x.Word == "cats");
        // Splices from actual recordings are offered, and nothing changes until one is approved.
        var splices = SentenceComposer.Splices("sunlight", corpus, TakeFilter.Of(p.Settings));
        var splice = Assert.Single(splices, x => x.Description.Contains("“sun” from “sun”") && x.Description.Contains("“light” from “light”"));
        Assert.Equal(2, splice.Parts.Count);
        var sunlight = missing[2];
        Assert.Equal("missing", sunlight.Kind);
        // Find missing again (on request) finds nothing new and changes nothing.
        var before = Describe(p);
        Assert.Equal(0, SentenceComposer.FindMissingAgain(p, corpus));
        Assert.Equal(before, Describe(p));
        // New recordings never change the sentence by themselves; Find missing again does, when asked.
        corpus.Add(voice, "likes");
        Assert.Equal(before, Describe(p));
        Assert.Equal(1, SentenceComposer.FindMissingAgain(p, corpus));
        Assert.Equal("word", p.Pieces.Single(x => x.TokenStart == 2).Kind);
        // Deliberate choices: a similar word (marked as a substitute) and an approved splice.
        var cat = p.Pieces.Single(x => x.TokenStart == 1); sunlight = p.Pieces.Single(x => x.TokenStart == 3);
        Assert.Equal(missing[0].Id, cat.Id);
        SentenceComposer.UseSimilar(p, cat, "cats", corpus);
        Assert.Equal(("word", "cats"), (cat.Kind, cat.Substitute));
        SentenceComposer.UseSplice(p, sunlight, splice);
        Assert.Equal("splice", sunlight.Kind); Assert.Equal(2, sunlight.Parts!.Count);
        // Either can go back to a plain missing word, as a Gap or a Bleep.
        SentenceComposer.MakeMissing(p, sunlight, "bleep");
        Assert.Equal(("missing", "bleep"), (sunlight.Kind, sunlight.Missing));
    }

    // ---- Rendering ---------------------------------------------------------------------------------------------------

    private static PcmSliceSource Constant(float value, int ms, int rate = 48000) => new(Enumerable.Repeat(value, rate * ms / 1000).ToArray(), rate, 1);

    [Fact]
    public void GapsAndBleepsRenderTheirLengthsAndAMissingWordIsNeverSpeech()
    {
        var settings = new SentenceSettings { Balance = false };
        var pieces = new List<SentencePiece>
        {
            new() { Id = "000000000001", Kind = "missing", Missing = "gap", DurationMs = 400, OffsetMs = 0, Text = "cat" },
            new() { Id = "000000000002", Kind = "missing", Missing = "bleep", DurationMs = 300, OffsetMs = 400, Text = "dog" },
        };
        var result = SentenceRender.Render(pieces, settings, _ => throw new InvalidOperationException("nothing may be read"));
        Assert.Equal(48000, result.Audio.Rate);
        Assert.Equal(48000 * 700 / 1000, result.Audio.Frames);
        Assert.All(result.Audio.Samples[..(48000 * 400 / 1000)], s => Assert.Equal(0f, s));
        var bleep = result.Audio.Samples[(48000 * 400 / 1000)..];
        Assert.True(Math.Sqrt(bleep.Average(s => (double)s * s)) > 0.1);
        Assert.Empty(result.Parts);
        Assert.Equal([typeof(SlicePause), typeof(SliceBleep)], result.Items.Select(i => i.GetType()));
    }

    [Fact]
    public void ALaterStartingPieceCutsOffTheEarlierOneInTheRender()
    {
        var sources = new Dictionary<string, ISliceSource> { [new string('1', 32)] = Constant(0.5f, 1000), [new string('2', 32)] = Constant(-0.25f, 500) };
        var pieces = new List<SentencePiece>
        {
            new() { Id = "00000000000a", Kind = "word", Text = "long", OffsetMs = 0, FadeInMs = 0, FadeOutMs = 0, Source = new() { RecordingId = new string('1', 32), StartMs = 0, EndMs = 1000 } },
            new() { Id = "00000000000b", Kind = "word", Text = "short", OffsetMs = 600, FadeInMs = 0, FadeOutMs = 0, Source = new() { RecordingId = new string('2', 32), StartMs = 0, EndMs = 500 } },
        };
        var audio = SentenceRender.Render(pieces, new SentenceSettings { Balance = false }, id => sources[id]).Audio;
        Assert.Equal(48000 * 1100 / 1000, audio.Frames);
        Assert.Equal(0.5f, audio.Samples[48000 * 300 / 1000]);
        // After the short crossfade only the later piece sounds: never a mix of both.
        Assert.Equal(-0.25f, audio.Samples[48000 * 650 / 1000]);
        Assert.Equal(-0.25f, audio.Samples[48000 * 1000 / 1000]);
    }

    [Fact]
    public void TheRendererOnlyReadsLibraryRecordingsAndMakesOnlyPausesAndBleeps()
    {
        var library = new Dictionary<string, ISliceSource> { [new string('1', 32)] = Constant(0.3f, 2000), [new string('2', 32)] = Constant(0.2f, 2000, 16000) };
        var asked = new List<string>();
        ISliceSource Resolve(string id) { asked.Add(id); return library[id]; }
        var pieces = new List<SentencePiece>
        {
            new() { Id = "000000000001", Kind = "word", Text = "a", OffsetMs = 0, Source = new() { RecordingId = new string('1', 32), StartMs = 0, EndMs = 300 } },
            new() { Id = "000000000002", Kind = "named", Text = "[laugh]", OffsetMs = 400, Source = new() { RecordingId = new string('2', 32), StartMs = 100, EndMs = 600 }, Effect = new() { Speed = 1.5, KeepPitch = true } },
            new() { Id = "000000000003", Kind = "splice", Text = "b", OffsetMs = 1000, Parts = [new() { RecordingId = new string('1', 32), StartMs = 0, EndMs = 100 }, new() { RecordingId = new string('2', 32), StartMs = 0, EndMs = 100 }], Effect = new() { Echo = true, Reverse = true } },
            new() { Id = "000000000004", Kind = "pause", Text = "", OffsetMs = 1400, DurationMs = 200 },
            new() { Id = "000000000005", Kind = "missing", Missing = "bleep", Text = "c", OffsetMs = 1700, DurationMs = 300 },
        };
        foreach (bool cleanup in new[] { false, true })
        {
            asked.Clear();
            var result = SentenceRender.Render(pieces, new SentenceSettings { Effect = new() { Speed = 0.9 } }, Resolve, cleanup);
            Assert.All(asked, id => Assert.True(library.ContainsKey(id)));
            Assert.All(result.Items, item => Assert.True(item is SlicePiece or SlicePause or SliceBleep, item.GetType().Name));
            // Every audio piece came from a library range the resolver handed out (processed copies of it at most).
            Assert.Equal(result.Items.Count(i => i is SlicePiece), result.Parts.Count);
            Assert.All(result.Parts, part => Assert.Contains(part.RecordingId, library.Keys));
        }
        // No speech engine of any kind is part of the companion.
        var assembly = typeof(Bridge).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.Contains("Speech", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("Tts", StringComparison.Ordinal));
        Assert.DoesNotContain(assembly.GetTypes(), t => t.Name.Contains("Synthes", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("TextToSpeech", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Clone", StringComparison.Ordinal) && t.Name.Contains("Voice", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSentencesFileIsValidatedStrictlyAndKeepsABackup()
    {
        var store = new SentenceStore(folder);
        store.Change(f => { f.Sentences.Add(new SentenceProject { Id = new string('a', 32), Text = "hello" }); return 0; });
        store.Change(f => { f.Sentences[0].Title = "Hi"; return 0; });
        Assert.True(File.Exists(Path.Combine(folder, "library", "sentences.v1.bak.json")));
        // A refused change leaves nothing half done.
        Assert.Throws<InvalidDataException>(() => store.Change(f => { f.Sentences[0].Pieces.Add(new SentencePiece { Id = "bad", Kind = "word" }); return 0; }));
        Assert.Empty(store.Read(f => f.Sentences[0].Pieces));
        // A damaged main file falls back to the backup; both damaged: blocked and untouched.
        File.WriteAllText(Path.Combine(folder, "library", "sentences.v1.json"), "{ \"schemaVersion\": 9 }");
        var again = new SentenceStore(folder);
        Assert.False(again.Blocked); Assert.NotNull(again.Problem);
        Assert.Equal("hello", again.Read(f => f.Sentences.Single().Text));
        File.WriteAllText(Path.Combine(folder, "library", "sentences.v1.bak.json"), "nonsense");
        var blocked = new SentenceStore(folder);
        Assert.True(blocked.Blocked);
        Assert.Throws<InvalidDataException>(() => blocked.Change(f => 0));
        Assert.Equal("{ \"schemaVersion\": 9 }", File.ReadAllText(Path.Combine(folder, "library", "sentences.v1.json")));
    }

    // ---- Through the Bridge -----------------------------------------------------------------------------------------

    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static JsonElement Body(Reply reply) => JsonSerializer.SerializeToElement(reply.Data, SoundpadWeb.Json.Options);
    private static async Task<Reply> Ok(Task<Reply> reply) { var r = await reply; Assert.True(r.Ok, r.Error); return r; }
    private static readonly Participant Alice = new("11111111-1111-1111-1111-111111111111", "Alice");
    private static readonly Participant Bob = new("22222222-2222-2222-2222-222222222222", "Bob");

    private sealed class Rig : IAsyncDisposable
    {
        public readonly MockSoundpad Mock;
        public readonly SoundpadPipe Pipe;
        public readonly Bridge Bridge;
        public readonly Dictionary<string, short[]> Audio = [];
        public readonly Dictionary<string, string> Paths = [];
        public readonly List<string> Recordings = [];
        public string VoiceA = "", VoiceB = "";
        private readonly string folder;
        public Rig(string folder)
        {
            this.folder = folder;
            var board = Path.Combine(folder, "board.wav"); File.WriteAllBytes(board, SliceWav.Encode(new float[4800], 48000, 1));
            var all = new XElement("Soundlist", new XElement("Sound", new XAttribute("index", 1), new XAttribute("url", board), new XAttribute("title", "Board")));
            var cats = new XElement("Categories", new XElement("Category", new XAttribute("index", 2), new XAttribute("name", "Fixtures"), new XElement("Sound", new XAttribute("index", 1), new XAttribute("url", board))));
            Mock = new MockSoundpad { AllXml = all.ToString(), CategoryXml = cats.ToString(), FollowFileLength = true };
            Pipe = new SoundpadPipe(Mock.Name);
            Bridge = new Bridge(Pipe, new StateStore(folder));
            Bridge.UseMimicCapture(new MimicFixture()); Bridge.UseTranscriptEngine(new TranscriptFixture()); Bridge.UseHotkey(new HotkeyFixture());
            Bridge.WorkerPauseMs = 0; Bridge.BackgroundWorker = false;
        }
        /// <summary>A recording of noise (every sample differs) whose words each last 300 ms with 100 ms between.</summary>
        public string Add(string text, string voice, int seed)
        {
            var words = text.Split(' ');
            int ms = words.Length * 400 + 200;
            var random = new Random(seed);
            var samples = Enumerable.Range(0, 48000 * ms / 1000).Select(_ => (short)random.Next(-12000, 12000)).ToArray();
            var path = Path.Combine(folder, $"rec{seed}.wav");
            File.WriteAllBytes(path, SliceWav.Encode(samples.Select(s => s / 32768f).ToArray(), 48000, 1));
            var list = words.Select((w, i) => new LibraryWord(i * 400, i * 400 + 300, w, 0.9f, false)).ToList();
            var (r, _) = Bridge.VoiceLibraryStore.Add(new LibraryRecording { Path = path, Title = $"Rec {seed}", DurationMs = ms, Source = new LibrarySource { Kind = "upload" }, VoiceId = voice }, list, "host", "Host");
            Audio[r.Id] = samples; Paths[r.Id] = path; Recordings.Add(r.Id);
            return r.Id;
        }
        public async Task Start()
        {
            await Bridge.Poll(true);
            VoiceA = Bridge.VoiceLibraryStore.CreateVoice("Ann", null, "host", "Host").Voice.Id;
            VoiceB = Bridge.VoiceLibraryStore.CreateVoice("Ben", null, "host", "Host").Voice.Id;
            Add("fixture tone one", VoiceA, 1);
            Add("fixture tone two", VoiceB, 2);
            Add("hello sun light", VoiceB, 3);
            await Bridge.Execute(Cmd("beginSession"), "host");
        }
        public ISliceSource Source(string id) => new PcmSliceSource(Audio[id].Select(s => s / 32768f).ToArray(), 48000, 1);
        public Task<Reply> Host(Command c) => Bridge.Execute(c, "host");
        public Task<Reply> As(Participant who, Command c) => Bridge.Execute(c, "guest", who, "peer-" + who.Name);
        public SentenceProject Stored(string id) => Bridge.SentenceStoreForTests.Read(f => SentenceStore.Copy(f.Sentences.Single(s => s.Id == id)));
        public MockSoundpad.SlotPlay[] SlotPlays() { lock (Mock.SlotPlays) return [.. Mock.SlotPlays]; }
        public async ValueTask DisposeAsync() { Bridge.Dispose(); Pipe.Dispose(); await Mock.DisposeAsync(); }
    }

    private static async Task<string> Create(Rig rig, string text, Participant? who = null, int chaos = 0)
    {
        var c = Cmd("sentenceCreate") with { Sentence = new SentenceInput(Text: text, Chaos: chaos) };
        var reply = await Ok(who == null ? rig.Host(c) : rig.As(who, c));
        return Body(reply).GetProperty("id").GetString()!;
    }
    private static Command Edit(string id, SentenceInput input) => Cmd("sentenceEdit") with { SentenceId = id, Sentence = input };

    [Fact]
    public async Task PlayIntoGameRendersTheRevisionAskedForIntoASlotThroughArbitration()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "fixture tone zebra");
        var project = rig.Stored(id);
        // Typing matched without playing anything: no slot was written and Soundpad was not asked to play.
        Assert.Empty(rig.SlotPlays()); lock (rig.Mock.Commands) Assert.DoesNotContain(rig.Mock.Commands, x => x.StartsWith("DoPlaySound"));
        Assert.Equal(["word", "missing"], project.Pieces.OrderBy(p => p.TokenStart).Select(p => p.Kind));
        var zebra = project.Pieces.Single(p => p.Kind == "missing");
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "missing", Pieces: [zebra.Id], Mode: "bleep"))));
        var asked = rig.Stored(id);
        // Play, and edit the text while it is being prepared: the play is of the revision it was asked at.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Bridge.SentenceRenderHold = async () => { held.TrySetResult(); await hold.Task; };
        var playing = rig.Host(Cmd("sentencePlay") with { SentenceId = id });
        await held.Task;
        rig.Bridge.SentenceRenderHold = null;
        await Ok(rig.Host(Cmd("sentenceText") with { SentenceId = id, Sentence = new SentenceInput(Text: "fixture tone hello") }));
        hold.SetResult();
        var played = await Ok(playing);
        Assert.Equal(asked.Revision, Body(played).GetProperty("detail").GetProperty("revision").GetInt64());
        Assert.True(Body(played).GetProperty("slot").GetBoolean());
        Assert.Equal(asked.Revision + 1, rig.Stored(id).Revision);
        var slot = Assert.Single(rig.SlotPlays());
        var expected = SentenceRender.Render(asked.Pieces, asked.Settings, rig.Source).Audio.ToWav();
        Assert.Equal(SliceWav.Pcm16(expected), SliceWav.Pcm16(slot.Bytes));
        lock (rig.Mock.Commands) Assert.DoesNotContain(rig.Mock.Commands, x => x.StartsWith("DoSeekMs"));
        Assert.Equal("sentence:" + id, rig.Bridge.Snapshot.Playback.SoundId);
        // The next Play is the edited revision.
        await Ok(rig.Host(Cmd("stop")));
        await Ok(rig.Host(Cmd("sentencePlay") with { SentenceId = id }));
        var now = rig.Stored(id);
        Assert.Equal(SliceWav.Pcm16(SentenceRender.Render(now.Pieces, now.Settings, rig.Source).Audio.ToWav()), SliceWav.Pcm16(rig.SlotPlays()[^1].Bytes));
        // Guests: Panic holds their plays (quietly superseded); without remote playback they are refused.
        await Ok(rig.Host(Cmd("panic") with { Action = "enable" }));
        var suspended = await rig.As(Alice, Cmd("paletteTap") with { Action = "game", Sentence = new SentenceInput(Item: new PaletteRef { RecordingId = rig.Recordings[0], StartMs = 0, EndMs = 300, Text = "fixture" }) });
        Assert.True(suspended.Ok); Assert.Equal("suspended", Body(suspended).GetProperty("reason").GetString());
        await Ok(rig.Host(Cmd("panic") with { Action = "disable" }));
        await Ok(rig.Host(Cmd("permissions") with { Permissions = new(RemotePlayback: false) }));
        var refused = await rig.As(Alice, Cmd("paletteTap") with { Action = "game", Sentence = new SentenceInput(Item: new PaletteRef { RecordingId = rig.Recordings[0], StartMs = 0, EndMs = 300, Text = "fixture" }) });
        Assert.False(refused.Ok);
    }

    [Fact]
    public async Task TimelineEditsDeleteLeavesAPauseOthersStayAndSplicesNeedApproval()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone sunlight");
        var p = rig.Stored(id);
        var hello = p.Pieces.Single(x => x.TokenStart == 0); var phrase = p.Pieces.Single(x => x.TokenStart == 1);
        var sunlight = p.Pieces.Single(x => x.TokenStart == 3);
        Assert.Equal("missing", sunlight.Kind);
        // Free movement: moving one piece moves nothing else.
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "move", Pieces: [hello.Id], OffsetMs: 900))));
        var moved = rig.Stored(id);
        Assert.Equal(900, moved.Pieces.Single(x => x.Id == hello.Id).OffsetMs);
        Assert.Equal(phrase.OffsetMs, moved.Pieces.Single(x => x.Id == phrase.Id).OffsetMs);
        // The overlap is reported (the later-starting piece will cut off the earlier one).
        var view = Body(await Ok(rig.Host(Cmd("sentenceGet") with { SentenceId = id })));
        Assert.Contains(view.GetProperty("overlaps").EnumerateArray(), o => o.GetProperty("first").GetString() == phrase.Id && o.GetProperty("second").GetString() == hello.Id);
        // Delete leaves an editable pause of the same length, in the same place; the rest stays put.
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "delete", Pieces: [phrase.Id]))));
        var deleted = rig.Stored(id);
        var pause = deleted.Pieces.Single(x => x.Id == phrase.Id);
        Assert.Equal(("pause", phrase.OffsetMs, SentenceComposer.Duration(phrase)), (pause.Kind, pause.OffsetMs, pause.DurationMs));
        Assert.Equal(900, deleted.Pieces.Single(x => x.Id == hello.Id).OffsetMs);
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "pause", Pieces: [pause.Id], DurationMs: 250))));
        Assert.Equal(250, rig.Stored(id).Pieces.Single(x => x.Id == pause.Id).DurationMs);
        // Splices: offered from actual recordings, the missing word untouched until one is approved.
        var alternatives = Body(await Ok(rig.Host(Cmd("sentenceAlternatives") with { SentenceId = id, Sentence = new SentenceInput(Pieces: [sunlight.Id]) })));
        var splices = alternatives.GetProperty("splices").EnumerateArray().ToList();
        Assert.NotEmpty(splices);
        Assert.Equal("missing", rig.Stored(id).Pieces.Single(x => x.Id == sunlight.Id).Kind);
        Assert.False((await rig.Host(Edit(id, new SentenceInput(Do: "resolve", Pieces: [sunlight.Id], Splice: 99)))).Ok);
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "resolve", Pieces: [sunlight.Id], Splice: 0))));
        var spliced = rig.Stored(id).Pieces.Single(x => x.Id == sunlight.Id);
        Assert.Equal("splice", spliced.Kind);
        Assert.All(spliced.Parts!, part => Assert.Contains(part.RecordingId, rig.Recordings));
        // A word nobody recorded and nothing similar to: no splice or similar word is invented.
        var other = await Create(rig, "xylophone");
        var x = rig.Stored(other).Pieces.Single();
        var none = Body(await Ok(rig.Host(Cmd("sentenceAlternatives") with { SentenceId = other, Sentence = new SentenceInput(Pieces: [x.Id]) })));
        Assert.Equal(0, none.GetProperty("splices").GetArrayLength());
        Assert.False((await rig.Host(Edit(other, new SentenceInput(Do: "resolve", Pieces: [x.Id], Word: "hello")))).Ok);
        // Effects on the selection, or on the whole sentence when nothing is selected.
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "effects", Pieces: [hello.Id], Effect: new PieceEffect { Reverse = true }))));
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "effects", Effect: new PieceEffect { Echo = true }))));
        var effects = rig.Stored(id);
        Assert.True(effects.Pieces.Single(y => y.Id == hello.Id).Effect!.Reverse);
        Assert.True(effects.Settings.Effect!.Echo);
        // Swap goes through the other takes of the same words.
        var twoTakes = await Create(rig, "fixture tone");
        var piece = rig.Stored(twoTakes).Pieces.Single();
        await Ok(rig.Host(Edit(twoTakes, new SentenceInput(Do: "swap", Pieces: [piece.Id]))));
        var swapped = rig.Stored(twoTakes).Pieces.Single();
        Assert.NotEqual(piece.Source!.RecordingId, swapped.Source!.RecordingId);
        Assert.True(swapped.Chosen);
    }

    [Fact]
    public async Task KeptVersionsPinTheirSourcesAndStayPlayableWhenSourcesChange()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone");
        Assert.False((await rig.Host(Edit(id, new SentenceInput(Do: "pin", Pinned: true)))).Ok); // keep first
        var kept = Body(await Ok(rig.Host(Cmd("sentenceKeep") with { SentenceId = id })));
        Assert.Equal(1, kept.GetProperty("version").GetInt32());
        var v1 = rig.Stored(id).Versions.Single();
        // Pinned: each source recording's id, its file's hash and the frames used.
        Assert.NotEmpty(v1.Sources);
        foreach (var pin in v1.Sources)
        {
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rig.Paths[pin.RecordingId]))).ToLowerInvariant(), pin.Hash);
            Assert.True(pin.EndFrame > pin.StartFrame); Assert.Equal(48000, pin.Rate);
        }
        var v1Audio = File.ReadAllBytes(Path.Combine(folder, "library", "sentences", id, v1.File));
        // Edit and keep again: a new version, the old one recoverable.
        await Ok(rig.Host(Cmd("sentenceText") with { SentenceId = id, Sentence = new SentenceInput(Text: "hello fixture tone two") }));
        await Ok(rig.Host(Cmd("sentenceKeep") with { SentenceId = id }));
        Assert.Equal([1, 2], rig.Stored(id).Versions.Select(v => v.N));
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "pin", Pinned: true))));
        Assert.True(rig.Stored(id).Pinned);
        // Editing a source's metadata changes nothing about the kept versions.
        await Ok(rig.Host(Cmd("libraryEdit") with { RecordingId = v1.Sources[0].RecordingId, Title = "Renamed", CorrectedText = "something else entirely" }));
        var view = Body(await Ok(rig.Host(Cmd("sentenceGet") with { SentenceId = id })));
        Assert.All(view.GetProperty("versions").EnumerateArray(), v => Assert.Equal(0, v.GetProperty("affected").GetArrayLength()));
        // A source file changes or goes missing: the affected pieces are named; version 1 still plays its kept audio.
        var changed = v1.Sources[0];
        File.WriteAllBytes(rig.Paths[changed.RecordingId], SliceWav.Encode(new float[48000], 48000, 1));
        view = Body(await Ok(rig.Host(Cmd("sentenceGet") with { SentenceId = id })));
        var first = view.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("n").GetInt32() == 1);
        Assert.Contains(first.GetProperty("affected").EnumerateArray(), a => a.GetProperty("pieceId").GetString() == changed.PieceId && a.GetProperty("state").GetString() == "changed");
        await Ok(rig.Host(Cmd("sentencePlay") with { SentenceId = id, Sentence = new SentenceInput(Version: 1) }));
        Assert.Equal(SliceWav.Pcm16(v1Audio), SliceWav.Pcm16(rig.SlotPlays()[^1].Bytes));
        // Restore brings version 1's text and pieces back as the current revision.
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "restore", Version: 1))));
        Assert.Equal("hello fixture tone", rig.Stored(id).Text);
        // Guests see kept sentences (and play them) but cannot change someone else's.
        var list = Body(await Ok(rig.As(Alice, Cmd("sentenceList"))));
        var entry = list.GetProperty("sentences").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        Assert.False(entry.GetProperty("canEdit").GetBoolean()); Assert.True(entry.GetProperty("pinned").GetBoolean());
        Assert.False((await rig.As(Alice, Cmd("sentenceText") with { SentenceId = id, Sentence = new SentenceInput(Text: "nope") })).Ok);
        // A guest's unkept project is theirs alone.
        var mine = await Create(rig, "hello", Alice);
        Assert.DoesNotContain(Body(await Ok(rig.As(Bob, Cmd("sentenceList")))).GetProperty("sentences").EnumerateArray(), s => s.GetProperty("id").GetString() == mine);
        Assert.False((await rig.As(Bob, Cmd("sentenceGet") with { SentenceId = mine })).Ok);
        // Keep budgets apply to a guest's kept sentences: a 15 s sentence (about 1.4 MB) does not fit in 1 MB.
        await Ok(rig.Host(Cmd("keepSettings") with { KeptMb = 1 }));
        var pause = Body(await Ok(rig.As(Alice, Edit(mine, new SentenceInput(Do: "insertPause", OffsetMs: 500, DurationMs: 15000))))).GetProperty("result").GetProperty("pieceId").GetString();
        Assert.NotNull(pause);
        var over = await rig.As(Alice, Cmd("sentenceKeep") with { SentenceId = mine });
        Assert.False(over.Ok); Assert.Contains(GuestBudgets.Waiting, over.Error);
        Assert.Empty(rig.Stored(mine).Versions);
        Assert.Contains(rig.Bridge.Budgets.Requests(), r => r.GuestId == Alice.Id && r.Kind == "kept");
        // Without the long pause it fits, and it counts against the budget.
        await Ok(rig.As(Alice, Edit(mine, new SentenceInput(Do: "delete", Pieces: [pause!]))));
        await Ok(rig.Host(Cmd("budgetGrant") with { GuestId = Alice.Id, Action = "deny" }));
        Assert.True((await rig.As(Alice, Cmd("sentenceKeep") with { SentenceId = mine })).Ok);
        Assert.Single(rig.Stored(mine).Versions);
        // state.json and the managed-audio journal know nothing of sentences.
        Assert.DoesNotContain("sentence", File.ReadAllText(Path.Combine(folder, "state.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARecordedPerformanceHoldsOnlyItsRecordersOwnTaps()
    {
        await using var rig = new Rig(folder); await rig.Start();
        PaletteRef Item(int recording, int word, string text) => new() { RecordingId = rig.Recordings[recording], StartMs = word * 400, EndMs = word * 400 + 300, Text = text };
        Command Tap(PaletteRef item) => Cmd("paletteTap") with { Action = "preview", Sentence = new SentenceInput(Item: item) };
        // A named nonverbal piece on a recording's range.
        var named = Body(await Ok(rig.As(Alice, Cmd("paletteMark") with { RecordingId = rig.Recordings[2], Name = "[Laugh]", Sentence = new SentenceInput(StartMs: 0, EndMs: 300) })));
        Assert.Equal("laugh", named.GetProperty("name").GetString());
        var laugh = new PaletteRef { NamedId = named.GetProperty("namedId").GetString(), Text = "[laugh]" };
        var search = Body(await Ok(rig.As(Alice, Cmd("paletteSearch") with { Query = "laugh" })));
        Assert.Contains(search.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() == "named");
        await Ok(rig.As(Alice, Cmd("performance") with { Action = "start" }));
        await Ok(rig.As(Alice, Tap(Item(0, 0, "fixture"))));
        await Ok(rig.As(Bob, Tap(Item(1, 1, "tone"))));        // someone else's tap
        await Ok(rig.Host(Tap(Item(2, 0, "hello"))));         // and the host's
        await Task.Delay(30);
        await Ok(rig.As(Alice, Tap(laugh)));
        Assert.False((await rig.As(Bob, Cmd("performance") with { Action = "stop" })).Ok); // Bob was not recording
        var take = Body(await Ok(rig.As(Alice, Cmd("performance") with { Action = "stop" })));
        Assert.Equal(2, take.GetProperty("taps").GetInt32());
        var project = rig.Stored(take.GetProperty("sentenceId").GetString()!);
        Assert.Equal("performance", project.Origin);
        Assert.Equal("fixture [laugh]", project.Text);
        Assert.Equal(["word", "named"], project.Pieces.OrderBy(p => p.OffsetMs).Select(p => p.Kind));
        Assert.Equal(0, project.Pieces[0].OffsetMs);
        Assert.True(project.Pieces[1].OffsetMs >= 30);
        Assert.DoesNotContain(project.Pieces, p => p.Source!.RecordingId == rig.Recordings[1]);
        // The take is editable like any sentence; its words map to its text.
        var view = Body(await Ok(rig.As(Alice, Cmd("sentenceGet") with { SentenceId = project.Id })));
        Assert.True(view.GetProperty("canEdit").GetBoolean());
        // Performance sets keep references only, and each person picks their own active set.
        var sets = Body(await Ok(rig.As(Alice, Cmd("performanceSets") with { Action = "create", Set = new PerformanceSetInput(Name: "Spooky", VoiceIds: [rig.VoiceB], Favorites: [laugh], Templates: [new SentenceTemplate { Name = "Greeting", Text = "hello {name}" }]) })));
        Assert.NotEqual("default", sets.GetProperty("active").GetString());
        Assert.Equal("default", Body(await Ok(rig.As(Bob, Cmd("performanceSets")))).GetProperty("active").GetString());
        var stored = rig.Bridge.SentenceStoreForTests.Read(f => Json.Write(f.People));
        Assert.DoesNotContain("wav", stored, StringComparison.OrdinalIgnoreCase);
    }
    // ---- Review fixes (2.42) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task SentenceRendersRunOnTheAudioWorkerOneAtATimeAndANewerPlayGivesTheOlderUp()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var first = await Create(rig, "hello fixture tone");
        var second = await Create(rig, "fixture tone one");
        // The first render is held on the audio worker; the second Play arrives meanwhile.
        int running = 0, most = 0, renders = 0;
        var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        rig.Bridge.UseHeavyWorkHook(what =>
        {
            if (what != "sentence render") return;
            int now = Interlocked.Increment(ref running); most = Math.Max(most, now); Interlocked.Increment(ref renders);
            if (!entered.IsSet) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            Interlocked.Decrement(ref running);
        });
        var older = rig.Host(Cmd("sentencePlay") with { SentenceId = first });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the render did not reach the audio worker");
        var newer = rig.Host(Cmd("sentencePlay") with { SentenceId = second });
        await Task.Delay(100);
        release.Set();
        var a = await Ok(older); var b = await Ok(newer);
        Assert.Equal("newer", Body(a).GetProperty("reason").GetString());
        Assert.True(Body(b).GetProperty("played").GetBoolean());
        Assert.Equal(1, most);
        Assert.Single(rig.SlotPlays());
        Assert.Equal("sentence:" + second, rig.Bridge.Snapshot.Playback.SoundId);
    }

    [Fact]
    public async Task ASentenceLaidOutPastTheLimitIsRefusedBeforeAnythingIsRendered()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone");
        var piece = rig.Stored(id).Pieces[0];
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "move", Pieces: [piece.Id], OffsetMs: 590000))));
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "effects", Effect: new PieceEffect { Speed = 0.25, KeepPitch = true }))));
        var renders = 0;
        rig.Bridge.UseHeavyWorkHook(what => { if (what == "sentence render") Interlocked.Increment(ref renders); });
        foreach (var op in new[] { "sentencePlay", "sentencePreview", "sentenceKeep" })
        {
            var reply = await rig.Host(Cmd(op) with { SentenceId = id });
            Assert.False(reply.Ok); Assert.Contains("at most 120 seconds", reply.Error);
        }
        Assert.Equal(0, renders);
        Assert.Empty(rig.SlotPlays());
    }

    [Fact]
    public async Task SentenceCommandsLeaveTheReceiveLoopWhileTheSentencesFileIsBusy()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone");
        await Ok(rig.Host(Cmd("sentenceKeep") with { SentenceId = id }));
        // Someone holds the sentences file (a long save); Execute must hand every sentence command back at once.
        var holding = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var holder = Task.Run(() => rig.Bridge.SentenceStoreForTests.Read(f => { holding.Set(); release.Wait(TimeSpan.FromSeconds(3)); return 0; }));
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pending = new[]
        {
            rig.As(Alice, Cmd("sentenceList")),
            rig.As(Alice, Cmd("paletteMark") with { RecordingId = rig.Recordings[0], Name = "laugh", Sentence = new SentenceInput(StartMs: 0, EndMs: 300) }),
            rig.As(Alice, Cmd("performanceSets") with { Action = "create", Set = new PerformanceSetInput(Name: "Quick") }),
            rig.As(Alice, Cmd("sentencePlay") with { SentenceId = id, Sentence = new SentenceInput(Version: 1) }),
            rig.As(Alice, Cmd("sentencePreview") with { SentenceId = id, Sentence = new SentenceInput(Version: 1) }),
        };
        Assert.True(clock.ElapsedMilliseconds < 1000, $"Execute held the caller for {clock.ElapsedMilliseconds} ms");
        release.Set(); await holder;
        foreach (var reply in pending) await Ok(reply);
    }

    [Fact]
    public async Task AGuestSentenceDoesNotPlayWhenTheirPermissionChangedWhileItRendered()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone", Alice);
        async Task<Reply> PlayWhile(Command change)
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Bridge.SentenceRenderHold = async () => { held.TrySetResult(); await hold.Task; };
            var playing = rig.As(Alice, Cmd("sentencePlay") with { SentenceId = id });
            await held.Task;
            rig.Bridge.SentenceRenderHold = null;
            await Ok(rig.Host(change));
            hold.SetResult();
            return await playing;
        }
        static void NotPlayed(Reply r) => Assert.True(!r.Ok || Body(r).GetProperty("reason").GetString() == "suspended", r.Error);
        // Remote playback turned off meanwhile: nothing plays.
        var off = await PlayWhile(Cmd("permissions") with { Permissions = new(RemotePlayback: false) });
        NotPlayed(off);
        await Ok(rig.Host(Cmd("permissions") with { Permissions = new() }));
        // Seeking turned off meanwhile: a sentence rendered from words is parts of recordings.
        var seeking = await PlayWhile(Cmd("permissions") with { Permissions = new(GuestSeeking: false) });
        NotPlayed(seeking);
        await Ok(rig.Host(Cmd("permissions") with { Permissions = new() }));
        // Revoked meanwhile.
        var revoked = await PlayWhile(Cmd("revokePeer") with { OwnerId = "peer-Alice" });
        NotPlayed(revoked);
        Assert.Empty(rig.SlotPlays());
        lock (rig.Mock.Commands) Assert.DoesNotContain(rig.Mock.Commands, x => x.StartsWith("DoPlaySound"));
    }

    [Fact]
    public async Task WithGuestSeekingOffGuestsPlayOnlyKeptSentencesIntoTheGame()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var hosts = await Create(rig, "hello fixture tone");
        await Ok(rig.Host(Cmd("sentenceKeep") with { SentenceId = hosts }));
        var mine = await Create(rig, "fixture tone one", Alice);
        await Ok(rig.Host(Cmd("permissions") with { Permissions = new(GuestSeeking: false) }));
        var word = new SentenceInput(Item: new PaletteRef { RecordingId = rig.Recordings[0], StartMs = 0, EndMs = 300, Text = "fixture" });
        var tap = await rig.As(Alice, Cmd("paletteTap") with { Action = "game", Sentence = word });
        Assert.False(tap.Ok); Assert.Contains("seeking", tap.Error);
        var own = await rig.As(Alice, Cmd("sentencePlay") with { SentenceId = mine });
        Assert.False(own.Ok); Assert.Contains("seeking", own.Error);
        Assert.Empty(rig.SlotPlays());
        // A private preview and a kept version still work.
        await Ok(rig.As(Alice, Cmd("paletteTap") with { Action = "preview", Sentence = word }));
        Assert.True(Body(await Ok(rig.As(Alice, Cmd("sentencePlay") with { SentenceId = hosts, Sentence = new SentenceInput(Version: 1) }))).GetProperty("played").GetBoolean());
        // The host is not limited by it.
        await Ok(rig.Host(Cmd("paletteTap") with { Action = "game", Sentence = word }));
    }

    [Fact]
    public async Task ARegistrationFailureIsNotRetriedOnEveryPlay()
    {
        // The managed-audio journal is damaged: the slot files cannot be registered, and that is not a matter of quiet.
        Directory.CreateDirectory(Path.Combine(folder, "managed-audio"));
        File.WriteAllText(Path.Combine(folder, "managed-audio", "journal.json"), "{broken");
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone");
        await Task.Delay(400); // past the quiet moment after the last audio
        var first = await rig.Host(Cmd("sentencePlay") with { SentenceId = id });
        Assert.False(first.Ok);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var again = await rig.Host(Cmd("sentencePlay") with { SentenceId = id });
        Assert.False(again.Ok); Assert.Contains("getting ready", again.Error);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"the second Play waited {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void KeepAllowancesAreHeldWhenChecked()
    {
        var budgets = new GuestBudgets();
        var settings = new KeepSettings(KeptMb: 1);
        Assert.Null(budgets.ReserveKeep("g", "G", 600 * 1024, settings));
        // A second Keep at the same moment sees the first one.
        Assert.NotNull(budgets.ReserveKeep("g", "G", 600 * 1024, settings));
        Assert.NotNull(budgets.CheckKeep("g", "G", 600 * 1024, settings));
        budgets.ReleaseKeep("g", 600 * 1024);
        Assert.Null(budgets.CheckKeep("g", "G", 600 * 1024, settings));
    }

    [Fact]
    public async Task AGuestsKeepsAtTheSameMomentStayWithinTheirAllowance()
    {
        await using var rig = new Rig(folder); await rig.Start();
        await Ok(rig.Host(Cmd("keepSettings") with { KeptMb = 1 }));
        var ids = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var id = await Create(rig, "hello fixture tone", Alice);
            // About 0.6 MB each: one fits in 1 MB, two do not.
            await Ok(rig.As(Alice, Edit(id, new SentenceInput(Do: "insertPause", OffsetMs: 1500, DurationMs: 5000))));
            ids.Add(id);
        }
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Bridge.SentenceRenderHold = () => hold.Task;
        var first = rig.As(Alice, Cmd("sentenceKeep") with { SentenceId = ids[0] });
        var second = rig.As(Alice, Cmd("sentenceKeep") with { SentenceId = ids[1] });
        await Task.WhenAny(second, Task.Delay(2000));
        hold.SetResult();
        var replies = new[] { await first, await second };
        Assert.Single(replies, r => r.Ok);
        Assert.Equal(1, ids.Sum(id => rig.Stored(id).Versions.Count));
    }

    [Fact]
    public async Task AKeepThatCannotBeRecordedLeavesNoAudioBehind()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Bridge.SentenceRenderHold = async () => { held.TrySetResult(); await hold.Task; };
        var keeping = rig.Host(Cmd("sentenceKeep") with { SentenceId = id });
        await held.Task;
        rig.Bridge.SentenceRenderHold = null;
        await Ok(rig.Host(Cmd("sentenceDiscard") with { SentenceId = id }));
        hold.SetResult();
        Assert.False((await keeping).Ok);
        var audio = Path.Combine(folder, "library", "sentences", id);
        Assert.True(!Directory.Exists(audio) || Directory.GetFiles(audio).Length == 0);
    }

    [Fact]
    public async Task GuestsChangeSentencesAtALimitedPaceAndLeaveRoomForTheHost()
    {
        await using var rig = new Rig(folder); await rig.Start();
        rig.Bridge.UseSentenceWriteLimit(new RateWindow(2, TimeSpan.FromMinutes(1)));
        var id = await Create(rig, "hello", Alice);
        await Ok(rig.As(Alice, Cmd("sentenceText") with { SentenceId = id, Sentence = new SentenceInput(Text: "hello fixture") }));
        var third = await rig.As(Alice, Cmd("sentenceText") with { SentenceId = id, Sentence = new SentenceInput(Text: "hello tone") });
        Assert.False(third.Ok); Assert.Contains("Too many changes", third.Error);
        await Ok(rig.As(Alice, Cmd("sentenceList"))); // reading is not limited
        await Create(rig, "hello"); await Create(rig, "hello"); await Create(rig, "hello"); // nor is the host
        rig.Bridge.UseSentenceWriteLimit(new RateWindow(1000, TimeSpan.FromMinutes(1)));
        // An old guest sentence nobody kept goes; a full list still has room for the host.
        string alice = rig.Stored(id).Owner;
        long old = DateTimeOffset.UtcNow.AddDays(-15).ToUnixTimeMilliseconds();
        rig.Bridge.SentenceStoreForTests.Change(f =>
        {
            f.Sentences.Single(s => s.Id == id).Updated = old;
            while (f.Sentences.Count < SentenceStore.MaxSentences - Bridge.HostReserveSentences + 1)
                f.Sentences.Add(new SentenceProject { Id = SentenceStore.NewId(), Owner = "host", Text = "x", Versions = [new SentenceVersion { N = 1, File = "v1-00000000.wav", Hash = new string('0', 64), Bytes = 1 }] });
            return 0;
        });
        var full = await rig.As(Bob, Cmd("sentenceCreate") with { Sentence = new SentenceInput(Text: "hello") });
        Assert.False(full.Ok); Assert.Contains("full", full.Error);
        await Create(rig, "hello");
        Assert.DoesNotContain(rig.Bridge.SentenceStoreForTests.Read(f => f.Sentences.Select(s => s.Id).ToList()), x => x == id);
        Assert.NotEqual("host", alice);
    }

    [Fact]
    public void TheSentencesFileIsNeverSavedLargerThanLoadAccepts()
    {
        var store = new SentenceStore(folder) { SaveLimit = 64 * 1024 };
        store.Change(f => { f.Sentences.Add(new SentenceProject { Id = new string('a', 32), Text = "hello" }); return 0; });
        var big = string.Join(' ', Enumerable.Repeat("word", 700));
        // Each change below would pass the limit: refused, and nothing changes in memory or on disk.
        Assert.Throws<InvalidDataException>(() => store.Change(f => { for (int i = 0; i < 40; i++) f.Sentences.Add(new SentenceProject { Id = SentenceStore.NewId(), Text = big }); return 0; }));
        Assert.Single(store.Read(f => f.Sentences));
        Assert.False(new SentenceStore(folder).Blocked);
        // A guest's change may grow the file to three quarters of the room only; shrinking it is always allowed.
        store.Change(f => { for (int i = 0; i < 13; i++) f.Sentences.Add(new SentenceProject { Id = SentenceStore.NewId(), Text = big }); return 0; });
        Assert.Throws<InvalidDataException>(() => store.Change(f => { f.Sentences.Add(new SentenceProject { Id = SentenceStore.NewId(), Text = big }); return 0; }, guest: true));
        store.Change(f => { f.Sentences.RemoveAt(1); return 0; }, guest: true);
        store.Change(f => { f.Sentences.Add(new SentenceProject { Id = SentenceStore.NewId(), Text = big }); return 0; });
        Assert.Equal(14, new SentenceStore(folder).Read(f => f.Sentences.Count));
    }

    [Fact]
    public async Task EditsNotKeptYetStayWithTheirMaker()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone", Alice);
        await Ok(rig.As(Alice, Cmd("sentenceKeep") with { SentenceId = id }));
        await Ok(rig.As(Alice, Cmd("sentenceText") with { SentenceId = id, Sentence = new SentenceInput(Text: "fixture tone two") }));
        string Listed(Reply r) => Body(r).GetProperty("sentences").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id).GetProperty("text").GetString()!;
        Assert.Equal("hello fixture tone", Listed(await Ok(rig.As(Bob, Cmd("sentenceList")))));
        Assert.Equal("hello fixture tone", Body(await Ok(rig.As(Bob, Cmd("sentenceGet") with { SentenceId = id }))).GetProperty("text").GetString());
        Assert.Equal("fixture tone two", Listed(await Ok(rig.As(Alice, Cmd("sentenceList")))));
        Assert.Equal("fixture tone two", Body(await Ok(rig.As(Alice, Cmd("sentenceGet") with { SentenceId = id }))).GetProperty("text").GetString());
        // Bob's preview is of the kept version's pieces, not the draft.
        var kept = rig.Stored(id).Versions[^1];
        var heard = Convert.FromBase64String(Body(await Ok(rig.As(Bob, Cmd("sentencePreview") with { SentenceId = id }))).GetProperty("wav").GetString()!);
        var expected = SentenceRender.PreviewWav(SentenceRender.Render(kept.Pieces, kept.Settings, rig.Source).Audio, Bridge.GuestPreviewRate, Bridge.PreviewMaxMs);
        Assert.Equal(SliceWav.Pcm16(expected), SliceWav.Pcm16(heard));
    }

    [Fact]
    public async Task AGuestCanListSentencesBeforeTheVoiceLibraryHasLoaded()
    {
        await using var rig = new Rig(folder);
        var list = await rig.As(Alice, Cmd("sentenceList"));
        Assert.True(list.Ok, list.Error);
        Assert.True(Body(list).GetProperty("canBuild").GetBoolean());
    }

    [Fact]
    public async Task ALongPerformanceIsSavedAsATakeWithinTheTextLimits()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var label = string.Join(' ', Enumerable.Repeat("fixture", 12)); // 95 characters, 12 words
        await Ok(rig.Host(Cmd("performance") with { Action = "start" }));
        for (int i = 0; i < 45; i++)
            await Ok(rig.Host(Cmd("paletteTap") with { Action = "preview", Sentence = new SentenceInput(Item: new PaletteRef { RecordingId = rig.Recordings[0], StartMs = 0, EndMs = 300, Text = label }) }));
        var take = Body(await Ok(rig.Host(Cmd("performance") with { Action = "stop" })));
        Assert.Equal(45, take.GetProperty("taps").GetInt32());
        var project = rig.Stored(take.GetProperty("sentenceId").GetString()!);
        Assert.Equal(45, project.Pieces.Count);
        Assert.True(project.Text.Length <= SentenceStore.MaxText);
        Assert.All(project.Pieces, p => Assert.True(p.TokenEnd is null or <= SentenceStore.MaxTokens));
        // The recording ended with the saved take.
        Assert.False(Body(await Ok(rig.Host(Cmd("performance") with { Action = "status" }))).GetProperty("recording").GetBoolean());
    }

    // ---- 2.45 (WP3): voice slots, phrase blocks, Add to sentence, guests' unkept sentences, audio cleanup -----------

    private static readonly string VoiceA = new('a', 32), VoiceB = new('b', 32);

    [Fact]
    public void VoiceMarksSplitATemplateIntoRunsOfWordsWithTheirVoice()
    {
        var (text, runs) = SentenceComposer.VoiceMarked($"{{@{VoiceA}}} hello there, {{@{VoiceB}}}friend {{@}} [laugh] now");
        Assert.Equal("hello there, friend [laugh] now", text);
        Assert.Equal([(0, 2, (string?)VoiceA), (2, 3, VoiceB), (3, 5, null)], runs);
        // No marks: one run in the mix. A mark that is not a voice id stays text; marks next to each other merge.
        Assert.Equal([(0, 2, (string?)null)], SentenceComposer.VoiceMarked("hello there").Runs);
        Assert.Equal("{@nope} hi", SentenceComposer.VoiceMarked("{@nope} hi").Text);
        Assert.Equal([(0, 2, (string?)VoiceA)], SentenceComposer.VoiceMarked($"{{@{VoiceA}}} hello {{@{VoiceA}}} there").Runs);
        // Words are the same as typed text's: Words gives each word's place, Tokens only the words.
        var words = SentenceComposer.Words("  Hi,  — [laugh]! there").ToList();
        Assert.Equal(["hi", "[laugh]", "there"], words.Select(w => w.Token.Key));
        Assert.Equal([(2, 5), (9, 17), (18, 23)], words.Select(w => (w.Start, w.End)));
        Assert.Equal(SentenceComposer.Tokens("  Hi,  — [laugh]! there").Select(t => t.Key), words.Select(w => w.Token.Key));
    }

    [Fact]
    public void AVoiceSlotKeepsItsWordsInThatVoiceThroughTheMixSwapsShufflesAndEdits()
    {
        var corpus = new FakeCorpus();
        corpus.Add(VoiceA, "hello there friend"); corpus.Add(VoiceA, "hello there friend");
        corpus.Add(VoiceB, "hello there friend"); corpus.Add(VoiceB, "my");
        var p = Project(chaos: 60, seed: 7);
        var (text, runs) = SentenceComposer.VoiceMarked($"{{@{VoiceA}}} hello there {{@{VoiceB}}} friend");
        SentenceComposer.Compose(p, text, runs, corpus);
        Assert.Equal("hello there friend", p.Text);
        string? VoiceOf(int token) => p.Pieces.Single(x => x.TokenStart <= token && x.TokenEnd > token).Source?.VoiceId;
        Assert.Equal((VoiceA, VoiceA, VoiceB), (VoiceOf(0), VoiceOf(1), VoiceOf(2)));
        Assert.All(p.Pieces, x => Assert.NotNull(x.VoiceLock));
        // The mix (only voice B, chaotic) does not reach words a slot chose a voice for.
        p.Settings.VoiceIds = [VoiceB]; p.Settings.Chaos = 100;
        SentenceComposer.Recompose(p, corpus);
        Assert.Equal((VoiceA, VoiceA, VoiceB), (VoiceOf(0), VoiceOf(1), VoiceOf(2)));
        // Swap and Shuffle choose among that voice's takes only.
        var first = p.Pieces.First(x => x.TokenStart == 0);
        Assert.All(SentenceComposer.Alternatives(p, first, corpus), t => Assert.Equal(VoiceA, t.VoiceId));
        Assert.True(SentenceComposer.Swap(p, first, corpus));
        SentenceComposer.Shuffle(p, null, corpus);
        Assert.Equal((VoiceA, VoiceA, VoiceB), (VoiceOf(0), VoiceOf(1), VoiceOf(2)));
        // A word typed inside the slot's words stays in that voice: "my" was recorded only by B, so it is missing.
        SentenceComposer.Rematch(p, "hello my there friend", corpus);
        var my = p.Pieces.Single(x => x.TokenStart == 1 && x.TokenEnd == 2);
        Assert.Equal(("missing", VoiceA), (my.Kind, my.VoiceLock));
        // Find missing again also looks in that voice only; once A records it, it is found in A.
        Assert.Equal(0, SentenceComposer.FindMissingAgain(p, corpus));
        corpus.Add(VoiceA, "my");
        Assert.Equal(1, SentenceComposer.FindMissingAgain(p, corpus));
        Assert.Equal(VoiceA, VoiceOf(1));
        // Similar words for a missing word come from that voice too (B's "my" is not offered for "mx" in A).
        corpus.Add(VoiceB, "mine");
        SentenceComposer.Rematch(p, "hello mx there friend", corpus);
        var mx = p.Pieces.Single(x => x.TokenStart == 1 && x.TokenEnd == 2);
        Assert.DoesNotContain(SentenceComposer.Similar("mx", corpus, SentenceComposer.FilterFor(p, mx)), s => s.Word == "mine");
        // Words typed between two different voices' words are the mix's again.
        SentenceComposer.Rematch(p, "hello mx there my friend", corpus);
        Assert.Null(p.Pieces.Single(x => x.TokenStart == 3).VoiceLock);
    }

    [Fact]
    public void WordsGoInAtTheCaretNeverSplittingAPieceAndLaterPiecesMoveAlong()
    {
        var corpus = new FakeCorpus();
        corpus.Add(VoiceA, "alpha beta"); corpus.Add(VoiceA, "gamma");
        var p = Project();
        SentenceComposer.Rematch(p, "alpha beta, gamma", corpus);
        var gamma = p.Pieces.Single(x => x.TokenStart == 2);
        // Caret inside "gamma": after it, at the end.
        Assert.Equal((3, 1, 23), SentenceComposer.InsertText(p, 14, "delta"));
        Assert.Equal("alpha beta, gamma delta", p.Text);
        // Caret at the very start.
        Assert.Equal((0, 2, 10), SentenceComposer.InsertText(p, 0, "[laugh] oh"));
        Assert.Equal("[laugh] oh alpha beta, gamma delta", p.Text);
        Assert.Equal((4, 5), (gamma.TokenStart!.Value, gamma.TokenEnd!.Value));
        // Caret between "alpha" and "beta", which are one recorded phrase: after the phrase, before its comma's next word.
        var phrase = p.Pieces.Single(x => x.TokenEnd - x.TokenStart == 2 && x.Source != null);
        Assert.Equal((2, 4), (phrase.TokenStart!.Value, phrase.TokenEnd!.Value));
        Assert.Equal((4, 1, 25), SentenceComposer.InsertText(p, p.Text.IndexOf("beta", StringComparison.Ordinal) - 1, "ok"));
        Assert.Equal("[laugh] oh alpha beta, ok gamma delta", p.Text);
        Assert.Equal((5, 6), (gamma.TokenStart!.Value, gamma.TokenEnd!.Value));
        // Past the end is the end; nothing to add, or too much, is refused and changes nothing.
        Assert.Equal((7, 1, p.Text.Length + 4), SentenceComposer.InsertText(p, 9999, "end"));
        Assert.EndsWith(" end", p.Text);
        var before = p.Text;
        Assert.Throws<InvalidDataException>(() => SentenceComposer.InsertText(p, 0, " , "));
        Assert.Throws<InvalidDataException>(() => SentenceComposer.InsertText(p, 0, string.Join(' ', Enumerable.Repeat("w", SentenceStore.MaxTokens))));
        Assert.Equal(before, p.Text);
    }

    [Fact]
    public async Task AddToSentencePutsThatExactPaletteTakeAtTheCaretWithoutPlayingIt()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "fixture tone");
        var hello = new PaletteRef { RecordingId = rig.Recordings[2], StartMs = 0, EndMs = 300, Text = "hello" };
        var reply = Body(await Ok(rig.Host(Edit(id, new SentenceInput(Do: "insert", Item: hello, Caret: 0)))));
        var s = rig.Stored(id);
        Assert.Equal("hello fixture tone", s.Text);
        var piece = s.Pieces.Single(p => p.Id == reply.GetProperty("result").GetProperty("pieceId").GetString());
        Assert.Equal((0, 1, true, false), (piece.TokenStart!.Value, piece.TokenEnd!.Value, piece.Chosen, piece.Manual));
        Assert.Equal((rig.Recordings[2], 0L, 300L), (piece.Source!.RecordingId, piece.Source.StartMs, piece.Source.EndMs));
        Assert.Equal(5, reply.GetProperty("result").GetProperty("caret").GetInt32()); // just after "hello"
        // The words after it moved along, in order on the timeline.
        Assert.Equal(["hello", "fixture tone"], s.Pieces.OrderBy(p => p.OffsetMs).Select(p => p.Text));
        // At the end when no caret is given with words; a named piece goes in as [name].
        var named = Body(await Ok(rig.Host(Cmd("paletteMark") with { RecordingId = rig.Recordings[2], Name = "sigh", Sentence = new SentenceInput(StartMs: 400, EndMs: 700) })));
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "insert", Item: new PaletteRef { NamedId = named.GetProperty("namedId").GetString(), Text = "[sigh]" }, Caret: 999))));
        Assert.Equal("hello fixture tone [sigh]", rig.Stored(id).Text);
        Assert.Equal("named", rig.Stored(id).Pieces.Single(p => p.TokenStart == 3).Kind);
        // Nothing was played or written to a slot.
        Assert.Empty(rig.SlotPlays());
        lock (rig.Mock.Commands) Assert.DoesNotContain(rig.Mock.Commands, x => x.StartsWith("DoPlaySound"));
        // Without a caret an item is placed by hand at the end of the timeline, as in 2.42 (older websites).
        await Ok(rig.Host(Edit(id, new SentenceInput(Do: "insert", Item: hello))));
        Assert.Equal("hello fixture tone [sigh]", rig.Stored(id).Text);
        Assert.Single(rig.Stored(id).Pieces, p => p.TokenStart == null && p.Manual);
        // Someone else's sentence is not theirs to add to.
        Assert.False((await rig.As(Alice, Edit(id, new SentenceInput(Do: "insert", Item: hello, Caret: 0)))).Ok);
    }

    [Fact]
    public async Task PhraseBlocksAreNamedReusableAndSaidInTheirVoice()
    {
        await using var rig = new Rig(folder); await rig.Start();
        Command Sets(string action, PerformanceSetInput set) => Cmd("performanceSets") with { Action = action, Set = set };
        // Create two blocks in the default set; names are unique (any case).
        var sets = Body(await Ok(rig.As(Alice, Sets("update", new("default", Blocks: [new PhraseBlock { Name = "Greeting", Text = "hello sun", VoiceId = rig.VoiceB }, new PhraseBlock { Name = "Tone", Text = "fixture tone" }])))));
        var blocks = sets.GetProperty("sets")[0].GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal(["Greeting", "Tone"], blocks.Select(b => b.GetProperty("name").GetString()));
        var greeting = blocks[0].GetProperty("id").GetString()!;
        Assert.False((await rig.As(Alice, Sets("update", new("default", Blocks: [new PhraseBlock { Name = "x", Text = "a" }, new PhraseBlock { Name = "X", Text = "b" }])))).Ok);
        // Rename and change its words: the same block (its id stays).
        sets = Body(await Ok(rig.As(Alice, Sets("update", new("default", Blocks: [new PhraseBlock { Id = greeting, Name = "Hi", Text = "hello sun light", VoiceId = rig.VoiceB }, new PhraseBlock { Name = "Tone", Text = "fixture tone" }])))));
        Assert.Equal((greeting, "Hi", "hello sun light"), (sets.GetProperty("sets")[0].GetProperty("blocks")[0].GetProperty("id").GetString(), sets.GetProperty("sets")[0].GetProperty("blocks")[0].GetProperty("name").GetString(), sets.GetProperty("sets")[0].GetProperty("blocks")[0].GetProperty("text").GetString()));
        // Bob does not see Alice's blocks.
        Assert.False(Body(await Ok(rig.As(Bob, Cmd("performanceSets")))).GetProperty("sets")[0].TryGetProperty("blocks", out _));
        // Reuse: its words into a sentence at the caret, in its voice, twice.
        var id = await Create(rig, "fixture tone", Alice);
        await Ok(rig.As(Alice, Edit(id, new SentenceInput(Do: "insert", Text: "hello sun light", VoiceIds: [rig.VoiceB], Caret: 0))));
        await Ok(rig.As(Alice, Edit(id, new SentenceInput(Do: "insert", Text: "hello sun light", VoiceIds: [rig.VoiceB]))));
        var s = rig.Stored(id);
        Assert.Equal("hello sun light fixture tone hello sun light", s.Text);
        Assert.All(s.Pieces.Where(p => p.TokenStart is 0 or 5), p => Assert.Equal((rig.VoiceB, rig.VoiceB), (p.VoiceLock, p.Source!.VoiceId)));
        Assert.All(s.Pieces.Where(p => p.TokenStart == 3), p => Assert.Null(p.VoiceLock));
        // A block in a voice that never said the words leaves them missing (never another voice's).
        await Ok(rig.As(Alice, Edit(id, new SentenceInput(Do: "insert", Text: "hello", VoiceIds: [rig.VoiceA]))));
        Assert.Equal("missing", rig.Stored(id).Pieces.Single(p => p.TokenStart == 8).Kind);
        // Delete one: the other stays. Deleting all leaves no blocks field in the file (as before 2.45).
        sets = Body(await Ok(rig.As(Alice, Sets("update", new("default", Blocks: [new PhraseBlock { Name = "Tone", Text = "fixture tone" }])))));
        Assert.Equal(["Tone"], sets.GetProperty("sets")[0].GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("name").GetString()));
        await Ok(rig.As(Alice, Sets("update", new("default", Blocks: []))));
        Assert.DoesNotContain("\"blocks\"", rig.Bridge.SentenceStoreForTests.Read(f => Json.Write(f.People)));
        // A block survives a restart (it is in sentences.v1.json, checked strictly when loaded).
        await Ok(rig.As(Alice, Sets("update", new("default", Blocks: [new PhraseBlock { Name = "Back", Text = "fixture" }]))));
        var reloaded = new SentenceStore(folder);
        Assert.Null(reloaded.Problem);
        Assert.Equal("Back", reloaded.Read(f => f.People.Single(p => p.Owner == Alice.Id).Sets[0].Blocks!.Single().Name));
    }

    [Fact]
    public async Task ATemplatesVoiceSlotsAreSaidByTheChosenVoices()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var c = Cmd("sentenceCreate") with { Sentence = new SentenceInput(Text: $"{{@{rig.VoiceB}}} fixture tone {{@{rig.VoiceA}}} fixture tone", Origin: "template") };
        var id = Body(await Ok(rig.Host(c))).GetProperty("id").GetString()!;
        var s = rig.Stored(id);
        Assert.Equal(("fixture tone fixture tone", "template"), (s.Text, s.Origin));
        Assert.Equal([rig.VoiceB, rig.VoiceA], s.Pieces.OrderBy(p => p.TokenStart).Select(p => p.Source!.VoiceId));
        Assert.Equal([rig.VoiceB, rig.VoiceA], s.Pieces.OrderBy(p => p.TokenStart).Select(p => p.VoiceLock));
        // The view says which voice a piece is kept to.
        var view = Body(await Ok(rig.Host(Cmd("sentenceGet") with { SentenceId = id })));
        Assert.Equal(rig.VoiceB, view.GetProperty("pieces")[0].GetProperty("voiceLock").GetString());
        // Typed text is never read for marks.
        var typed = await Create(rig, $"{{@{rig.VoiceB}}} fixture");
        Assert.Contains("{@", rig.Stored(typed).Text);
    }

    [Fact]
    public async Task TheHostSeesAndClearsEachGuestsUnkeptSentencesAndGuestsNeverSeeEachOthers()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var alice1 = await Create(rig, "fixture tone", Alice); var alice2 = await Create(rig, "hello sun", Alice);
        var bob = await Create(rig, "fixture", Bob);
        var aliceKept = await Create(rig, "hello", Alice);
        await Ok(rig.Host(Cmd("keepSettings") with { GuestKeep = true }));
        await Ok(rig.As(Alice, Cmd("sentenceKeep") with { SentenceId = aliceKept }));
        var hosts = await Create(rig, "tone");
        // The host's list: their own and kept sentences as before, and each guest's unkept ones apart.
        var list = Body(await Ok(rig.Host(Cmd("sentenceList"))));
        Assert.DoesNotContain(list.GetProperty("sentences").EnumerateArray(), s => s.GetProperty("id").GetString() is var x && (x == alice1 || x == bob));
        var groups = list.GetProperty("guestUnkept").EnumerateArray().ToDictionary(g => g.GetProperty("guestId").GetString()!);
        Assert.Equal(2, groups[Alice.Id].GetProperty("count").GetInt32());
        Assert.Equal("Alice", groups[Alice.Id].GetProperty("ownerName").GetString());
        Assert.Equal(new[] { alice1, alice2 }.Order(), groups[Alice.Id].GetProperty("sentences").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).Order());
        Assert.Equal(1, groups[Bob.Id].GetProperty("count").GetInt32());
        Assert.DoesNotContain(list.GetProperty("guestUnkept").EnumerateArray(), g => g.GetProperty("sentences").EnumerateArray().Any(s => s.GetProperty("id").GetString() == aliceKept));
        // The host may open one (read-only); guests see no one else's, and get no guest list.
        var view = Body(await Ok(rig.Host(Cmd("sentenceGet") with { SentenceId = alice1 })));
        Assert.False(view.GetProperty("canEdit").GetBoolean());
        var aliceList = Body(await Ok(rig.As(Alice, Cmd("sentenceList"))));
        Assert.Equal(JsonValueKind.Null, aliceList.GetProperty("guestUnkept").ValueKind);
        Assert.DoesNotContain(aliceList.GetProperty("sentences").EnumerateArray(), s => s.GetProperty("id").GetString() is var x && (x == bob || x == hosts));
        Assert.False((await rig.As(Alice, Cmd("sentenceGet") with { SentenceId = bob })).Ok);
        // Guests cannot clear anyone's, their own included, this way.
        Assert.False((await rig.As(Alice, Cmd("sentenceDiscard") with { GuestId = Bob.Id })).Ok);
        Assert.False((await rig.As(Alice, Cmd("sentenceDiscard") with { GuestId = Alice.Id })).Ok);
        // The host clears Alice's: both unkept ones go; her kept one, Bob's and the host's stay.
        var cleared = Body(await Ok(rig.Host(Cmd("sentenceDiscard") with { GuestId = Alice.Id })));
        Assert.Equal(2, cleared.GetProperty("removed").GetInt32());
        var left = rig.Bridge.SentenceStoreForTests.Read(f => f.Sentences.Select(s => s.Id).ToHashSet());
        Assert.Equal(new HashSet<string> { aliceKept, bob, hosts }, left);
        Assert.DoesNotContain(Body(await Ok(rig.Host(Cmd("sentenceList")))).GetProperty("guestUnkept").EnumerateArray(), g => g.GetProperty("guestId").GetString() == Alice.Id);
        // One of Bob's by its id, as before.
        await Ok(rig.Host(Cmd("sentenceDiscard") with { SentenceId = bob }));
        Assert.Equal(0, Body(await Ok(rig.Host(Cmd("sentenceList")))).GetProperty("guestUnkept").GetArrayLength());
    }

    [Fact]
    public async Task RemovingAKeptSentenceFreesOnlyItsOwnUnreferencedRenderedAudio()
    {
        await using var rig = new Rig(folder); await rig.Start();
        var id = await Create(rig, "hello fixture tone");
        await Ok(rig.Host(Cmd("sentenceKeep") with { SentenceId = id }));
        await Ok(rig.Host(Cmd("sentenceText") with { SentenceId = id, Sentence = new SentenceInput(Text: "hello fixture tone two") }));
        await Ok(rig.Host(Cmd("sentenceKeep") with { SentenceId = id }));
        var other = await Create(rig, "fixture tone");
        await Ok(rig.Host(Cmd("sentenceKeep") with { SentenceId = other }));
        var audio = Path.Combine(folder, "library", "sentences", id);
        var versions = rig.Stored(id).Versions.Select(v => Path.Combine(audio, v.File)).ToList();
        Assert.Equal(2, versions.Count); Assert.All(versions, v => Assert.True(File.Exists(v)));
        var otherFile = Path.Combine(folder, "library", "sentences", other, rig.Stored(other).Versions[0].File);
        // Something that is not kept audio in the same folder is never touched, and neither are the recordings.
        var note = Path.Combine(audio, "notes.txt"); File.WriteAllText(note, "mine");
        var sources = rig.Paths.Values.ToDictionary(p => p, p => File.ReadAllBytes(p));
        var removed = Body(await Ok(rig.Host(Cmd("sentenceDiscard") with { SentenceId = id })));
        Assert.Equal(2, removed.GetProperty("freedFiles").GetInt32());
        Assert.All(versions, v => Assert.False(File.Exists(v)));
        Assert.True(File.Exists(note));
        Assert.True(File.Exists(otherFile));
        foreach (var (path, bytes) in sources) Assert.Equal(bytes, File.ReadAllBytes(path));
        // A version still in the file keeps its audio: only the file nobody refers to goes.
        var store = rig.Bridge.SentenceStoreForTests;
        var otherFolder = Path.GetDirectoryName(otherFile)!;
        var stray = Path.Combine(otherFolder, "v9-0123abcd.wav"); File.WriteAllBytes(stray, [1, 2, 3]);
        Assert.Equal((1, 3L), store.SweepAudio(other));
        Assert.True(File.Exists(otherFile)); Assert.False(File.Exists(stray));
        // An emptied folder goes too; a guest's unkept sentence has no audio and its removal frees nothing.
        File.Delete(note);
        Assert.Equal((0, 0L), store.SweepAudio(id));
        Assert.False(Directory.Exists(audio));
        // Kept clips (Keep) are elsewhere: removing a sentence never reaches the managed-audio folders.
        Assert.False(Body(await Ok(rig.Host(Cmd("sentenceDiscard") with { SentenceId = await Create(rig, "tone") }))).TryGetProperty("freedFiles", out _));
    }

    [Fact]
    public void SentencesAndSetsWithoutTheNewFieldsAreWrittenAsBefore()
    {
        // Older companions read sentences.v1.json too: nothing new appears in it unless a voice slot or a block is used.
        var project = new SentenceProject { Id = new string('c', 32), Pieces = [new SentencePiece { Id = "0123456789ab", Kind = "pause", DurationMs = 100 }] };
        Assert.DoesNotContain("voiceLock", Json.Write(project));
        Assert.DoesNotContain("blocks", Json.Write(new PerformanceSet()));
        var file = new SentenceFile { Sentences = [project], People = [new PersonSets { Owner = "host", Sets = [new PerformanceSet()] }] };
        SentenceStore.Validate(file);
        // A bad new field refuses the file, like any other.
        project.Pieces[0].VoiceLock = "not-a-voice";
        Assert.Throws<InvalidDataException>(() => SentenceStore.Validate(file));
        project.Pieces[0].VoiceLock = null;
        file.People[0].Sets[0].Blocks = [new PhraseBlock { Id = new string('d', 32), Name = "A", Text = "x" }, new PhraseBlock { Id = new string('e', 32), Name = "a", Text = "y" }];
        Assert.Throws<InvalidDataException>(() => SentenceStore.Validate(file));
    }
}
