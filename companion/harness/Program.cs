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
await bridge.Poll(true);
await using var server = new LocalServer(bridge, args.FirstOrDefault() ?? "http://127.0.0.1:5173");
await server.Start();
Console.WriteLine("TEST_READY " + Json.Write(new { port = server.Port, token = server.Token }));
using var stop = new CancellationTokenSource();
_ = Task.Run(() => { Console.ReadLine(); stop.Cancel(); });
try { while (!stop.IsCancellationRequested) { await Task.Delay(250, stop.Token); await bridge.Poll(false); } }
catch (OperationCanceledException) { }
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
