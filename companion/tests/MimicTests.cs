using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class MimicTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-mimic-test-" + Guid.NewGuid().ToString("N"));
    public MimicTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static T Data<T>(Reply reply) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(reply.Data, Json.Options), Json.Options)!;

    [Fact]
    public void RingReadsAbsoluteFramesAndRejectsOverwrittenAudio()
    {
        var ring = new MimicRing(10, 2);
        ring.Write(Enumerable.Range(0, 15).Select(i => (float)i).ToArray());
        Assert.Equal(15, ring.Written); Assert.Equal(0, ring.Oldest);
        Assert.Equal([12f, 13f, 14f], ring.Read(12, 15));
        ring.WriteSilence(10);
        Assert.Equal(5, ring.Oldest);
        Assert.Equal([14f, 0f], ring.Read(14, 16));
        Assert.Throws<InvalidDataException>(() => ring.Read(4, 6));
        Assert.Throws<InvalidDataException>(() => ring.Read(20, 26));
        Assert.Equal([0, 0], ring.Levels(25, 4, 2));
    }

    [Fact]
    public void WavIsMono16BitAtTheCaptureRate()
    {
        using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(MimicRing.Wav([0f, 0.5f, -1f], 16000)));
        Assert.Equal((1, 16, 16000), (reader.WaveFormat.Channels, reader.WaveFormat.BitsPerSample, reader.WaveFormat.SampleRate));
        Assert.Equal(6, reader.Length);
    }

    [Fact]
    public async Task OnlyTheHostChoosesSourcesAndGuestsNeedPermission()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.False(bridge.Snapshot.Mimic!.Active);
        Assert.False((await bridge.Execute(Cmd("mimicSources"), "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicStart") with { SourceId = "output:not-listed" }, "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        Assert.True(bridge.Snapshot.Mimic!.Active); Assert.Equal(MimicFixture.Source.Name, bridge.Snapshot.Mimic.Source);
        Assert.True((await bridge.Execute(Cmd("mimicPeek"), "host")).Ok);
        var denied = await bridge.Execute(Cmd("mimicPeek"), "guest");
        Assert.False(denied.Ok); Assert.Contains("not enabled Mimic", denied.Error);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicPeek"), "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicStop"), "guest")).Ok);
        Assert.True((await bridge.Execute(Cmd("endSession"), "host")).Ok);
        Assert.False(bridge.Snapshot.Mimic!.Active);
    }

    [Fact]
    public async Task SelectionsReachingBeforeCaptureStartedAreTrimmed()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(2.2));
        var peek = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "host"));
        // "Last 5 s" after 2.2 s of listening: the first 2.8 s were never recorded.
        var clip = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 25000, EndMs = 30000 }, "host");
        Assert.True(clip.Ok, clip.Error);
        var tooEarly = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 1000, EndMs = 3000 }, "host");
        Assert.False(tooEarly.Ok); Assert.Contains("not started", tooEarly.Error);
    }
    [Fact]
    public async Task GuestClipBecomesATempSoundWithExactlyTheSelectedAudio()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true, GuestUploads: false) }, "host")).Ok);
        // Alice was admitted before capture started (2.38: guests see the history from their join time).
        bridge.NoteGuest(new("guest-1", "Alice"), "peer-1", null);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(31));
        var peek = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "guest", new("guest-1", "Alice"), "peer-1"));
        Assert.Equal(Bridge.MimicBuckets, peek.Levels.Length); Assert.Contains(peek.Levels, l => l > 400); Assert.Contains(0, peek.Levels);
        long windowStart = peek.End - MimicFixture.Rate * 30L;
        // Find the start of a tone burst inside the window and select exactly its half second.
        long burst = windowStart + (2 * MimicFixture.Rate - windowStart % (2 * MimicFixture.Rate)) % (2 * MimicFixture.Rate);
        long fromMs = (burst - windowStart) * 1000 / MimicFixture.Rate;
        var silent = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs + 600, EndMs = fromMs + 1400 }, "guest", new("guest-1", "Alice"), "peer-1");
        Assert.False(silent.Ok); Assert.Contains("silent", silent.Error);
        var clip = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs, EndMs = fromMs + 500, Name = "Said it" }, "guest", new("guest-1", "Alice"), "peer-1");
        Assert.True(clip.Ok, clip.Error);
        var sound = bridge.Snapshot.Library.Sounds.Single(s => s.Temporary);
        Assert.Equal("Alice", sound.UploadedBy); Assert.StartsWith("Said it", sound.Name);
        var recent = Assert.Single(bridge.Snapshot.Mimic!.Recent!);
        Assert.Equal(("Said it", "Alice"), (recent.Name, recent.By)); // everyone in the session sees who mimicked what
        var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        using (var wave = new NAudio.Wave.WaveFileReader(journal.Assets.Single().Variants.Single().Path))
            Assert.InRange(wave.TotalTime.TotalMilliseconds, 480, 520);
        // 2.38: the selection stays available for as long as the history keeps it (here the shortest, one minute,
        // plus up to one 10 s segment), and is then answered as gone, never cut from other audio.
        fixture.Advance(TimeSpan.FromSeconds(45));
        Assert.True((await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs, EndMs = fromMs + 500, Name = "Still there" }, "guest", new("guest-1", "Alice"), "peer-1")).Ok);
        Assert.True((await bridge.Execute(Cmd("historySettings") with { Seconds = 60 }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(40));
        var expired = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs, EndMs = fromMs + 500 }, "guest", new("guest-1", "Alice"), "peer-1");
        Assert.False(expired.Ok); Assert.Contains("no longer in the history", expired.Error);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: false) }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 0, EndMs = 500 }, "guest")).Ok);
    }

    /// <summary>Recognises speech like the fixture until it has produced a line, then fails, as a crashed Whisper would.</summary>
    private sealed class FailingEngine : ITranscriptEngine
    {
        private readonly TranscriptFixture inner = new();
        public bool Available => true;
        public string Status => "";
        public Task Prepare(CancellationToken cancel) => inner.Prepare(cancel);
        private bool recognised;
        // Fails only once a line was recognised: a chunk read mid-write may come back empty first.
        public async Task<IReadOnlyList<TranscribedSpan>> Transcribe(float[] samples16k, CancellationToken cancel)
        {
            if (recognised) throw new InvalidOperationException("engine crashed");
            var spans = await inner.Transcribe(samples16k, cancel);
            recognised = spans.Count > 0;
            return spans;
        }
        public void Dispose() => inner.Dispose();
    }

    /// <summary>A history (2.38) and its transcript in their own folder under this test's folder.</summary>
    private (HistoryStore Store, TranscriptWords Words) NewHistory(string name = "history")
    {
        var root = Path.Combine(folder, name); Directory.CreateDirectory(root);
        var store = HistoryStore.Create(root, new HistoryOptions { Rate = MimicFixture.Rate, TailSeconds = 600 });
        return (store, TranscriptWords.Open(root, store.SessionId));
    }
    private static float[] Burst(int frames) => Enumerable.Range(0, frames).Select(i => MimicFixture.Sample(i)).ToArray();

    [Fact]
    public async Task AFailedTranscriberStopsButKeepsTheWordsItRecognised()
    {
        using var transcriber = new MimicTranscriber(new FailingEngine());
        var (store, words) = NewHistory(); using var closeStore = store;
        transcriber.Attach(store, words);
        await transcriber.Enable(() => store, false);
        // Transcripts start from "now" once the loop attaches (every 0.5 s); speak after that.
        await Task.Delay(3000);
        store.Append(Burst(MimicFixture.Rate / 2));
        store.AppendSilence(MimicFixture.Rate * 3);
        for (int i = 0; i < 200 && transcriber.Lines(store, HistoryAccess.Host, 0, store.LiveFrame).Length == 0; i++) await Task.Delay(100); // up to 20 s on a slow CI runner
        Assert.Single(transcriber.Lines(store, HistoryAccess.Host, 0, store.LiveFrame));
        store.Append(Burst(MimicFixture.Rate / 2));
        store.AppendSilence(MimicFixture.Rate * 3);
        for (int i = 0; i < 200 && transcriber.Enabled; i++) await Task.Delay(100);
        Assert.False(transcriber.Enabled);
        Assert.StartsWith("Transcripts stopped", transcriber.Status);
        // 2.38: words describe the history's audio and stay with it; only recognising stops.
        Assert.Single(transcriber.Lines(store, HistoryAccess.Host, 0, store.LiveFrame));
    }

    [Fact]
    public async Task TranscriptLinesLineUpWithTheHistoryAndSilenceIsSkipped()
    {
        var engine = new TranscriptFixture();
        using var transcriber = new MimicTranscriber(engine);
        var (store, words) = NewHistory(); using var closeStore = store;
        transcriber.Attach(store, words);
        Assert.False(await transcriber.Step(store, false, default));
        // 1 s of silence, 0.5 s of tone, 2.5 s of silence: one line at 1.0-1.5 s.
        store.AppendSilence(MimicFixture.Rate);
        store.Append(Burst(MimicFixture.Rate / 2));
        store.AppendSilence(MimicFixture.Rate * 5 / 2);
        Assert.True(await transcriber.Step(store, false, default));
        var line = Assert.Single(transcriber.Lines(store, HistoryAccess.Host, 0, store.LiveFrame));
        Assert.Equal("Fixture tone 1", line.Text);
        // Word edges come from the audio (anchor-valley): the tone's own start and end.
        Assert.InRange(line.StartMs, 975, 1015); Assert.InRange(line.EndMs, 1485, 1530);
        // Relative to a later window start, times shift accordingly.
        Assert.InRange(Assert.Single(transcriber.Lines(store, HistoryAccess.Host, MimicFixture.Rate, store.LiveFrame)).StartMs, -25, 15);
        // Three words, back to back, each on the absolute timeline, with honest status and pauses at the phrase edges.
        var list = transcriber.WordsBetween(store, HistoryAccess.Host, 0, store.LiveFrame).Select(w => w.Word).ToList();
        Assert.Equal(["Fixture", "tone", "1"], list.Select(w => w.Text));
        Assert.True(list[0].PauseBefore); Assert.True(list[^1].PauseAfter);
        Assert.Equal(list[0].End, list[1].Start); Assert.Equal(list[1].End, list[2].Start);
        Assert.All(list, w => Assert.Contains(w.Status, new[] { WordStatus.Done, WordStatus.Uncertain }));
        Assert.Single(list.Select(w => w.PhraseId).Distinct());
        int calls = engine.Calls;
        store.AppendSilence(MimicFixture.Rate * 5);
        Assert.True(await transcriber.Step(store, false, default));
        Assert.Equal(calls, engine.Calls); // silent chunks never reach the recognizer
        Assert.Empty(transcriber.PendingBetween(store, [new(0, store.LiveFrame)], 1000, store.LiveFrame)); // all of it is recognised now
        var (other, otherWords) = NewHistory("other"); using var closeOther = other;
        Assert.Empty(transcriber.Lines(other, HistoryAccess.Host, 0, 1)); // another session's words never show
        // The words are stored with the history: opened again, the transcript is the same.
        var again = TranscriptWords.Open(Path.Combine(folder, "history"), store.SessionId);
        Assert.Equal(list.Select(w => (w.Id, w.Start, w.End, w.Text)), again.Between(0, store.LiveFrame).Select(w => (w.Word.Id, w.Word.Start, w.Word.End, w.Word.Text)));
    }

    [Fact]
    public async Task TranscriptsNeedTheModelAndOnlyTheHostControlsThem()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture);
        var engine = new TranscriptFixture { Available = false }; bridge.UseTranscriptEngine(engine); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicTranscribe") with { Action = "download" }, "guest")).Ok);
        var missing = await bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host");
        Assert.False(missing.Ok); Assert.Contains("Download the speech model", missing.Error);
        Assert.False(bridge.Snapshot.Mimic!.TranscriptModel);
        Assert.True((await bridge.Execute(Cmd("mimicTranscribe") with { Action = "download" }, "host")).Ok);
        for (int i = 0; i < 50 && !bridge.Snapshot.Mimic!.Transcribing; i++) await Task.Delay(20);
        Assert.True(bridge.Snapshot.Mimic!.Transcribing); Assert.True(bridge.Snapshot.Mimic.TranscriptModel);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(6));
        MimicPeek? peek = null;
        for (int i = 0; i < 40 && (peek?.Transcript?.Length ?? 0) == 0; i++)
        {
            await Task.Delay(100);
            peek = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "guest"));
        }
        Assert.NotEmpty(peek!.Transcript!);
        Assert.All(peek.Transcript!, l => Assert.StartsWith("Fixture tone", l.Text));
        Assert.True((await bridge.Execute(Cmd("endSession"), "host")).Ok);
        Assert.False(bridge.Snapshot.Mimic!.Transcribing);
    }

    [Fact]
    public async Task TranscriptChunksEndAtPausesNotMidSpeech()
    {
        var engine = new TranscriptFixture();
        using var transcriber = new MimicTranscriber(engine);
        var (store, words) = NewHistory(); using var closeStore = store;
        transcriber.Attach(store, words);
        Assert.False(await transcriber.Step(store, false, default));
        float[] Tone(double seconds) => Enumerable.Range(0, (int)(seconds * MimicFixture.Rate)).Select(i => 0.5f * MathF.Sin(i * 0.17f)).ToArray();
        store.Append(Tone(5));
        Assert.False(await transcriber.Step(store, false, default)); // still talking: wait
        Assert.Equal(0, engine.Calls);
        store.AppendSilence(MimicFixture.Rate / 2);
        Assert.True(await transcriber.Step(store, false, default)); // pause: transcribe the whole sentence
        Assert.Single(transcriber.Lines(store, HistoryAccess.Host, 0, store.LiveFrame));
        store.Append(Tone(16));
        Assert.True(await transcriber.Step(store, false, default)); // 15 s without a pause forces a cut
        Assert.Equal(2, engine.Calls);
        // The words at a forced cut that was not at a pause are marked uncertain, never "done".
        var last = transcriber.WordsBetween(store, HistoryAccess.Host, 0, store.LiveFrame).Select(w => w.Word).Last();
        Assert.Equal(WordStatus.Uncertain, last.Status);
        // The cut came at the quietest moment of the last five seconds, so what follows it waits for the next chunk.
        Assert.All(transcriber.PendingBetween(store, [new(0, store.LiveFrame)], 0, store.LiveFrame), p => Assert.True(p.StartFrame >= MimicFixture.Rate * 15L));
    }

    [Fact]
    public async Task PausesAreFoundUnderASteadyBackgroundNotOnlyInSilence()
    {
        // Game sound is rarely silent: speech over a steady bed, then the bed alone. The pre-2.38 rule (RMS under
        // 0.004) never fired here, so every chunk was a forced 15 s cut; a pause relative to the background does.
        var engine = new TranscriptFixture();
        using var transcriber = new MimicTranscriber(engine);
        var (store, words) = NewHistory(); using var closeStore = store;
        transcriber.Attach(store, words);
        Assert.False(await transcriber.Step(store, false, default));
        var random = new Random(7);
        float Bed() => (float)(random.NextDouble() * 2 - 1) * 0.03f;
        store.Append(Enumerable.Range(0, MimicFixture.Rate * 4).Select(i => 0.5f * MathF.Sin(i * 0.17f) + Bed()).ToArray());
        Assert.False(await transcriber.Step(store, false, default)); // still talking
        store.Append(Enumerable.Range(0, MimicFixture.Rate / 2).Select(_ => Bed()).ToArray());
        Assert.True(await transcriber.Step(store, false, default)); // a pause, although the background never went quiet
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task VoiceReplayStudioClipsCanBeListedAndImportedByTheHostOnly()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var vrs = Path.Combine(folder, "vrs"); Directory.CreateDirectory(Path.Combine(vrs, "Clips"));
        bridge.UseVoiceReplayLibrary(new VoiceReplayLibrary(vrs)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        // No library yet: nothing listed, and the host is told.
        var none = await bridge.Execute(Cmd("mimicLibrary"), "host");
        Assert.True(none.Ok); Assert.Contains("\"available\":false", JsonSerializer.Serialize(none.Data, Json.Options));
        // A library as Voice Replay Studio writes it: one clip with audio, one whose file is gone, one MP3.
        string wav = Path.Combine(vrs, "Clips", "HELLO.wav"); File.WriteAllBytes(wav, MimicRing.Wav(Enumerable.Range(0, 57600).Select(i => 0.3f * MathF.Sin(i * 2 * MathF.PI * 440 / 48000)).ToArray(), 48000));
        string mp3 = Path.Combine(vrs, "Clips", "SONG.mp3"); File.WriteAllBytes(mp3, [1, 2, 3]);
        string bandWav = Path.Combine(vrs, "Clips", "BAND.wav"); File.WriteAllBytes(bandWav, MimicRing.Wav(Enumerable.Range(0, 48000).Select(i => 0.3f * MathF.Sin(i * 2 * MathF.PI * 660 / 48000)).ToArray(), 48000));
        Guid hello = Guid.NewGuid(), gone = Guid.NewGuid(), song = Guid.NewGuid(), band = Guid.NewGuid();
        var library = JsonSerializer.Serialize(new object[]
        {
            new { Id = hello, AudioPath = wav, Title = "Hello there", Transcript = "hello there friend", CapturedAt = DateTimeOffset.Now, Duration = 1.2, Summary = "x" },
            new { Id = gone, AudioPath = Path.Combine(vrs, "Clips", "GONE.wav"), Title = "Gone", Transcript = "", CapturedAt = DateTimeOffset.Now.AddMinutes(-1), Duration = 2.0 },
            new { Id = song, AudioPath = mp3, Title = "Song", Transcript = "", CapturedAt = DateTimeOffset.Now.AddMinutes(-2), Duration = 3.0 },
            new { Id = band, AudioPath = bandWav, Title = "AC/DC", Transcript = "", CapturedAt = DateTimeOffset.Now.AddMinutes(-3), Duration = 1.2 },
        });
        File.WriteAllText(Path.Combine(vrs, "library.json"), library);
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(vrs, "library.json"));
        var listed = JsonSerializer.Serialize((await bridge.Execute(Cmd("mimicLibrary"), "host")).Data, Json.Options);
        Assert.Contains("Hello there", listed); Assert.Contains("hello there friend", listed); Assert.Contains("1200", listed);
        Assert.DoesNotContain("Gone", listed);
        // Guests can neither list nor import, even with Guest Mimic on.
        Assert.False((await bridge.Execute(Cmd("mimicLibrary"), "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicLibraryImport") with { SourceId = hello.ToString() }, "guest")).Ok);
        // Importing copies the clip into Temp under its title.
        Assert.True((await bridge.Execute(Cmd("mimicLibraryImport") with { SourceId = hello.ToString() }, "host")).Ok);
        Assert.Contains(bridge.Snapshot.Library.Sounds, s => s.Temporary && s.Name == "Hello there");
        Assert.True(File.Exists(wav)); // the original stays in Voice Replay Studio
        // A slash in a title doesn't cut the sound's name short.
        Assert.True((await bridge.Execute(Cmd("mimicLibraryImport") with { SourceId = band.ToString() }, "host")).Ok);
        Assert.Contains(bridge.Snapshot.Library.Sounds, s => s.Temporary && s.Name == "AC-DC");
        // Missing files, non-WAV files and unknown ids are refused with a reason.
        Assert.Contains("missing", (await bridge.Execute(Cmd("mimicLibraryImport") with { SourceId = gone.ToString() }, "host")).Error);
        Assert.Contains("WAV", (await bridge.Execute(Cmd("mimicLibraryImport") with { SourceId = song.ToString() }, "host")).Error);
        Assert.False((await bridge.Execute(Cmd("mimicLibraryImport") with { SourceId = "not-a-clip" }, "host")).Ok);
        // Voice Replay Studio's library file is never written.
        Assert.Equal(library, File.ReadAllText(Path.Combine(vrs, "library.json")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(vrs, "library.json")));
        // A damaged library is reported and left as it is.
        File.WriteAllText(Path.Combine(vrs, "library.json"), "{ not json");
        var damaged = await bridge.Execute(Cmd("mimicLibrary"), "host");
        Assert.False(damaged.Ok); Assert.Contains("left untouched", damaged.Error);
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(vrs, "library.json")));
    }

    [Fact]
    public async Task HotkeyKeyCanBeChangedAndATakenKeyKeepsTheWorkingOne()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var keys = new Dictionary<string, HotkeyFixture>();
        bridge.UseHotkeys(name => keys[name] = new HotkeyFixture(name) { Taken = name == "F9" }); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        Assert.Equal("F6", bridge.Snapshot.Mimic!.HotkeyKey);
        // Changing the key while off checks it is free, then leaves it off.
        var takenWhileOff = await bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F9" }, "host");
        Assert.False(takenWhileOff.Ok); Assert.Contains("F9 is already used", takenWhileOff.Error);
        Assert.Equal("F6", bridge.Snapshot.Mimic!.HotkeyKey);
        Assert.True((await bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F8" }, "host")).Ok);
        Assert.Equal("F8", bridge.Snapshot.Mimic!.HotkeyKey);
        Assert.False(keys["F8"].Active); Assert.False(bridge.Snapshot.Mimic!.Hotkey);
        Assert.True((await bridge.Execute(Cmd("mimicHotkey") with { Action = "enable" }, "host")).Ok);
        Assert.True(keys["F8"].Active); Assert.Contains("Press F8", bridge.Snapshot.Mimic!.HotkeyStatus);
        // Changing it while on moves the registration.
        Assert.True((await bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F10" }, "host")).Ok);
        Assert.False(keys["F8"].Active); Assert.True(keys["F10"].Active);
        Assert.Contains("Press F10", bridge.Snapshot.Mimic!.HotkeyStatus);
        // A key another app owns is refused and the working key stays registered.
        var taken = await bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F9" }, "host");
        Assert.False(taken.Ok); Assert.Contains("F9 is already used", taken.Error);
        Assert.True(keys["F10"].Active); Assert.Equal("F10", bridge.Snapshot.Mimic!.HotkeyKey);
        // Only the listed keys, and only the host.
        Assert.False((await bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F5" }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicHotkey") with { Action = "key" }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicHotkey") with { Action = "key", Name = "F7" }, "guest")).Ok);
        Assert.Equal("F10", bridge.Snapshot.Mimic!.HotkeyKey);
        Assert.True((await bridge.Execute(Cmd("endSession"), "host")).Ok);
        Assert.False(keys["F10"].Active);
    }

    [Fact]
    public async Task HotkeySavesTheLastTenSecondsNamedAfterTheTranscript()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); bridge.UseTranscriptEngine(new TranscriptFixture());
        var key = new HotkeyFixture { Taken = true }; bridge.UseHotkey(key); await bridge.Poll(true);
        var log = new List<string>(); bridge.MimicLog += log.Add;
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var taken = await bridge.Execute(Cmd("mimicHotkey") with { Action = "enable" }, "host");
        Assert.False(taken.Ok); Assert.Contains("already used", taken.Error);
        key.Taken = false;
        Assert.False((await bridge.Execute(Cmd("mimicHotkey") with { Action = "enable" }, "guest")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicHotkey") with { Action = "enable" }, "host")).Ok);
        Assert.True(bridge.Snapshot.Mimic!.Hotkey);
        await bridge.HotkeyPressed(); // not listening yet
        Assert.Contains("not listening", bridge.Snapshot.Mimic!.HotkeyStatus);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(12));
        // Wait for the transcriber to catch up, as a person would see lines before pressing F6.
        for (int i = 0; i < 50 && (Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "host")).Transcript?.Length ?? 0) == 0; i++) await Task.Delay(100);
        await bridge.HotkeyPressed();
        var sound = bridge.Snapshot.Library.Sounds.Single(s => s.Temporary);
        Assert.StartsWith("Fixture tone", sound.Name);
        Assert.StartsWith("Saved the last 10 s", bridge.Snapshot.Mimic!.HotkeyStatus);
        var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        using (var wave = new NAudio.Wave.WaveFileReader(journal.Assets.Single().Variants.Single().Path))
            Assert.InRange(wave.TotalTime.TotalSeconds, 9.9, 10.1);
        Assert.False((await bridge.Execute(Cmd("mimicHotkey") with { Action = "length", PositionMs = 7000 }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicHotkey") with { Action = "length", PositionMs = 3000 }, "guest")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicHotkey") with { Action = "length", PositionMs = 3000 }, "host")).Ok);
        Assert.Equal(3, bridge.Snapshot.Mimic!.HotkeySeconds);
        Assert.Contains("last 3 s", bridge.Snapshot.Mimic.HotkeyStatus);
        fixture.Advance(TimeSpan.FromSeconds(4));
        await bridge.HotkeyPressed();
        var shortClip = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!.Assets.OrderBy(x => x.CreatedAt).Last();
        using (var wave = new NAudio.Wave.WaveFileReader(shortClip.Variants.Single().Path))
            Assert.InRange(wave.TotalTime.TotalSeconds, 2.9, 3.1);
        Assert.All(bridge.Snapshot.Mimic!.Recent!, r => Assert.Equal("Host", r.By));
        Assert.Contains(log, l => l.StartsWith("Listening to Fixture"));
        Assert.Contains(log, l => l.StartsWith("Host mimicked \"Fixture tone") && l.EndsWith("now in Temp"));
        Assert.Contains(log, l => l.Contains("not listening"));
        Assert.True((await bridge.Execute(Cmd("endSession"), "host")).Ok);
        Assert.False(key.Active); Assert.False(bridge.Snapshot.Mimic!.Hotkey);
        Assert.Contains(log, l => l.StartsWith("Hosting ended"));
        Assert.Empty(bridge.Snapshot.Mimic!.Recent!);
    }

    [Fact]
    public async Task TrimSavesAnExactCopyAndKeepsTheOriginal()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var tone = Enumerable.Range(0, 32000).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray(); // 2 s at 16 kHz
        Assert.True((await bridge.Upload(MimicRing.Wav(tone, 16000), "Long take.wav", "Alice")).Ok);
        var journal = () => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        var original = journal().Assets.Single();
        Assert.False((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 500, EndMs = 550 }, "host")).Ok);
        var trimmed = await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 500, EndMs = 1500 }, "host");
        Assert.True(trimmed.Ok, trimmed.Error);
        var assets = journal().Assets;
        Assert.Equal(2, assets.Count);
        var copy = assets.Single(a => a.Id != original.Id);
        Assert.Equal("Long take (trimmed)", copy.Name);
        using (var wave = new NAudio.Wave.WaveFileReader(copy.Variants.Single().Path)) Assert.InRange(wave.TotalTime.TotalMilliseconds, 990, 1010);
        using (var wave = new NAudio.Wave.WaveFileReader(original.Variants.Single().Path)) Assert.InRange(wave.TotalTime.TotalMilliseconds, 1990, 2010);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestUploads: false) }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 1000 }, "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("trimAudio") with { AssetId = Guid.NewGuid().ToString(), PositionMs = 0, EndMs = 1000 }, "host")).Ok);
    }

    [Fact]
    public void EffectsChangeSpeedAndAddEchoTaps()
    {
        var mono = Enumerable.Range(0, 3000).Select(i => (float)i).ToArray();
        Assert.Equal(2000, AudioEffects.Apply(mono, 1, 1000, "chipmunk").Length);
        Assert.Equal(4285, AudioEffects.Apply(mono, 1, 1000, "slowmo").Length);
        Assert.Same(mono, AudioEffects.Apply(mono, 1, 1000, "none"));
        // Free speed (2.16): 4x slower is 4x longer, 4x faster a quarter; fast speeds average what they skip.
        Assert.Equal(12000, AudioEffects.Apply(mono, 1, 1000, "speed", 0.25).Length);
        Assert.Equal(750, AudioEffects.Apply(mono, 1, 1000, "speed", 4).Length);
        Assert.Equal(1.5f, AudioEffects.Speed([0, 1, 2, 3], 1, 4)[0]);
        var stereo = new float[] { 1, -1, 3, -3, 5, -5, 7, -7 };
        Assert.Equal(new float[] { 1, -1, 4, -4 }, AudioEffects.Speed(stereo, 2, 1.5)); // 4 frames / 1.5 = 2; channels stay separate
        var click = new float[2000]; click[0] = 1;
        var echo = AudioEffects.Echo(click, 1, 1000);
        Assert.Equal(2500, echo.Length);
        Assert.Equal(0.8f, echo[0], 3); Assert.Equal(0.36f, echo[250], 3); Assert.Equal(0.16f, echo[500], 3);
    }

    [Fact]
    public void KeepPitchChangesLengthButNotFrequency()
    {
        // 2.17: "Keep pitch" stretches time with WSOLA; the plain speed effect changes pitch like a tape.
        const int rate = 16000;
        var tone = Enumerable.Range(0, rate * 2).Select(i => 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / rate)).ToArray(); // 2 s, 440 Hz
        static double Hertz(float[] mono, int rate)
        {
            int from = mono.Length / 4, to = mono.Length * 3 / 4, crossings = 0; // middle half, away from the edges
            for (int i = from + 1; i < to; i++) if (mono[i - 1] < 0 && mono[i] >= 0) crossings++;
            return crossings / ((to - from) / (double)rate);
        }
        foreach (var speed in new[] { 0.5, 2.0 })
        {
            var kept = AudioEffects.Apply(tone, 1, rate, "speed", speed, keepPitch: true);
            Assert.InRange(kept.Length, (int)(tone.Length / speed) - 1, (int)(tone.Length / speed) + 1);
            Assert.InRange(Hertz(kept, rate), 430, 450);
            var tape = AudioEffects.Apply(tone, 1, rate, "speed", speed);
            Assert.InRange(Hertz(tape, rate), 440 * speed - 10, 440 * speed + 10);
        }
        // Stereo channels stay separate and in step.
        var stereo = tone.SelectMany(v => new[] { v, -v }).ToArray();
        var stretched = AudioEffects.TimeStretch(stereo, 2, rate, 0.5);
        for (int f = 100; f < stretched.Length / 2; f += 997) Assert.Equal(stretched[f * 2], -stretched[f * 2 + 1], 4);
    }

    [Fact]
    public async Task PlainTrimsAreNamedAfterTheirWordsWhileTranscriptsAreOn()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture);
        var engine = new TranscriptFixture(); bridge.UseTranscriptEngine(engine); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        // 1 s of silence, 1 s of tone, 1 s of silence, at 16 kHz.
        var audio = Enumerable.Range(0, 48000).Select(i => i is >= 16000 and < 32000 ? 0.3f * MathF.Sin(i * 0.2f) : 0f).ToArray();
        Assert.True((await bridge.Upload(MimicRing.Wav(audio, 16000), "Said something.wav", "Alice")).Ok);
        var journal = () => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        var original = journal().Assets.Single();
        // Transcripts off: the usual name, and the engine isn't asked.
        Assert.True((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 3000 }, "host")).Ok);
        Assert.Single(journal().Assets, a => a.Name == "Said something (trimmed)");
        Assert.Equal(0, engine.Calls);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host")).Ok);
        // Transcripts on: a plain trim is named after what it says.
        var named = await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 500, EndMs = 2500 }, "host");
        Assert.True(named.Ok, named.Error);
        Assert.Single(journal().Assets, a => a.Name.StartsWith("Fixture tone ") && a.Name.EndsWith(" (trimmed)"));
        // Silence has no words, so it keeps the usual name; so do effects and names the person typed.
        Assert.True((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 900 }, "host")).Ok);
        Assert.Equal(2, journal().Assets.Count(a => a.Name == "Said something (trimmed)"));
        Assert.True((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 3000, Action = "echo" }, "host")).Ok);
        Assert.Single(journal().Assets, a => a.Name == "Said something (echo)");
        Assert.True((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 200, EndMs = 2800, Name = "My pick" }, "host")).Ok);
        Assert.Single(journal().Assets, a => a.Name == "My pick");
        // Previews are never transcribed.
        int calls = engine.Calls;
        Assert.True((await bridge.Execute(Cmd("trimPreview") with { AssetId = original.Id, PositionMs = 0, EndMs = 3000 }, "host")).Ok);
        Assert.Equal(calls, engine.Calls);
    }

    [Fact]
    public async Task GuestsCanUndoOnlyTheirOwnTemporaryAudio()
    {
        // 2.32: a guest may remove temporary audio they added (Undo), never anyone else's.
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        float[] Tone(float step) => Enumerable.Range(0, 16000).Select(i => 0.3f * MathF.Sin(i * step)).ToArray();
        Assert.True((await bridge.Upload(MimicRing.Wav(Tone(0.2f), 16000), "Alice clip.wav", "Alice", false, "peer-alice")).Ok);
        Assert.True((await bridge.Upload(MimicRing.Wav(Tone(0.3f), 16000), "Bob clip.wav", "Bob", false, "peer-bob")).Ok);
        Assert.True((await bridge.Upload(MimicRing.Wav(Tone(0.4f), 16000), "Host clip.wav", "Host", true, "host")).Ok);
        var journal = () => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        string Id(string name) => journal().Assets.Single(a => a.Name == name).Id;
        Task<Reply> Remove(string name, string peer) => bridge.Execute(Cmd("removeAudio") with { AssetId = Id(name), Action = "stopIfPlaying" }, "guest", null, peer);
        bool Gone(string name) => journal().Assets.Single(a => a.Name == name).Variants.All(v => v.Retired || v.State == "removed");
        // Someone else's, or the host's: refused, and nothing is removed.
        var bob = await Remove("Bob clip", "peer-alice");
        Assert.False(bob.Ok); Assert.Contains("only temporary audio they added", bob.Error);
        Assert.False((await Remove("Host clip", "peer-alice")).Ok);
        Assert.False(Gone("Bob clip")); Assert.False(Gone("Host clip"));
        // Without a known requester (no session identity), refused too.
        Assert.False((await bridge.Execute(Cmd("removeAudio") with { AssetId = Id("Alice clip") }, "guest")).Ok);
        // Once the host stops guests adding audio, they can't remove it either.
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestUploads: false) }, "host")).Ok);
        Assert.False((await Remove("Alice clip", "peer-alice")).Ok);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestUploads: true) }, "host")).Ok);
        // Their own: removed.
        var own = await Remove("Alice clip", "peer-alice");
        Assert.True(own.Ok, own.Error);
        Assert.True(Gone("Alice clip")); Assert.False(Gone("Bob clip")); Assert.False(Gone("Host clip"));
    }

    [Fact]
    public async Task UndoStopsOnlyItsOwnPlayingSource()
    {
        // 2.30: removeAudio with "stopIfPlaying" stops playback only when the companion itself started this
        // source; another sound keeps playing and the removal is refused as before.
        await using var mock = new MockSoundpad { DurationMs = 60000 }; using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        float[] Tone(float step) => Enumerable.Range(0, 16000).Select(i => 0.3f * MathF.Sin(i * step)).ToArray();
        Assert.True((await bridge.Upload(MimicRing.Wav(Tone(0.2f), 16000), "Playing one.wav", "Alice")).Ok);
        Assert.True((await bridge.Upload(MimicRing.Wav(Tone(0.3f), 16000), "Other one.wav", "Alice")).Ok);
        var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        var playingOne = journal.Assets.Single(a => a.Name == "Playing one");
        var otherOne = journal.Assets.Single(a => a.Name == "Other one");
        var played = await bridge.Execute(Cmd("audioPlay") with { AssetId = playingOne.Id, Orientation = "normal" }, "host");
        Assert.True(played.Ok, played.Error);
        Assert.Equal("PLAYING", await pipe.Request("GetPlayStatus()"));
        // Undo of a different source never stops what is playing.
        var other = await bridge.Execute(Cmd("removeAudio") with { AssetId = otherOne.Id, Action = "stopIfPlaying" }, "host");
        Assert.False(other.Ok); Assert.Contains("Stop playback", other.Error);
        Assert.Equal("PLAYING", await pipe.Request("GetPlayStatus()"));
        // Guests can't remove, and their request stops nothing.
        Assert.False((await bridge.Execute(Cmd("removeAudio") with { AssetId = playingOne.Id, Action = "stopIfPlaying" }, "guest")).Ok);
        Assert.Equal("PLAYING", await pipe.Request("GetPlayStatus()"));
        // Undo of the playing source stops it, then removes it.
        var own = await bridge.Execute(Cmd("removeAudio") with { AssetId = playingOne.Id, Action = "stopIfPlaying" }, "host");
        Assert.True(own.Ok, own.Error);
        Assert.Equal("STOPPED", await pipe.Request("GetPlayStatus()"));
        await bridge.Poll(true);
        Assert.DoesNotContain(bridge.Snapshot.Library.Sounds, s => s.AssetId == playingOne.Id);
        Assert.Contains(bridge.Snapshot.Library.Sounds, s => s.AssetId == otherOne.Id);
    }

    [Fact]
    public async Task CopyNamesKeepEveryWordAndStayPlain()
    {
        // 2.29.3: a '/' in a copy's name cut it short ("24/7" -> "7"), and Whisper's curly quotes and dashes
        // are not plain ASCII.
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var tone = Enumerable.Range(0, 32000).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray();
        Assert.True((await bridge.Upload(MimicRing.Wav(tone, 16000), "Song.wav", "Alice")).Ok);
        var journal = () => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        var original = journal().Assets.Single();
        Assert.True((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 1500, Name = @"Open 24/7 AC\DC" }, "host")).Ok);
        Assert.Single(journal().Assets, a => a.Name == "Open 24-7 AC-DC");
        Assert.Equal("don't stop - it's \"fine\"...", Bridge.PlainWords("don’t stop — it’s “fine”…"));
    }

    [Fact]
    public async Task TrimPreviewEndsOnAWholeFrame()
    {
        // 2.29.3: the size cap on a preview could end mid-frame for sources whose channel count does not
        // divide it (6 channels here).
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        const int rate = 48000, channels = 6; // 10 s is 2.88 million samples, over the 2.75 million cap
        using var stream = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(stream, new NAudio.Wave.WaveFormat(rate, 16, channels)))
            for (int i = 0; i < rate * 10 * channels; i++) writer.WriteSample(0.2f * MathF.Sin(i / channels * 0.2f));
        Assert.True((await bridge.Upload(stream.ToArray(), "Surround.wav", "Alice")).Ok);
        var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        var reply = await bridge.Execute(Cmd("trimPreview") with { AssetId = journal.Assets.Single().Id, PositionMs = 0, EndMs = 10000 }, "host");
        Assert.True(reply.Ok, reply.Error);
        var wav = Convert.FromBase64String(JsonSerializer.SerializeToElement(reply.Data, Json.Options).GetProperty("wav").GetString()!);
        using var read = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
        Assert.Equal(channels, read.WaveFormat.Channels);
        Assert.Equal(0, read.Length % read.WaveFormat.BlockAlign);
    }

    [Fact]
    public async Task TrimPreviewPlaysTheCopyToTheHostAndSavesNothing()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var tone = Enumerable.Range(0, 16000 * 30).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray(); // 30 s at 16 kHz
        Assert.True((await bridge.Upload(MimicRing.Wav(tone, 16000), "Long take.wav", "Alice")).Ok);
        var journal = () => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        var original = journal().Assets.Single();
        double Seconds(Reply r)
        {
            Assert.True(r.Ok, r.Error);
            var wav = Convert.FromBase64String(JsonSerializer.SerializeToElement(r.Data, Json.Options).GetProperty("wav").GetString()!);
            using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
            return reader.TotalTime.TotalSeconds;
        }
        // The cut itself, with the same effect rules as Save copy: 1 s at half speed lasts 2 s.
        Assert.InRange(Seconds(await bridge.Execute(Cmd("trimPreview") with { AssetId = original.Id, PositionMs = 500, EndMs = 1500 }, "host")), 0.99, 1.01);
        Assert.InRange(Seconds(await bridge.Execute(Cmd("trimPreview") with { AssetId = original.Id, PositionMs = 0, EndMs = 1000, Action = "speed", Speed = 0.5, KeepPitch = true }, "host")), 1.99, 2.01);
        // A long cut previews only its first 20 s, even when slowed down a lot.
        Assert.InRange(Seconds(await bridge.Execute(Cmd("trimPreview") with { AssetId = original.Id, PositionMs = 0, EndMs = 30000 }, "host")), 19.99, 20.01);
        Assert.InRange(Seconds(await bridge.Execute(Cmd("trimPreview") with { AssetId = original.Id, PositionMs = 0, EndMs = 30000, Action = "speed", Speed = 0.05 }, "host")), 19.99, 20.01);
        // Guests never get preview audio, and the same checks as Save copy apply.
        Assert.False((await bridge.Execute(Cmd("trimPreview") with { AssetId = original.Id, PositionMs = 0, EndMs = 1000 }, "guest")).Ok);
        Assert.False((await bridge.Execute(Cmd("trimPreview") with { AssetId = original.Id, PositionMs = 0, EndMs = 50 }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("trimPreview") with { AssetId = original.Id, PositionMs = 0, EndMs = 1000, Action = "robot" }, "host")).Ok);
        // A preview saves nothing.
        Assert.Single(journal().Assets);
        Assert.Single(bridge.Snapshot.Library.Sounds, s => s.Temporary);
    }

    [Fact]
    public async Task EditedCopiesCanCarryAVoiceEffect()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        var tone = Enumerable.Range(0, 48000).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray(); // 3 s at 16 kHz
        Assert.True((await bridge.Upload(MimicRing.Wav(tone, 16000), "Said it.wav", "Alice")).Ok);
        var journal = () => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        var original = journal().Assets.Single();
        Assert.False((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 3000, Action = "robot" }, "host")).Ok);
        var fast = await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 3000, Action = "chipmunk" }, "host");
        Assert.True(fast.Ok, fast.Error);
        var copy = journal().Assets.Single(a => a.Id != original.Id);
        Assert.Equal("Said it (chipmunk)", copy.Name);
        using var wave = new NAudio.Wave.WaveFileReader(copy.Variants.Single().Path);
        Assert.InRange(wave.TotalTime.TotalMilliseconds, 1990, 2010);
        // Any speed in range: half speed makes 3 s into 6 s and says so in the name.
        foreach (var bad in new double?[] { null, 0, 0.01, 25, double.NaN })
            Assert.False((await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 3000, Action = "speed", Speed = bad }, "host")).Ok);
        var slow = await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 3000, Action = "speed", Speed = 0.5 }, "host");
        Assert.True(slow.Ok, slow.Error);
        var halved = journal().Assets.Single(a => a.Name == "Said it (0.5x speed)");
        var same = await bridge.Execute(Cmd("trimAudio") with { AssetId = original.Id, PositionMs = 0, EndMs = 3000, Action = "speed", Speed = 0.5, KeepPitch = true }, "host");
        Assert.True(same.Ok, same.Error);
        Assert.Single(journal().Assets, a => a.Name == "Said it (0.5x speed, same pitch)");
        using var slowWave = new NAudio.Wave.WaveFileReader(halved.Variants.Single().Path);
        Assert.InRange(slowWave.TotalTime.TotalMilliseconds, 5990, 6010);
    }

    [Fact]
    public async Task PeeksWithin100MsShareOneComputation()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(5));
        var first = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "host"));
        var second = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "host"));
        Assert.Equal(first.End, second.End); // same snapshot, so a clip lines up with what either viewer saw
        Assert.Equal(first.Levels, second.Levels);
        fixture.Advance(TimeSpan.FromSeconds(1));
        var later = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "host"));
        Assert.True(later.End >= first.End + MimicFixture.Rate);
    }

    [Fact]
    public async Task ReviewFixesForTrimClipsAndTranscripts()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("beginSession"), "host")).Ok);
        // A slow-mo copy of a 15 MiB upload would be ~21 MiB: it is reduced (here to a lower rate) to fit 16 MiB.
        var take = new float[48000 * 163];
        for (int i = 0; i < take.Length; i++) take[i] = 0.3f * MathF.Sin(i * 0.05f);
        Assert.True((await bridge.Upload(MimicRing.Wav(take, 48000), "Long take.wav", "Host", true)).Ok);
        var journal = () => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(folder, "managed-audio", "journal.json")), Json.Options)!;
        var source = journal().Assets.Single();
        var slow = await bridge.Execute(Cmd("trimAudio") with { AssetId = source.Id, PositionMs = 0, EndMs = 163000, Action = "slowmo" }, "host");
        Assert.True(slow.Ok, slow.Error);
        var copy = journal().Assets.Single(a => a.Id != source.Id);
        using (var wave = new NAudio.Wave.WaveFileReader(copy.Variants.Single().Path))
            Assert.InRange(wave.TotalTime.TotalSeconds, 230, 235); // full slow-mo length kept
        var (stereoFit, stereoRate, stereoChannels) = Bridge.FitForTemp(new float[10_000_000], 48000, 2);
        Assert.Equal((5_000_000, 48000, 1), (stereoFit.Length, stereoRate, stereoChannels)); // mono is enough here
        // A revoked guest is refused before any decoding, even with uploads allowed.
        Assert.True((await bridge.Execute(Cmd("revokePeer") with { OwnerId = "peer-9" }, "host")).Ok);
        var revoked = await bridge.Execute(Cmd("trimAudio") with { AssetId = source.Id, PositionMs = 0, EndMs = 1000 }, "guest", new("g", "Mallory"), "peer-9");
        Assert.False(revoked.Ok); Assert.Contains("not allowed", revoked.Error);
        // A clip from an old view (different capture) is refused instead of cutting the new source.
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(5));
        var peek = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "host"));
        Assert.NotNull(peek.RingId);
        var stale = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 25000, EndMs = 30000, SourceId = "an-older-capture" }, "host");
        Assert.False(stale.Ok); Assert.Contains("restarted", stale.Error);
        // A transcriber step from a replaced loop commits nothing.
        using var transcriber = new MimicTranscriber(new TranscriptFixture());
        var (store, words) = NewHistory(); using var closeStore = store;
        transcriber.Attach(store, words);
        Assert.False(await transcriber.Step(store, false, default));
        store.Append(Burst(MimicFixture.Rate * 3));
        store.AppendSilence(MimicFixture.Rate);
        Assert.False(await transcriber.Step(store, false, default, generation: 999));
        Assert.Empty(transcriber.Lines(store, HistoryAccess.Host, 0, store.LiveFrame));
    }

    [Fact]
    public async Task PreviewReturnsTheSelectionToTheHostOnly()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true); using var bridge = new Bridge(pipe, new StateStore(folder));
        var fixture = new MimicFixture(); bridge.UseMimicCapture(fixture); await bridge.Poll(true);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: true) }, "host")).Ok);
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(10));
        var peek = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "host"));
        var select = Cmd("mimicPreview") with { MimicEnd = peek.End, PositionMs = 27000, EndMs = 29500, SourceId = peek.RingId };
        var guest = await bridge.Execute(select, "guest");
        Assert.False(guest.Ok); // audio never goes to guests
        var preview = await bridge.Execute(select with { Id = Guid.NewGuid().ToString() }, "host");
        Assert.True(preview.Ok, preview.Error);
        var wav = Convert.FromBase64String(JsonSerializer.SerializeToElement(preview.Data, Json.Options).GetProperty("wav").GetString()!);
        using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
        Assert.InRange(reader.TotalTime.TotalMilliseconds, 2490, 2510);
        Assert.DoesNotContain(bridge.Snapshot.Library.Sounds, s => s.Temporary); // a preview saves nothing
    }
}
