using SoundpadWeb;
using SoundpadWeb.Testing;
if (args.FirstOrDefault() == "--real-slice-audio-probe") { Environment.ExitCode = await RealSliceAudioProbe.Run(); return; }
// Exact cue starts and regions on the real Soundpad (2.37); see RealExactPlaybackProbe.cs before running it.
if (args.FirstOrDefault() == "--real-exact-playback-probe") { Environment.ExitCode = await RealExactPlaybackProbe.Run(); return; }
if (args.FirstOrDefault() == "--real-slot-probe") { Environment.ExitCode = await RealSlotProbe.Run(); return; }
if (args.FirstOrDefault() == "--word-timing-eval" && args.Length >= 2) { await WordTimingEval.Run(args); return; } // see WordTimingEval.cs
if (args.FirstOrDefault() == "--word-pipeline-eval" && args.Length >= 2) { Environment.ExitCode = await WordPipelineEval.Run(args); return; } // see WordPipelineEval.cs (2.39)
if (args.FirstOrDefault() == "--speaker-eval" && args.Length >= 2) { Environment.ExitCode = SpeakerGroupingEval.Run(args); return; } // see SpeakerGroupingEval.cs (2.41)
if (args.FirstOrDefault() == "--mock-only")
{
    await using var isolated = new MockSoundpad(); isolated.DurationMs = 600000;
    Console.WriteLine("TEST_PIPE " + isolated.Name);
    while (Console.ReadLine() is string line) { if (line == "play") isolated.BeginFixturePlayback(); else if (line == "exit") break; }
    return;
}
if (args.FirstOrDefault() == "--playback-bench")
{
    // --playback-bench [--latency-ms N] [--seconds S] [--seed N] [--out file.json] (2.35): two clients alternating
    // plays against a mock Soundpad under background load; reports latency, obsolete plays and error answers.
    await PlaybackBench.Run(args);
    return;
}
if (args.FirstOrDefault() == "--spotify-status")
{
    var status = await new SpotifyControl().Request("status");
    Console.WriteLine(Json.Write(new { status.Available, status.CanToggle, status.CanNext, status.CanPrevious, status.Error, status.SourceId, hasArtwork = status.Artwork != null })); return;
}
if (args.FirstOrDefault() == "--mimic-probe")
{
    // Real-device check: lists sources, then listens to one for two seconds and reports only levels. Nothing is saved.
    using var capture = new WasapiMimicCapture();
    var sources = capture.Sources();
    foreach (var s in sources) Console.WriteLine(s.Kind + "	" + s.Name);
    var pick = args.Length > 1 ? sources.First(s => s.Id == args[1] || s.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)) : sources.First(s => s.Kind == "output");
    var ring = new MimicRing(capture.Rate, 5);
    await capture.Start(pick, ring); await Task.Delay(2000);
    capture.Pump(); var levels = ring.Levels(ring.Written, ring.Rate * 2L, 20);
    Console.WriteLine(Json.Write(new { source = pick.Name, capture.Status, capture.Running, frames = ring.Written, capture.Packets, rate = ring.Rate, peak = levels.Max() }));
    return;
}
if (args.FirstOrDefault() == "--mimic-stream-transcript" && args.Length == 2)
{
    // Streams a WAV into Mimic's ring in 10 ms packets at real-time pace, with the real Whisper engine and
    // transcriber loop, then prints the lines as the Mimic window would show them. No audio device is used.
    using var file = new NAudio.Wave.WaveFileReader(args[1]);
    var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(file);
    var mono = new List<float>(); var frame = new float[provider.WaveFormat.Channels * 4096]; int read;
    while ((read = provider.Read(frame)) > 0)
        for (int i = 0; i + provider.WaveFormat.Channels <= read; i += provider.WaveFormat.Channels) mono.Add(frame.AsSpan(i, provider.WaveFormat.Channels).ToArray().Average());
    var audio = MimicTranscriber.Resample(mono.ToArray(), provider.WaveFormat.SampleRate, 48000);
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb");
    using var transcriber = new MimicTranscriber(new WhisperEngine(Path.Combine(data, "models", "ggml-base.en.bin"), null, Path.Combine(data, "native", "whisper-1.9.1")));
    // A throwaway history in a temporary folder (2.39), never the companion's own data folder.
    var (store, words, folder) = HistorySinkFixture.Temporary(48000);
    try
    {
        transcriber.Attach(store, words);
        store.AppendSilence(48000);
        await transcriber.Enable(() => store, false);
        await Task.Delay(700); // the loop attaches at "now"
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int offset = 0; offset < audio.Length; offset += 480)
        {
            store.Append(audio.AsSpan(offset, Math.Min(480, audio.Length - offset)));
            var due = TimeSpan.FromSeconds((offset + 480) / 48000.0) - clock.Elapsed;
            if (due > TimeSpan.Zero) await Task.Delay(due);
        }
        for (int i = 0; i < 30; i++) { store.AppendSilence(4800); await Task.Delay(100); } // 3 s of trailing silence, like a pause
        long end = store.LiveFrame;
        foreach (var line in transcriber.Lines(store, HistoryAccess.Host, 0, end)) Console.WriteLine($"[{line.StartMs / 1000.0:0.00} s - {line.EndMs / 1000.0:0.00} s] {line.Text}");
        foreach (var (word, _) in transcriber.WordsBetween(store, HistoryAccess.Host, 0, end)) Console.WriteLine($"  {word.Start / 48.0:0} - {word.End / 48.0:0} ms  {word.Text}  p={word.Probability:0.00} stretch={word.StretchMs} {word.Status}");
        Console.WriteLine($"Streamed {audio.Length / 48000.0:0.0} s in {clock.Elapsed.TotalSeconds:0.0} s.");
    }
    finally { transcriber.Disable(); HistorySinkFixture.Delete(store, folder); }
    return;
}
if (args.FirstOrDefault() == "--cleanup-real-categories")
{
    // Recovery tool (2.15.9): on this PC's real Soundpad and companion data, sends the same host command as
    // Temp's "Remove duplicate categories" button, then reports what is left. Run it with the companion closed.
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb");
    using var realPipe = new SoundpadPipe();
    using var realBridge = new Bridge(realPipe, new StateStore(data));
    await realBridge.Poll(true);
    Console.WriteLine($"Soundpad available: {realBridge.Snapshot.Available}; categories: {realBridge.Snapshot.Library.Categories.Length}");
    var started = System.Diagnostics.Stopwatch.StartNew();
    var reply = await realBridge.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "cleanupCategories"), "host");
    Console.WriteLine($"cleanupCategories: ok={reply.Ok} {reply.Error} {Json.Write(reply.Data)} in {started.Elapsed.TotalSeconds:0.0} s");
    await realBridge.Poll(true);
    foreach (var c in realBridge.Snapshot.Library.Categories) Console.WriteLine($"  category: {c.Name} ({c.SoundIds.Length} sounds)");
    return;
}
if (args.FirstOrDefault() == "--close-signal-probe" && args.Length == 2)
{
    // Proves the companion's window-close handling (2.16): registers the same SIGHUP handler (Windows maps it to
    // CTRL_CLOSE_EVENT, the window's X) and writes a marker file from it, given ~1 s of simulated cleanup.
    using var onClose = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, _ =>
    {
        Thread.Sleep(1000);
        File.WriteAllText(args[1], "cleanup ran on window close at " + DateTime.Now.ToString("HH:mm:ss.fff"));
    });
    Console.WriteLine("waiting to be closed...");
    Thread.Sleep(Timeout.Infinite);
}
if (args.FirstOrDefault() == "--stretch" && args.Length == 5)
{
    // --stretch <in.wav> <out.wav> <speed> <keep-pitch true|false>: writes Edit copy's speed effect to a file (2.17).
    using var decoded = new DecodedAudio(args[1]); // the companion's own decoder, as Edit copy uses
    var samples = new List<float>(); var block = new float[decoded.Channels * 4096]; int got;
    while ((got = decoded.Read(block)) > 0) samples.AddRange(block.AsSpan(0, got));
    double factor = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    var result = AudioEffects.Apply(samples.ToArray(), decoded.Channels, decoded.Rate, "speed", factor, bool.Parse(args[4]));
    using (var writer = new NAudio.Wave.WaveFileWriter(args[2], new NAudio.Wave.WaveFormat(decoded.Rate, 16, decoded.Channels)))
        foreach (var sample in result) writer.WriteSample(Math.Clamp(sample, -1f, 1f));
    Console.WriteLine($"{samples.Count / decoded.Channels / (double)decoded.Rate:0.00} s -> {result.Length / decoded.Channels / (double)decoded.Rate:0.00} s");
    return;
}
if (args.FirstOrDefault() == "--trim-words" && args.Length == 4)
{
    // --trim-words <in.wav> <fromMs> <toMs>: the name Edit copy would give that trim with transcripts on (2.24),
    // using the real speech model on this PC (read-only). Touches neither Soundpad nor the companion's data.
    var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb", "models");
    using var engine = new WhisperEngine(Path.Combine(models, "ggml-base.en.bin"), null, Path.Combine(Path.GetTempPath(), "soundpad-web-trim-words-native"));
    using var words = new MimicTranscriber(engine);
    await words.Enable(() => null, false);
    using var decoded = new DecodedAudio(args[1]);
    long from = long.Parse(args[2]), to = long.Parse(args[3]);
    var cut = new List<float>(); var block = new float[decoded.Channels * 4096]; int got;
    long skip = from * decoded.Rate / 1000 * decoded.Channels, keep = (to - from) * decoded.Rate / 1000 * decoded.Channels;
    while (keep > 0 && (got = decoded.Read(block)) > 0)
    {
        int offset = (int)Math.Min(skip, got); skip -= offset;
        int take = (int)Math.Min(got - offset, keep); cut.AddRange(block.AsSpan(offset, take)); keep -= take;
    }
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var said = await words.Words(cut.ToArray(), decoded.Rate, decoded.Channels, default);
    Console.WriteLine($"{from / 1000.0:0.0}-{to / 1000.0:0.0} s -> \"{said ?? "(no words: keeps the usual name)"} (trimmed)\" in {clock.Elapsed.TotalSeconds:0.0} s");
    words.Disable();
    return;
}
if (args.FirstOrDefault() == "--refresh-bench")
{
    // Read-only timing of library refreshes on this PC's Soundpad, with a throwaway data folder (never the companion's).
    var scratch = Path.Combine(Path.GetTempPath(), "soundpad-web-bench-" + Guid.NewGuid().ToString("N"));
    try
    {
        using var realPipe = new SoundpadPipe();
        using var benchBridge = new Bridge(realPipe, new StateStore(scratch));
        for (int i = 1; i <= 4; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew(); await benchBridge.Poll(true);
            Console.WriteLine($"refresh {i}: {watch.ElapsedMilliseconds} ms ({benchBridge.Snapshot.Library.Sounds.Length} sounds, {benchBridge.Snapshot.Library.Categories.Length} categories)");
        }
    }
    finally { try { Directory.Delete(scratch, true); } catch (IOException) { } }
    return;
}
if (args.FirstOrDefault() == "--real-session-cleanup")
{
    // Runs exactly what the companion does on start and on window close (2.16): End-hosting cleanup on this PC's
    // real Soundpad and companion data. Run with the companion closed.
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb");
    using var realPipe = new SoundpadPipe();
    using var realBridge = new Bridge(realPipe, new StateStore(data));
    await realBridge.Poll(true);
    Console.WriteLine("before: " + string.Join(", ", realBridge.Snapshot.Library.Categories.Select(c => c.Name)));
    await realBridge.CleanupSession(); await realBridge.Poll(true);
    Console.WriteLine("after: " + string.Join(", ", realBridge.Snapshot.Library.Categories.Select(c => c.Name)));
    var journal = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "managed-audio", "journal.json"))).RootElement;
    Console.WriteLine($"journal: tempCategory={journal.GetProperty("tempCategory")}; assets={journal.GetProperty("assets").GetArrayLength()}; issues: {string.Join("; ", journal.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()))}");
    return;
}
if (args.FirstOrDefault() == "--real-edit-copy-probe")
{
    // Owner-approved check (2.27) of Edit copy on this PC's real Soundpad, run with the companion closed: upload a
    // tone, make a chipmunk copy, preview, then End hosting. It uses a throwaway data folder (never the companion's),
    // so its Temp category is its own; nothing is played aloud. It reports whether Soundpad ends exactly as before.
    var scratch = Path.Combine(Path.GetTempPath(), "soundpad-web-edit-probe-" + Guid.NewGuid().ToString("N"));
    using var real = new SoundpadPipe();
    async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { NativeCatalog c = await ManagedSoundpad.Read(real); for (int i = 0; i < 30 && !done(c); i++) { await Task.Delay(200); c = await ManagedSoundpad.Read(real); } return c; }
    Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    var before = await ManagedSoundpad.Read(real);
    Console.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
    try
    {
        using (var probe = new Bridge(real, new StateStore(scratch)))
        {
            await probe.Poll(true);
            Console.WriteLine("begin session: " + (await probe.Execute(Cmd("beginSession"), "host")).Ok);
            var tone = Enumerable.Range(0, 48000 * 2).Select(i => 0.2f * MathF.Sin(i * 2 * MathF.PI * 523 / 48000)).ToArray();
            var upload = await probe.Upload(MimicRing.Wav(tone, 48000), "Edit probe tone.wav", "Probe");
            Console.WriteLine($"upload: {upload.Ok} {upload.Error}");
            var journal = System.Text.Json.JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(scratch, "managed-audio", "journal.json")), Json.Options)!;
            var source = journal.Assets.Single();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var copy = await probe.Execute(Cmd("trimAudio") with { AssetId = source.Id, PositionMs = 250, EndMs = 1750, Action = "chipmunk" }, "host");
            Console.WriteLine($"chipmunk copy: {copy.Ok} {copy.Error} in {clock.ElapsedMilliseconds} ms");
            var named = System.Text.Json.JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(scratch, "managed-audio", "journal.json")), Json.Options)!.Assets.SingleOrDefault(x => x.Id != source.Id);
            var path = named?.Variants.FirstOrDefault()?.Path;
            var listed = await Settle(c => path != null && c.Entries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine($"copy \"{named?.Name}\" in Soundpad: {path != null && listed.Entries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))}");
            if (path != null) using (var wave = new NAudio.Wave.WaveFileReader(path)) Console.WriteLine($"copy length: {wave.TotalTime.TotalSeconds:0.00} s (1.5 s at 1.5x = 1.00 s)");
            var preview = await probe.Execute(Cmd("trimPreview") with { AssetId = source.Id, PositionMs = 0, EndMs = 2000, Action = "echo" }, "host");
            Console.WriteLine($"echo preview: {preview.Ok} {preview.Error} (returned to this tool only, not played)");
            Console.WriteLine("end session: " + (await probe.Execute(Cmd("endSession"), "host")).Ok);
        }
        var after = await Settle(c => c.Entries.Length == before.Entries.Length && c.Categories.Length == before.Categories.Length);
        bool same = after.Entries.Select(e => e.Path).OrderBy(p => p).SequenceEqual(before.Entries.Select(e => e.Path).OrderBy(p => p))
            && after.Categories.Select(c => c.Name).OrderBy(n => n).SequenceEqual(before.Categories.Select(c => c.Name).OrderBy(n => n));
        Console.WriteLine($"after: {after.Entries.Length} sounds, {after.Categories.Length} categories; RESULT: Soundpad {(same ? "is exactly as before" : "CHANGED")}");
    }
    finally
    {
        try { Directory.Delete(scratch, true); Console.WriteLine("throwaway data folder deleted"); } catch (IOException ex) { Console.WriteLine("could not delete " + scratch + ": " + ex.Message); }
    }
    return;
}
if (args.FirstOrDefault() == "--test-real-category-removal")
{
    // Owner-approved probe (2.16): does removing a category also remove its sounds from Soundpad's main list?
    // Adds one silent WAV in its own new category, removes the category, and reports. If the sound is left in the
    // main list it is removed with "remove selected" inside a fresh category holding only it. Leaves nothing behind.
    using var real = new SoundpadPipe();
    async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { NativeCatalog c = await ManagedSoundpad.Read(real); for (int i = 0; i < 20 && !done(c); i++) { await Task.Delay(150); c = await ManagedSoundpad.Read(real); } return c; }
    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb", "managed-audio", "removal-test");
    Directory.CreateDirectory(folder);
    var file = Path.Combine(folder, "soundpad-web-removal-test.wav");
    File.WriteAllBytes(file, MimicRing.Wav(new float[24000], 48000));
    const string name = "Soundpad Web removal test";
    var before = await ManagedSoundpad.Read(real);
    Console.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
    if (before.Categories.Any(c => c.Name == name) || before.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) { Console.WriteLine("ABORT: leftovers from an earlier run"); return; }
    Console.WriteLine("add category: " + await real.Request($"DoAddCategory(\"{name}\", -1)"));
    var withCategory = await Settle(c => c.Categories.Any(x => x.Name == name));
    int index = withCategory.Categories.Single(c => c.Name == name).Index;
    Console.WriteLine("add sound: " + await real.Request($"DoAddSound(\"{file}\", {index}, 1)"));
    var withSound = await Settle(c => c.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase)));
    Console.WriteLine($"sound in main list: {withSound.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase))}; in its category: {withSound.Categories.Single(c => c.Name == name).Paths.Contains(file, StringComparer.OrdinalIgnoreCase)}");
    Console.WriteLine("remove category: " + await real.Request($"DoRemoveCategory({index})"));
    var after = await Settle(c => !c.Categories.Any(x => x.Name == name));
    bool left = after.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"RESULT: after removing the category, the sound is {(left ? "STILL in" : "GONE from")} the main list ({after.Entries.Length} sounds, {after.Categories.Length} categories)");
    if (left)
    {
        // Put it back into a fresh category of its own and remove it there by selection: the category holds only it.
        Console.WriteLine("cleanup add category: " + await real.Request($"DoAddCategory(\"{name}\", -1)"));
        var again = await Settle(c => c.Categories.Any(x => x.Name == name)); int cleanup = again.Categories.Single(c => c.Name == name).Index;
        Console.WriteLine("cleanup add (existing path): " + await real.Request($"DoAddSound(\"{file}\", {cleanup}, 1)"));
        await Settle(c => c.Categories.Single(x => x.Name == name).Paths.Length > 0);
        Console.WriteLine("select category: " + await real.Request($"DoSelectCategory({cleanup})"));
        Console.WriteLine("select row 1: " + await real.Request("DoSelectIndex(1)"));
        Console.WriteLine("remove selected (not on disk): " + await real.Request("DoRemoveSelectedEntries(false)"));
        var cleaned = await Settle(c => !c.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase)));
        Console.WriteLine($"after remove-selected: sound in main list: {cleaned.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase))}");
        Console.WriteLine("remove cleanup category: " + await real.Request($"DoRemoveCategory({cleanup})"));
    }
    var final = await Settle(c => !c.Categories.Any(x => x.Name == name));
    Console.WriteLine($"final: {final.Entries.Length} sounds, {final.Categories.Length} categories (before: {before.Entries.Length}, {before.Categories.Length}); same paths: {final.Entries.Select(e => e.Path).OrderBy(p => p).SequenceEqual(before.Entries.Select(e => e.Path).OrderBy(p => p))}");
    try { File.Delete(file); Directory.Delete(folder); } catch (IOException) { Console.WriteLine("test file still in use: " + file); }
    return;
}
if (args.FirstOrDefault() == "--check-real-temp-category")
{
    // Owner check (2.15.9): on the real Soundpad, creates or finds the companion's Temp category the way a reverse does,
    // twice, and shows the name Soundpad stored. It must be created at most once and match exactly.
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb");
    using var realPipe = new SoundpadPipe();
    var store = new ManagedAudioStore(data, realPipe, path => path, work => work()); // native ids aren't used by this check
    int first = await store.EnsureTempCategory(), second = await store.EnsureTempCategory();
    var catalog = await ManagedSoundpad.Read(realPipe);
    var matches = catalog.Categories.Where(c => c.Name.Contains("Soundpad Web")).ToArray();
    Console.WriteLine($"journal name: {store.Public.TempCategoryName}; first index {first}, second index {second}");
    foreach (var c in matches) Console.WriteLine($"  Soundpad has: index {c.Index} name [{c.Name}] exact match: {c.Name == store.Public.TempCategoryName}");
    Console.WriteLine(matches.Length == 1 && first == second && matches[0].Name == store.Public.TempCategoryName ? "RESULT: one correct Temp category" : "RESULT: PROBLEM");
    return;
}
if (args.FirstOrDefault() == "--vrs-library")
{
    // Reads this PC's Voice Replay Studio library the way the companion does, read-only. Prints counts only.
    var library = new VoiceReplayLibrary();
    var clips = library.List();
    Console.WriteLine($"library.json: {(library.Exists ? "found" : "not found")}; clips with audio: {clips.Count}; total {clips.Sum(c => c.DurationMs) / 1000.0:0.0} s");
    return;
}
if (args.FirstOrDefault() == "--hotkey-probe")
{
    // Registers and releases each hotkey the host can pick with Windows; reports any another app already owns. Presses nothing.
    foreach (var name in Bridge.HotkeyKeys)
    {
        using var key = Bridge.WindowsKey(name);
        try { key.Register(() => { }); Console.WriteLine($"{name} registered: " + key.Active); }
        catch (InvalidDataException ex) { Console.WriteLine(ex.Message); }
        key.Unregister(); Console.WriteLine($"{name} released: " + !key.Active);
    }
    return;
}
if (args.FirstOrDefault() == "--mimic-silent-selftest" && args.Length == 2)
{
    // Full real-capture chain without sound: this process plays a WAV with its stream volume at 0 (set before
    // Play) while Mimic's real app-loopback capture listens to this process, and the real Whisper engine
    // transcribes the result. If Windows taps app audio after the stream volume, the capture is silent and the
    // test reports that instead of passing.
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb");
    using var capture = new WasapiMimicCapture();
    var (store, words, folder) = HistorySinkFixture.Temporary(capture.Rate);
    await capture.Start(new MimicSource("app:" + Environment.ProcessId, "App: this test", "app"), new HistorySinkFixture(store));
    using var transcriber = new MimicTranscriber(new WhisperEngine(Path.Combine(data, "models", "ggml-base.en.bin"), null, Path.Combine(data, "native", "whisper-1.9.1")));
    transcriber.Attach(store, words);
    await transcriber.Enable(() => { capture.Pump(); return store; }, false);
    using var file = new NAudio.Wave.WaveFileReader(args[1]);
#pragma warning disable CS0618 // WasapiOut's stream Volume is the simplest guaranteed-silent player for this diagnostic
    using var output = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
#pragma warning restore CS0618
    output.Init(file);
    output.Volume = 0f;
    if (output.Volume != 0f) { Console.WriteLine("Refusing to play: stream volume did not reach 0."); return; }
    output.Play();
    while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing) await Task.Delay(100);
    await Task.Delay(4000); // let the pause after the speech trigger a transcript chunk
    capture.Pump(); long end = store.LiveFrame;
    var pairs = store.Levels(HistoryAccess.Host, Math.Max(0, end - (long)store.Rate * 20), Math.Max(1, end), 40);
    int peak = pairs.Select(v => Math.Abs((int)v)).DefaultIfEmpty(0).Max() * 1000 / short.MaxValue;
    Console.WriteLine($"Capture: {capture.Status}, {capture.Packets} packets, peak level {peak} / 1000");
    var lines = transcriber.Lines(store, HistoryAccess.Host, Math.Max(0, end - (long)store.Rate * 30), end);
    foreach (var line in lines) Console.WriteLine($"[{line.StartMs / 1000.0:0.00} s - {line.EndMs / 1000.0:0.00} s] {line.Text}");
    Console.WriteLine(lines.Length > 0 ? "RESULT: real capture + transcript chain works" : peak == 0 ? "RESULT: inconclusive (Windows captured the muted stream as silence)" : "RESULT: audio captured but no transcript");
    capture.Stop(); transcriber.Disable(); HistorySinkFixture.Delete(store, folder);
    return;
}
if (args.FirstOrDefault() == "--whisper-bench" && args.Length == 2)
{
    // Times warm transcription of a WAV with 1, 2 and 4 CPU threads to choose a light default for gaming hosts.
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb");
    using var file = new NAudio.Wave.WaveFileReader(args[1]);
    var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(file);
    var mono = new List<float>(); var frame = new float[provider.WaveFormat.Channels * 4096]; int read;
    while ((read = provider.Read(frame)) > 0)
        for (int i = 0; i + provider.WaveFormat.Channels <= read; i += provider.WaveFormat.Channels) mono.Add(frame.AsSpan(i, provider.WaveFormat.Channels).ToArray().Average());
    var speech = MimicTranscriber.Resample(mono.ToArray(), provider.WaveFormat.SampleRate, MimicTranscriber.SpeechRate);
    double seconds = speech.Length / (double)MimicTranscriber.SpeechRate;
    foreach (var threads in new[] { 1, 2, 4 })
    {
        using var engine = new WhisperEngine(Path.Combine(data, "models", "ggml-base.en.bin"), null, Path.Combine(data, "native", "whisper-1.9.1"), threads);
        await engine.Transcribe(speech, default); // warm-up: loads the model
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var text = string.Join(" ", (await engine.Transcribe(speech, default)).Select(s => s.Text.Trim()));
        var used = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu;
        Console.WriteLine($"{threads} thread(s): {clock.Elapsed.TotalSeconds:0.00} s wall, {used.TotalSeconds:0.00} s CPU for {seconds:0.0} s of speech ({seconds / clock.Elapsed.TotalSeconds:0.0}x real time) | {text}");
    }
    return;
}
if (args.FirstOrDefault() == "--peek-bench")
{
    // Cost of one Mimic peek (30 s window at 48 kHz, 300 buckets), the work each guest refresh triggers.
    var ring = new MimicRing(48000, 45);
    ring.Write(Enumerable.Range(0, 48000 * 45).Select(i => MathF.Sin(i * 0.01f)).ToArray());
    for (int i = 0; i < 20; i++) ring.Levels(ring.Written, 48000L * 30, 300);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 200; i++) ring.Levels(ring.Written, 48000L * 30, 300);
    var each = clock.Elapsed.TotalMilliseconds / 200;
    Console.WriteLine($"One peek: {each:0.00} ms. 12 guests at the 12/s limit = {each * 144 / 10:0.0}% of a core.");
    // The bridge shares one computation per 100 ms between all viewers, so the worst case is 10 per second.
    Console.WriteLine($"With the shared 100 ms result: at most {each * 10 / 10:0.00}% of a core, however many guests.");
    return;
}
if (args.FirstOrDefault() == "--make-rollback-fixture" && args.Length == 3)
{
    // Writes a real data folder with every kind of data newer versions create (Mimic clips, a trimmed and an
    // effect copy, the guestMimic permission, a mid-session journal), so an older companion can be started on
    // it to prove rolling back works. args[1] = data folder, args[2] = "open" (leave the session open) or "ended".
    var dir = args[1];
    Directory.CreateDirectory(dir);
    await using var soundpad = new MockSoundpad();
    var all2 = System.Xml.Linq.XDocument.Parse(MockSoundpad.Sounds);
    foreach (var element in all2.Descendants("Sound")) { var original = (string?)element.Attribute("url"); if (original == null) continue; var path = Path.Combine(dir, "fixture-" + Path.GetFileName(original)); if (!File.Exists(path)) AudioFixtures.Wave(path); element.SetAttributeValue("url", path); }
    soundpad.AllXml = all2.ToString();
    using var fixturePipe = new SoundpadPipe(soundpad.Name, true);
    using var b = new Bridge(fixturePipe, new StateStore(dir));
    var capture = new MimicFixture(); b.UseMimicCapture(capture); b.UseTranscriptEngine(new TranscriptFixture()); b.UseHotkey(new HotkeyFixture());
    await b.Poll(true);
    Command C(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    async Task Must(Task<Reply> reply, string what) { var r = await reply; if (!r.Ok) throw new InvalidOperationException(what + ": " + r.Error); }
    await Must(b.Execute(C("beginSession") with { AudienceId = Guid.NewGuid().ToString() }, "host"), "begin");
    await Must(b.Execute(C("permissions") with { Permissions = new(GuestMimic: true) }, "host"), "permissions");
    await Must(b.Execute(C("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"), "start");
    capture.Advance(TimeSpan.FromSeconds(8));
    var peekReply = await b.Execute(C("mimicPeek"), "host");
    var peek = System.Text.Json.JsonSerializer.Deserialize<MimicPeek>(System.Text.Json.JsonSerializer.Serialize(peekReply.Data, Json.Options), Json.Options)!;
    await Must(b.Execute(C("mimicClip") with { MimicEnd = peek.End, PositionMs = 22000, EndMs = 30000, Name = "Rollback clip", SourceId = peek.RingId }, "host"), "clip");
    var journalPath = Path.Combine(dir, "managed-audio", "journal.json");
    var asset = System.Text.Json.JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(journalPath), Json.Options)!.Assets.Single();
    await Must(b.Execute(C("trimAudio") with { AssetId = asset.Id, PositionMs = 500, EndMs = 4000 }, "host"), "trim");
    await Must(b.Execute(C("trimAudio") with { AssetId = asset.Id, PositionMs = 0, EndMs = 8000, Action = "echo" }, "host"), "effect");
    if (args[2] == "ended") await Must(b.Execute(C("endSession"), "host"), "end");
    else b.Dispose();
    Console.WriteLine($"FIXTURE_READY {dir} pipe={soundpad.Name}");
    Console.WriteLine(File.ReadAllText(Path.Combine(dir, "state.json")).Contains("guestMimic") ? "state.json has guestMimic" : "state.json lacks guestMimic");
    Console.WriteLine("journal assets: " + System.Text.Json.JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(journalPath), Json.Options)!.Assets.Count);
    return;
}
if (args.FirstOrDefault() == "--capture-bench")
{
    // CPU cost of Mimic capture on a real source: listen for 20 s and report process CPU time and allocations.
    // It captures this harness's own (silent) process, or a named source, so no sound is made.
    using var capture = new WasapiMimicCapture();
    var sources = capture.Sources();
    var pick = args.Length > 1 ? sources.First(s => s.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)) : new MimicSource("app:" + Environment.ProcessId, "App: this bench", "app");
