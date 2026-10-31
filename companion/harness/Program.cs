using SoundpadWeb;
using SoundpadWeb.Testing;
if (args.FirstOrDefault() == "--mock-only")
{
    await using var isolated = new MockSoundpad(); isolated.DurationMs = 600000;
    Console.WriteLine("TEST_PIPE " + isolated.Name);
    while (Console.ReadLine() is string line) { if (line == "play") isolated.BeginFixturePlayback(); else if (line == "exit") break; }
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
    await capture.Start(pick, 5); await Task.Delay(2000);
    var ring = capture.Ring!; var levels = ring.Levels(ring.Written, ring.Rate * 2L, 20);
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
    var ring = new MimicRing(48000, 45);
    ring.WriteSilence(48000);
    await transcriber.Enable(() => ring, false);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (int offset = 0; offset < audio.Length; offset += 480)
    {
        ring.Write(audio.AsSpan(offset, Math.Min(480, audio.Length - offset)));
        var due = TimeSpan.FromSeconds((offset + 480) / 48000.0) - clock.Elapsed;
        if (due > TimeSpan.Zero) await Task.Delay(due);
    }
    for (int i = 0; i < 30; i++) { ring.WriteSilence(4800); await Task.Delay(100); } // 3 s of trailing silence, like a pause
    long end = ring.Written;
    foreach (var line in transcriber.Lines(ring, end - 48000L * 30, end)) Console.WriteLine($"[{line.StartMs / 1000.0:0.00} s - {line.EndMs / 1000.0:0.00} s] {line.Text}");
    Console.WriteLine($"Speech starts at {(end - 48000L * 30 < 48000 ? (48000 - (end - 48000L * 30)) / 48.0 : 0):0} ms into the window; streamed {audio.Length / 48000.0:0.0} s in {clock.Elapsed.TotalSeconds:0.0} s.");
    return;
}
if (args.FirstOrDefault() == "--hotkey-probe")
{
    // Registers and releases F6 with Windows; reports whether another app already owns it. Presses nothing.
    using var key = new WindowsHotkey();
    try { key.Register(() => { }); Console.WriteLine("F6 registered: " + key.Active); }
    catch (InvalidDataException ex) { Console.WriteLine(ex.Message); }
    key.Unregister(); Console.WriteLine("F6 released: " + !key.Active);
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
    await capture.Start(new MimicSource("app:" + Environment.ProcessId, "App: this test", "app"), 45);
    using var transcriber = new MimicTranscriber(new WhisperEngine(Path.Combine(data, "models", "ggml-base.en.bin"), null, Path.Combine(data, "native", "whisper-1.9.1")));
    await transcriber.Enable(() => capture.Ring, false);
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
    var ring = capture.Ring!; long end = ring.Written;
    var levels = ring.Levels(end, (long)ring.Rate * 20, 40);
    Console.WriteLine($"Capture: {capture.Status}, {capture.Packets} packets, peak level {levels.Max()} / 1000");
    var lines = transcriber.Lines(ring, end - (long)ring.Rate * 30, end);
    foreach (var line in lines) Console.WriteLine($"[{line.StartMs / 1000.0:0.00} s - {line.EndMs / 1000.0:0.00} s] {line.Text}");
    Console.WriteLine(lines.Length > 0 ? "RESULT: real capture + transcript chain works" : levels.Max() == 0 ? "RESULT: inconclusive (Windows captured the muted stream as silence)" : "RESULT: audio captured but no transcript");
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
using var pipe = new SoundpadPipe(mock.Name, true);
using var bridge = new Bridge(pipe, new StateStore(directory));
bridge.UseMimicCapture(new MimicFixture());
bridge.UseTranscriptEngine(new TranscriptFixture());
bridge.UseHotkey(new HotkeyFixture());
await bridge.Poll(true);
await using var server = new LocalServer(bridge, args.FirstOrDefault() ?? "http://127.0.0.1:5173");
await server.Start();
Console.WriteLine("TEST_READY " + Json.Write(new { port = server.Port, token = server.Token }));
using var stop = new CancellationTokenSource();
_ = Task.Run(() => { Console.ReadLine(); stop.Cancel(); });
try { while (!stop.IsCancellationRequested) { await Task.Delay(250, stop.Token); await bridge.Poll(false); } }
catch (OperationCanceledException) { }
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
