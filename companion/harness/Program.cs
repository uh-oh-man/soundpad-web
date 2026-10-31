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
await bridge.Poll(true);
await using var server = new LocalServer(bridge, args.FirstOrDefault() ?? "http://127.0.0.1:5173");
await server.Start();
Console.WriteLine("TEST_READY " + Json.Write(new { port = server.Port, token = server.Token }));
using var stop = new CancellationTokenSource();
_ = Task.Run(() => { Console.ReadLine(); stop.Cancel(); });
try { while (!stop.IsCancellationRequested) { await Task.Delay(250, stop.Token); await bridge.Poll(false); } }
catch (OperationCanceledException) { }
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