#pragma warning disable CS0618 // WasapiOut with stream volume 0: a silent signal so packets keep flowing
    using var output = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
    var tone = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.3 };
    output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(tone)); output.Volume = 0f; if (args.Length == 1) output.Play();
#pragma warning restore CS0618
    await capture.Start(pick, new MimicRing(capture.Rate, 45));
    await Task.Delay(2000);
    var proc = System.Diagnostics.Process.GetCurrentProcess();
    var cpu0 = proc.TotalProcessorTime; var alloc0 = GC.GetTotalAllocatedBytes(); var gen0 = GC.CollectionCount(0);
    var packets0 = capture.Packets; var clock = System.Diagnostics.Stopwatch.StartNew();
    await Task.Delay(20000);
    proc.Refresh();
    double cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds, wall = clock.Elapsed.TotalMilliseconds;
    Console.WriteLine($"{pick.Name}: {capture.Packets - packets0} packets in {wall / 1000:0} s, CPU {cpu:0} ms = {cpu / wall * 100:0.00}% of one core, allocated {(GC.GetTotalAllocatedBytes() - alloc0) / 1048576.0:0.0} MiB, gen0 GCs {GC.CollectionCount(0) - gen0}");
    output.Stop();
    return;
}
if (args.FirstOrDefault() == "--analyze-file" && args.Length == 2)
{
    var scratch = Path.Combine(Path.GetTempPath(), "soundpad-web-analysis-" + Guid.NewGuid().ToString("N"));
    try
    {
        using var waves = new Waveforms(scratch); var clock = System.Diagnostics.Stopwatch.StartNew();
        var wave = await waves.Request(new('a', 64), args[1]); var coldMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
        await waves.Request(new('a', 64), args[1]);
        Console.WriteLine(Json.Write(new { format = Path.GetExtension(args[1]), wave.Status, wave.DurationMs, bins = wave.Levels?.Select(l => l.Bins), coldMs, cachedMs = clock.Elapsed.TotalMilliseconds, waves.DecodeCount }));
    }
    finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    return;
}
var directory = Path.Combine(Path.GetTempPath(), "soundpad-web-e2e-" + Guid.NewGuid().ToString("N"));
await using var mock = new MockSoundpad();
Directory.CreateDirectory(directory);
var all = System.Xml.Linq.XDocument.Parse(MockSoundpad.Sounds);
var categories = System.Xml.Linq.XDocument.Parse(MockSoundpad.Categories);
if (args.Length > 1 && int.TryParse(args[1], out var count) && count is > 4 and <= 50000)
{
    var template = all.Root!.Elements("Sound").First();
    for (int i = 5; i <= count; i++) { var item = new System.Xml.Linq.XElement(template); item.SetAttributeValue("index", i); item.SetAttributeValue("title", $"Benchmark fixture {i:D5}"); all.Root.Add(item); }
}
foreach (var element in all.Descendants("Sound").Concat(categories.Descendants("Sound"))) { var original = (string?)element.Attribute("url"); if (original == null) continue; var path = Path.Combine(directory, Path.GetFileName(original)); if (!File.Exists(path)) AudioFixtures.Wave(path); element.SetAttributeValue("url", path); element.SetAttributeValue("duration", "00:02.500"); }
mock.AllXml = all.ToString(); mock.CategoryXml = categories.ToString();
// After the origin and the sound count (2.35), for e2e tests of playback under delay: --latency-ms N makes every
// Soundpad request take N ms; --slow-add-ms N makes only an import into Soundpad (DoAddSound) take N ms longer.
int Option(string name) { int at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int value) && value is >= 0 and <= 3000 ? value : 0; }
mock.LatencyMs = Option("--latency-ms"); mock.SlowMs = Option("--slow-add-ms"); mock.SlowPrefix = "DoAddSound(";
using var pipe = new SoundpadPipe(mock.Name, true);
using var bridge = new Bridge(pipe, new StateStore(directory));
// The tone has been playing for 10 s when Mimic starts listening, so the browser tests' "Last 3 s" and "Last 10 s"
// right after Start find that much recorded (2.39: the history view selects only audio that exists).
bridge.UseMimicCapture(new MimicFixture { PrefillSeconds = 10 });
bridge.UseTranscriptEngine(new TranscriptFixture());
bridge.UseHotkeys(key => new HotkeyFixture(key) { Taken = key == "F9" }); // F9 plays a key another app owns
bridge.UseVoiceReplayLibrary(VoiceReplayFixture.Create(Path.Combine(directory, "voice-replay"))); // never the real library
// Exact cue starts and regions (2.37) only with --exact-slices: the browser tests check the play-then-seek commands
// Soundpad receives for a cue start (DoPlaySound(n), DoSeekMs(t)), which slices replace with DoPlaySound(slot).
bridge.UseExactSlices(args.Contains("--exact-slices"));
await bridge.Poll(true);
// --owner-key <64 hex> (2.36) stands in for the Soundpad Web window's key, so e2e tests can open a page that takes
// hosting over. Without it there is no window: the first protocol-3 page becomes the session owner, as in --no-window.
int ownerAt = Array.IndexOf(args, "--owner-key");
var ownerKey = ownerAt >= 0 && ownerAt + 1 < args.Length && System.Text.RegularExpressions.Regex.IsMatch(args[ownerAt + 1], "^[a-f0-9]{64}$") ? args[ownerAt + 1] : null;
await using var server = new LocalServer(bridge, args.FirstOrDefault() ?? "http://127.0.0.1:5173", ownerKey) { Log = line => Console.WriteLine("TEST_LOG " + line) };
await server.Start();
Console.WriteLine("TEST_READY " + Json.Write(new { port = server.Port, token = server.Token }));
using var stop = new CancellationTokenSource();
// Any line ends the harness, except "restart-history" (2.41): the recording history is replaced by a new one at once
// (the same as the companion restarting Mimic), so browser tests can check that nothing of the old one carries over.
_ = Task.Run(() =>
{
    while (Console.ReadLine() == "restart-history")
    {
        bridge.CloseHistory();
        bridge.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "mimicStart") with { SourceId = MimicFixture.Source.Id }, "host").GetAwaiter().GetResult();
    }
    stop.Cancel();
});
try { while (!stop.IsCancellationRequested) { await Task.Delay(250, stop.Token); await bridge.Poll(false); } }
catch (OperationCanceledException) { }
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
