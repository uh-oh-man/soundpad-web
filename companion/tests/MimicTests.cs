using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class MimicTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-mimic-test-" + Guid.NewGuid().ToString("N"));
    public MimicTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
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
        Assert.True((await bridge.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host")).Ok);
        fixture.Advance(TimeSpan.FromSeconds(31));
        var peek = Data<MimicPeek>(await bridge.Execute(Cmd("mimicPeek"), "guest"));
        Assert.Equal(Bridge.MimicBuckets, peek.Levels.Length); Assert.Contains(peek.Levels, l => l > 400); Assert.Contains(0, peek.Levels);
        long windowStart = peek.End - MimicFixture.Rate * 30L;
        // Find the start of a tone burst inside the window and select exactly its half second.
        long burst = windowStart + (2 * MimicFixture.Rate - windowStart % (2 * MimicFixture.Rate)) % (2 * MimicFixture.Rate);
        long fromMs = (burst - windowStart) * 1000 / MimicFixture.Rate;
        var silent = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs + 600, EndMs = fromMs + 1400 }, "guest");
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
        fixture.Advance(TimeSpan.FromSeconds(31 + Bridge.MimicGraceSeconds));
        var expired = await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = fromMs, EndMs = fromMs + 500 }, "guest");
        Assert.False(expired.Ok); Assert.Contains("scrolled out", expired.Error);
        Assert.True((await bridge.Execute(Cmd("permissions") with { Permissions = new(GuestMimic: false) }, "host")).Ok);
        Assert.False((await bridge.Execute(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = 0, EndMs = 500 }, "guest")).Ok);
    }

    [Fact]
    public async Task TranscriptLinesLineUpWithTheBufferAndSilenceIsSkipped()
    {
        var engine = new TranscriptFixture();
        using var transcriber = new MimicTranscriber(engine);
        var ring = new MimicRing(MimicFixture.Rate, 45);
        Assert.False(await transcriber.Step(ring, false, default));
        // 1 s of silence, 0.5 s of tone, 2.5 s of silence: one line at 1.0-1.5 s.
        ring.WriteSilence(MimicFixture.Rate);
        ring.Write(Enumerable.Range(0, MimicFixture.Rate / 2).Select(i => MimicFixture.Sample(i)).ToArray());
        ring.WriteSilence(MimicFixture.Rate * 5 / 2);
        Assert.True(await transcriber.Step(ring, false, default));
        var line = Assert.Single(transcriber.Lines(ring, 0, ring.Written));
        Assert.Equal("Fixture tone 1", line.Text);
        Assert.InRange(line.StartMs, 990, 1010); Assert.InRange(line.EndMs, 1490, 1520);
        // Relative to a later window start, times shift accordingly.
        Assert.InRange(Assert.Single(transcriber.Lines(ring, MimicFixture.Rate, ring.Written)).StartMs, -10, 10);
        int calls = engine.Calls;
        ring.WriteSilence(MimicFixture.Rate * 5);
        Assert.True(await transcriber.Step(ring, false, default));
        Assert.Equal(calls, engine.Calls); // silent chunks never reach the recognizer
        Assert.Empty(transcriber.Lines(new MimicRing(MimicFixture.Rate, 45), 0, 1));
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
        var ring = new MimicRing(MimicFixture.Rate, 45);
        Assert.False(await transcriber.Step(ring, false, default));
        float[] Tone(double seconds) => Enumerable.Range(0, (int)(seconds * MimicFixture.Rate)).Select(i => 0.5f * MathF.Sin(i * 0.17f)).ToArray();
        ring.Write(Tone(5));
        Assert.False(await transcriber.Step(ring, false, default)); // still talking: wait
        Assert.Equal(0, engine.Calls);
        ring.WriteSilence(MimicFixture.Rate / 2);
        Assert.True(await transcriber.Step(ring, false, default)); // pause: transcribe the whole sentence
        Assert.Single(transcriber.Lines(ring, 0, ring.Written));
        ring.Write(Tone(16));
        Assert.True(await transcriber.Step(ring, false, default)); // 15 s without a pause forces a cut
        Assert.Equal(2, engine.Calls);
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
        Assert.Equal("Host", Assert.Single(bridge.Snapshot.Mimic!.Recent!).By);
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
        var stereo = new float[] { 1, -1, 3, -3, 5, -5, 7, -7 };
        Assert.Equal(new float[] { 1, -1, 4, -4 }, AudioEffects.Speed(stereo, 2, 1.5)); // 4 frames / 1.5 = 2; channels stay separate
        var click = new float[2000]; click[0] = 1;
        var echo = AudioEffects.Echo(click, 1, 1000);
        Assert.Equal(2500, echo.Length);
        Assert.Equal(0.8f, echo[0], 3); Assert.Equal(0.36f, echo[250], 3); Assert.Equal(0.16f, echo[500], 3);
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
        var ring = new MimicRing(MimicFixture.Rate, 45);
        ring.Write(Enumerable.Range(0, MimicFixture.Rate * 3).Select(i => MimicFixture.Sample(i)).ToArray());
        ring.WriteSilence(MimicFixture.Rate);
        Assert.False(await transcriber.Step(ring, false, default, generation: 999));
        Assert.Empty(transcriber.Lines(ring, 0, ring.Written));
    }
}
