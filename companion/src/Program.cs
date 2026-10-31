using System.Diagnostics;
using SoundpadWeb;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Soundpad Web requires Windows 11 x64."); return; }
if (args.Contains("--licenses"))
{
    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains("Licenses"))) { using var stream = assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream); Console.WriteLine(reader.ReadToEnd()); }
    return;
}
if (args.FirstOrDefault() == "--transcribe-file" && args.Length >= 2)
{
    // Diagnostic: transcribe a local audio file with Mimic's engine. --download fetches the model first if missing.
    if (args.Contains("--verbose")) Whisper.net.Logger.LogProvider.AddConsoleLogging(Whisper.net.Logger.WhisperLogLevel.Debug);
    var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb", "models");
    using var engine = new WhisperEngine(Path.Combine(models, "ggml-base.en.bin"), null, Path.Combine(Path.GetDirectoryName(models)!, "native", "whisper-1.9.1"));
    try
    {
        if (!engine.Available && args.Contains("--download")) { Console.WriteLine("Downloading the speech model…"); await engine.Prepare(default); }
        using var file = new NAudio.Wave.WaveFileReader(args[1]);
        var reader = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(file);
        var all = new List<float>(); var frame = new float[reader.WaveFormat.Channels * 4096]; int read;
        while ((read = reader.Read(frame)) > 0)
            for (int i = 0; i + reader.WaveFormat.Channels <= read; i += reader.WaveFormat.Channels) all.Add(frame.AsSpan(i, reader.WaveFormat.Channels).ToArray().Average());
        var clock = Stopwatch.StartNew();
        foreach (var span in await engine.Transcribe(MimicTranscriber.Resample(all.ToArray(), reader.WaveFormat.SampleRate, MimicTranscriber.SpeechRate), default))
            Console.WriteLine($"[{span.Start.TotalSeconds:0.0} s - {span.End.TotalSeconds:0.0} s] {span.Text.Trim()}");
        Console.WriteLine($"Transcribed {all.Count / (double)reader.WaveFormat.SampleRate:0.0} s of audio in {clock.Elapsed.TotalSeconds:0.0} s.");
    }
    catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine("Transcription failed: " + ex.Message); Environment.ExitCode = 1; }
    return;
}
try
{
    string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    var directory = Arg("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb");
    Directory.CreateDirectory(directory);
    using var instanceLock = new FileStream(Path.Combine(directory, "companion.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var pipe = new SoundpadPipe(Arg("--pipe") ?? "sp_remote_control");
    using var bridge = new Bridge(pipe, new StateStore(directory));
    var builtInWebsite = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "WebsiteUrl")?.Value;
    var site = Arg("--site") ?? bridge.Website ?? Environment.GetEnvironmentVariable("SOUNDPAD_WEB_URL") ?? builtInWebsite;
    if (string.IsNullOrWhiteSpace(site))
    {
        Console.WriteLine("Soundpad Web — first launch\nPaste your published HTTPS website URL (saved for future launches):");
        site = Console.ReadLine();
    }
    if (!Uri.TryCreate(site, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(args.Contains("--dev") && uri.IsLoopback && uri.Scheme == "http")) || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Use an HTTPS website URL. Developers may use --dev with an HTTP loopback URL.");
    var clean = new UriBuilder(uri) { Fragment = "", Query = "" }.Uri;
    bridge.SetWebsite(clean.ToString());
    await using var server = new LocalServer(bridge, clean.GetLeftPart(UriPartial.Authority));
    await server.Start();
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    _ = Task.Run(() => { if (Console.ReadLine() != null) shutdown.Cancel(); });
    try { await bridge.CleanupSession(); } catch (Exception) { Console.WriteLine("Temporary audio cleanup will retry when Soundpad is available."); }
    var url = clean + "#host=" + server.Port + "." + server.Token;
    if (!args.Contains("--no-browser")) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    Console.WriteLine($"Soundpad Web is running on loopback port {server.Port}.\nKeep this window and your host browser tab open. Enter or Ctrl+C stops the companion.\nAudio devices and Soundpad settings are unchanged.");
    long nextLibrary = 0;
    using var wake = new SemaphoreSlim(0, 1);
    bridge.CommandAccepted += () => { if (wake.CurrentCount == 0) wake.Release(); };
    try { while (!shutdown.IsCancellationRequested)
    {
        var now = Environment.TickCount64;
        bool refresh = now >= nextLibrary;
        if (refresh) nextLibrary = now + 30000;
        await bridge.Poll(refresh);
        await wake.WaitAsync(TimeSpan.FromMilliseconds(bridge.Snapshot.Playback.Status is "PLAYING" or "SEEKING" ? 250 : bridge.Snapshot.Playback.Status == "PAUSED" ? 500 : 1500), shutdown.Token);
    } } finally { try { await bridge.CleanupSession(); } catch (Exception) { Console.WriteLine("Temporary entries retained for cleanup on next launch."); } }
}
catch (OperationCanceledException) { }
catch (Exception ex) { Console.Error.WriteLine("Soundpad Web: " + ex.Message); Environment.ExitCode = 1; }
