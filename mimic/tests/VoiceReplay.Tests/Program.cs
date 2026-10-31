using VoiceReplay.Core;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;

if (args.Contains("--playback-diagnostic"))
{
    var store = new Store();
    Console.WriteLine("Saved monitor: " + (string.IsNullOrEmpty(store.Settings.MonitorDeviceId) ? "NONE" : store.Settings.MonitorDeviceId));
    foreach (var device in MonitorOutputs.List())
        Console.WriteLine($"OUTPUT {device.Id} | {device.Label}");
    foreach (var c in store.Snapshot().Take(5))
    {
        var audio = AudioFiles.Read(c.AudioPath);
        Console.WriteLine($"WAV {Path.GetFileName(c.AudioPath)} | {audio.Rate}Hz | {audio.Samples.Length} samples | peak={audio.Samples.Max(x => Math.Abs(x)):F4} | duration={audio.Samples.Length / (double)audio.Rate:F3}s");
    }
    return 0;
}

if (args.Contains("--soundpad-diagnostic"))
{
    using (var pipe = new NamedPipeClientStream(".", "sp_remote_control", PipeDirection.InOut, PipeOptions.Asynchronous))
    {
        await pipe.ConnectAsync(2000);
        NativePipe.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid);
        Console.WriteLine("Pipe server process: " + pid + " / " + System.Diagnostics.Process.GetProcessById((int)pid).ProcessName);
    }
    var transport = new PipeTransport();
    var service = new SoundpadService(transport);
    Console.WriteLine("Installed/detected: " + SoundpadService.Installed());
    Console.WriteLine("Reachable: " + await service.Probe());
    Console.WriteLine(service.Status);
    if (!service.Connected)
        return 2;
    Console.WriteLine("Soundpad version: " + await transport.Request("GetVersion()"));
    Console.WriteLine("RC version: " + await transport.Request("GetRemoteControlVersion()"));
    Console.WriteLine("Trial: " + await transport.Request("IsTrial()"));
    var list = XDocument.Parse(await transport.Request("GetSoundlist()"));
    Console.WriteLine("Sound count: " + list.Descendants("Sound").Count());
    var categories = XDocument.Parse(await transport.Request("GetCategories(false,false)"));
    Console.WriteLine("Category count: " + categories.Descendants("Category").Count());
    return 0;
}
if (args.Length == 2 && args[0] == "--soundpad-import-fixture")
{
    var store = new Store();
    var transport = new PipeTransport();
    var soundpad = new SoundpadService(transport);
    if (!await soundpad.Probe())
    {
        Console.WriteLine(soundpad.Status);
        return 2;
    }
    var before = XDocument.Parse(await transport.Request("GetSoundlist()"));
    var beforePaths = before.Descendants("Sound").Select(x => (string?)x.Attribute("url")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var audio = AudioFiles.Read(args[1]);
    Clip clip;
    await using (var pipeline = new ClipPipeline(store, new LocalTranscriber(store), soundpad))
    {
        clip = store.Snapshot().FirstOrDefault(c => c.Source == "JFK reference speech — integration diagnostic" && c.ImportedPath != c.AudioPath)!;
        if (clip is null)
            clip = await pipeline.SaveCapture(audio.Samples, audio.Rate, "JFK reference speech — integration diagnostic");
        else
        {
            pipeline.RenameFile(clip);
            store.Save();
            pipeline.Enqueue(clip, false, true);
        }
        await Wait(() => !pipeline.Busy(clip));
        Console.WriteLine("Clip ID: " + clip.Id);
        Console.WriteLine("Duration: " + clip.Duration);
        Console.WriteLine("Transcript: " + clip.Transcript);
        Console.WriteLine("Import state: " + clip.ImportState);
        Console.WriteLine("Soundpad index: " + clip.SoundpadIndex);
        Console.WriteLine("Error: " + clip.Error);
        if (clip.ImportedPath != clip.AudioPath)
            return 3;
        string index = await soundpad.Import(clip.AudioPath);
        Console.WriteLine("Repeat import resolved same association: " + (index == clip.SoundpadIndex));
    }
    var after = XDocument.Parse(await transport.Request("GetSoundlist()"));
    var afterPaths = after.Descendants("Sound").Select(x => (string?)x.Attribute("url")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    bool originalPreserved = beforePaths.IsSubsetOf(afterPaths), verified = SoundpadService.FindSound(after.ToString(), clip.AudioPath) is not null;
    Console.WriteLine($"Before sounds: {before.Descendants("Sound").Count()}; after: {after.Descendants("Sound").Count()}; existing paths preserved: {originalPreserved}; exact new path verified: {verified}");
    Console.WriteLine("Library persisted after reopen: " + new Store().Clips.Any(c => c.Id == clip.Id && c.ImportedPath == clip.AudioPath));
    return originalPreserved && verified && after.Descendants("Sound").Count() == before.Descendants("Sound").Count() + 1 ? 0 : 4;
}
int passed = 0, failed = 0;
var root = Path.Combine(Path.GetTempPath(), "VoiceReplayTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Test(string name, Action action) { try { action(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); failed++; } }
void Assert(bool condition, string reason = "Assertion failed") { if (!condition) throw new Exception(reason); }
async Task AsyncTest(string name, Func<Task> action) { try { await action(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); failed++; } }
Test("Ring wraparound preserves most recent samples", () => { var r = new RingBuffer(10, 3); r.Write(Enumerable.Range(0, 40).Select(x => x / 40f).ToArray()); Assert(r.Snapshot(1).SequenceEqual(Enumerable.Range(30, 10).Select(x => x / 40f))); Assert(r.Count == 30); });
Test("Exact duration and warmup", () => { var r = new RingBuffer(48000, 30); r.Write(new float[48000 * 5]); Assert(r.Snapshot(3).Length == 144000); Assert(r.Snapshot(10).Length == 240000); });
Test("Missing audio refuses snapshot", () => { try { new RingBuffer(10, 3).Snapshot(1); throw new Exception("No error"); } catch (InvalidOperationException) { } });
Test("Repeated concurrent snapshots", () => { var r = new RingBuffer(48000, 30); r.Write(Enumerable.Repeat(.25f, 48000 * 30).ToArray()); Parallel.For(0, 100, _ => Assert(r.Snapshot(3).Length == 144000)); });
Test("Silence detection", () => { Assert(AudioFiles.Silent(new float[200])); Assert(!AudioFiles.Silent([.1f])); });
Test("Stereo PCM conversion", () => { byte[] b = [0, 64, 0, 0, 0, 128, 0, 0]; var f = AudioFiles.Decode(b, b.Length, new WaveFormat(48000, 16, 2)); Assert(Math.Abs(f[0] - .25) < .001); Assert(Math.Abs(f[1] + .5) < .001); });
Test("Stereo IEEE float conversion", () => { float[] data = [.75f, -.25f, -.5f, .25f]; var bytes = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length); var output = AudioFiles.Decode(bytes, bytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)); Assert(output.SequenceEqual(new[] { .25f, -.125f })); });
var sine = Enumerable.Range(0, 48000).Select(x => (float)(.4 * Math.Sin(2 * Math.PI * 440 * x / 48000))).ToArray();
Test("Atomic WAV integrity and sample fidelity", () => { string p = Path.Combine(root, "sine.wav"); AudioFiles.Write(p, sine, 48000); var f = AudioFiles.Read(p); Assert(f.Rate == 48000 && f.Samples.Length == 48000); Assert(f.Samples.Zip(sine).All(x => Math.Abs(x.First - x.Second) < .0001)); Assert(!Directory.GetFiles(root, "*.partial").Any()); });
Test("WAV collision preserves previous audio and cleans partial file", () => { string path = Path.Combine(root, "sine.wav"); try { AudioFiles.Write(path, [.25f], 48000); throw new Exception("Overwrote existing WAV"); } catch (IOException) { } Assert(AudioFiles.Read(path).Samples.Length == 48000); Assert(!Directory.GetFiles(root, "*.partial").Any()); });
Test("Trim exact duration and boundary", () => { var t = AudioFiles.Trim(sine, 48000, .2, .7); Assert(t.Length == 24000); Assert(t[0] == sine[9600]); });
Test("Playback selection contains only requested audio", () => { var t = AudioFiles.Trim(sine, 48000, .123, .456); Assert(t.SequenceEqual(sine[5904..21888])); try { AudioFiles.Trim(sine, 48000, .8, .2); throw new Exception("Reverse selection accepted"); } catch (ArgumentException) { } });
Test("Waveform extrema", () => { var peaks = AudioFiles.Peaks([-.5f, .7f, -.2f, .1f], 2); Assert(peaks[0] == (-.5f, .7f) && peaks[1] == (-.2f, .1f)); });
Test("Resampling duration", () => { var s = AudioFiles.Resample(sine, 48000, 16000); Assert(Math.Abs(s.Length - 16000) < 20, $"Length {s.Length}"); });
Test("Filename sanitation and reserved names", () => { Assert(Names.Safe("WHAT THE HELL WAS THAT?") == "WHAT_THE_HELL_WAS_THAT"); Assert(Names.Safe("CON") == "CLIP_CON"); Assert(Names.Safe(new string('a', 200)).Length == 80); });
Test("Duplicate titles have unique paths", () => { Assert(Names.FileName(new Clip { Title = "hello" }) != Names.FileName(new Clip { Title = "hello" })); });
Test("Library restart and settings persistence", () => { var s = new Store(Path.Combine(root, "store")); s.Settings.CaptureSeconds = 15; s.Add(new Clip { Title = "Saved", OriginalPath = "original.wav" }); var restored = new Store(s.Root); Assert(restored.Settings.CaptureSeconds == 15 && restored.Clips.Single().Title == "Saved"); });
Test("Interrupted transcription recovers", () => { var s = new Store(Path.Combine(root, "recovery")); s.Add(new Clip { TranscriptionState = "Working", ImportState = "Working" }); var r = new Store(s.Root); Assert(r.Clips[0].TranscriptionState == "Pending" && r.Clips[0].ImportState == "Pending"); });
Test("Corrupt library is preserved and reported", () => { string dir = Path.Combine(root, "corrupt"); Directory.CreateDirectory(dir); string path = Path.Combine(dir, "library.json"); File.WriteAllText(path, "broken JSON"); try { new Store(dir); throw new Exception("Corrupt library silently accepted"); } catch (InvalidDataException) { } Assert(File.ReadAllText(path) == "broken JSON"); });
Test("Paused capture refuses recording", () => { using var c = new CaptureService(); try { c.Snapshot(3); throw new Exception("No error"); } catch (InvalidOperationException) { } });
Test("Hotkey modifiers and no-repeat configuration", () => { Assert(HotkeySpec.Parse("Ctrl+Alt+F6") == new HotkeySpec(3, 0x75)); Assert(HotkeySpec.Parse("F6") == new HotkeySpec(0, 0x75)); });
Test("Hotkey reserved and unsupported values rejected", () => { foreach (var s in new[] { "Ctrl+Alt+Delete", "Win+L", "F12", "BadKey" }) { try { HotkeySpec.Parse(s); throw new Exception("Accepted " + s); } catch (ArgumentException) { } } });
await AsyncTest("Soundpad exact syntax and duplicate prevention (simulator)", async () =>
{
    var fake = new FakeSoundpad();
    var service = new SoundpadService(fake);
    var path = Path.Combine(root, "sine.wav");
    Assert(await service.Probe());
    Assert(await service.Import(path) == "7");
    Assert(await service.Import(path) == "7");
    Assert(fake.Adds == 1);
    Assert(fake.Commands.Contains("DoAddCategory(\"Voice Replay Studio\", -1)"));
    Assert(fake.Commands.Any(c => c == $"DoAddSound(\"{path}\", 4, 1)"));
});
await AsyncTest("Soundpad import failures never succeed (simulator)", async () =>
{
    var fake = new FakeSoundpad { Reject = true };
    var service = new SoundpadService(fake);
    try
    {
        await service.Import(Path.Combine(root, "sine.wav"));
        throw new Exception("False success");
    }
    catch (IOException ex) { Assert(ex.Message.Contains("trial")); }
});
await AsyncTest("Soundpad connection loss and reconnect (simulator)", async () =>
{
    var fake = new FakeSoundpad { Offline = true };
    var service = new SoundpadService(fake);
    Assert(!await service.Probe());
    fake.Offline = false;
    Assert(await service.Probe());
});
await AsyncTest("Soundpad acknowledgement without path is not success", async () =>
{
    var service = new SoundpadService(new FalseAckSoundpad());
    try
    {
        await service.Import(Path.Combine(root, "sine.wav"));
        throw new Exception("False success");
    }
    catch (IOException ex) { Assert(ex.Message.Contains("not yet")); }
});
await AsyncTest("Real Windows named pipe transport, multi-read UTF8 boundary", async () =>
{
    string name = "VoiceReplayTests-" + Guid.NewGuid().ToString("N");
    var response = "<Soundlist>" + new string('é', 16000) + "</Soundlist>";
    var server = Task.Run(async () => { await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous); await pipe.WaitForConnectionAsync(); byte[] cmd = new byte[512]; int n = await pipe.ReadAsync(cmd); Assert(Encoding.UTF8.GetString(cmd, 0, n) == "GetSoundlist()"); await pipe.WriteAsync(Encoding.UTF8.GetBytes(response)); await pipe.FlushAsync(); pipe.WaitForPipeDrain(); });
    string actual = await new PipeTransport(name).Request("GetSoundlist()");
    await server;
    Assert(actual == response);
});
await AsyncTest("Missing named pipe times out without UI block", async () =>
{
    try
    {
        await new PipeTransport("VoiceReplayAbsent-" + Guid.NewGuid()).Request("IsAlive()");
        throw new Exception("False connection");
    }
    catch (OperationCanceledException) { }
});
await AsyncTest("Transcription failure retains WAV, queued import, restart restoration", async () =>
{
    var s = new Store(Path.Combine(root, "pipeline"));
    s.Settings.StorageDirectory = Path.Combine(s.Root, "Clips");
    var fake = new FakeSoundpad { Offline = true };
    await using (var p = new ClipPipeline(s, new BrokenSpeech(), new SoundpadService(fake)))
    {
        var c = await p.SaveCapture(sine, 48000, "fixture");
        await Wait(() => !p.Busy(c));
        Assert(File.Exists(c.AudioPath) && File.Exists(c.OriginalPath));
        Assert(c.TranscriptionState == "Retry available" && c.ImportState == "Pending retry");
        fake.Offline = false;
        p.Enqueue(c, false);
        await Wait(() => !p.Busy(c));
        Assert(c.ImportState == "Imported (verified)" && c.SoundpadIndex == "7");
    }
    Assert(new Store(s.Root).Clips.Count == 1);
});
await AsyncTest("Transcript filenames, instant import reference preservation", async () =>
{
    var s = new Store(Path.Combine(root, "instant"));
    s.Settings.StorageDirectory = Path.Combine(s.Root, "Clips");
    s.Settings.InstantImport = true;
    var fake = new FakeSoundpad();
    await using var p = new ClipPipeline(s, new FixtureSpeech(), new SoundpadService(fake));
    var c = await p.SaveCapture(sine, 48000, "fixture");
    await Wait(() => !p.Busy(c));
    Assert(c.Transcript == "WHAT THE HELL WAS THAT?");
    Assert(Path.GetFileName(c.AudioPath).StartsWith("WHAT_THE_HELL_WAS_THAT_"));
    Assert(c.RetainedPaths.Count == 2 && c.RetainedPaths.All(File.Exists));
    Assert(c.ImportedPath == c.AudioPath && fake.Adds == 2);
});
await AsyncTest("Manual import preserves disabled automatic-import preference", async () =>
{
    var s = new Store(Path.Combine(root, "manual"));
    s.Settings.StorageDirectory = Path.Combine(s.Root, "Clips");
    s.Settings.AutoImport = false;
    var fake = new FakeSoundpad();
    await using var p = new ClipPipeline(s, new FixtureSpeech(), new SoundpadService(fake));
    var c = await p.SaveCapture(sine, 48000, "fixture");
    await Wait(() => !p.Busy(c));
    Assert(fake.Adds == 0);
    p.Enqueue(c, false, true);
    await Wait(() => !p.Busy(c));
    Assert(fake.Adds == 1 && !s.Settings.AutoImport);
});
await AsyncTest("Five concurrent captures save independent WAVs and survive restart", async () =>
{
    var s = new Store(Path.Combine(root, "burst"));
    s.Settings.StorageDirectory = Path.Combine(s.Root, "Clips");
    s.Settings.AutoImport = false;
    await using (var p = new ClipPipeline(s, new FixtureSpeech(), new SoundpadService(new FakeSoundpad())))
    {
        var clips = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => p.SaveCapture(sine, 48000, "burst fixture")));
        await Wait(() => clips.All(c => !p.Busy(c)));
        Assert(clips.Select(c => c.Id).Distinct().Count() == 5);
        Assert(clips.All(c => AudioFiles.Read(c.AudioPath).Samples.Length == 48000 && File.Exists(c.OriginalPath)));
    }
    Assert(new Store(s.Root).Clips.Count == 5);
});
await AsyncTest("Failed instant import preserves possibly accepted backing path", async () =>
{
    var s = new Store(Path.Combine(root, "uncertain-import"));
    s.Settings.StorageDirectory = Path.Combine(s.Root, "Clips");
    s.Settings.InstantImport = true;
    await using var p = new ClipPipeline(s, new FixtureSpeech(), new SoundpadService(new FakeSoundpad { Offline = true }));
    var c = await p.SaveCapture(sine, 48000, "fixture");
    await Wait(() => !p.Busy(c));
    Assert(c.RetainedPaths.Count == 2 && c.RetainedPaths.All(File.Exists));
    Assert(c.ImportState == "Pending retry" && c.ImportedPath is null);
});
if (args.Contains("--live"))
{
    await AsyncTest("LIVE endpoint enumeration and non-disruptive output capture", async () =>
    {
        using var e = new MMDeviceEnumerator();
        using var before = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var mic = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        using var c = new CaptureService();
        await c.Start(new(before.ID, before.FriendlyName, "Output"));
        await Task.Delay(1500);
        c.UpdateClock();
        Console.WriteLine($"LIVE endpoint: {before.FriendlyName}; status={c.Status}; frames={c.Buffer?.Count}; peak={c.Peak}; rate={c.Buffer?.Rate}");
        Assert(c.Running, "Capture stopped: " + c.Status);
        Assert(c.Buffer!.Count > 0, "No frames buffered");
        c.Stop();
        Assert(!c.Running && c.Buffer is null);
        using var after = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var micAfter = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        Assert(before.ID == after.ID && mic.ID == micAfter.ID);
    });
    await AsyncTest("LIVE process-loopback activation, start, silent clock and stop", async () =>
    {
        using var c = new CaptureService();
        await c.Start(new(Environment.ProcessId.ToString(), "Test process (no playback)", "Process"));
        await Task.Delay(1500);
        c.UpdateClock();
        Assert(c.Running);
        Assert(c.Buffer!.Count > 0);
        Console.WriteLine($"LIVE process: {c.Status}; frames={c.Buffer.Count}; rate={c.Buffer.Rate}");
        c.Stop();
        Assert(!c.Running);
    });
    await AsyncTest("LIVE pausing during application activation never resumes hidden capture", async () =>
    {
        using var capture = new CaptureService();
        for (int i = 0; i < 3; i++)
        {
            var start = capture.Start(new(Environment.ProcessId.ToString(), "Cancel activation fixture", "Process"));
            capture.Stop();
            await start;
            Assert(!capture.Running && capture.Buffer is null, "Canceled activation started monitoring");
        }
    });
}
if (args.Contains("--speech"))
{
    await AsyncTest("LIVE offline Whisper model download and real speech transcription", async () =>
    {
        var store = new Store();
        var transcriber = new LocalTranscriber(store);
        if (!File.Exists(store.ModelPath))
            await transcriber.Download();
        var path = args[Array.IndexOf(args, "--speech") + 1];
        string text = await transcriber.Transcribe(path);
        Console.WriteLine("WHISPER: " + text);
        Assert(text.Contains("country", StringComparison.OrdinalIgnoreCase), "Known JFK speech was not recognized");
    });
}
Test("Preview accepts Elgato monitor and headphones, blocks outgoing buses", () => { Assert(MonitorOutputs.Rejection("Wave Link Monitor (Elgato Virtual Audio)") == ""); Assert(MonitorOutputs.Rejection("Headphones (2- Elgato Wave:3)") == ""); Assert(MonitorOutputs.Rejection("Voice chat (Elgato Virtual Audio)") != ""); Assert(MonitorOutputs.Rejection("Voicemeeter AUX Input") != ""); });
Test("Preview selection, cursor start, loop sample boundaries", () => { float[] data = [.1f, .2f, .3f, .4f, .5f]; var input = new PreviewSamples(data, 10, .1, .4, false); float[] b = new float[8]; Assert(input.Read(b, 0, 8) == 3 && b.Take(3).SequenceEqual(data[1..4])); input = new PreviewSamples(data, 10, .2, .4, true); Assert(input.Read(b, 0, 8) == 8 && b.SequenceEqual(new[] { .3f, .4f, .3f, .4f, .3f, .4f, .3f, .4f })); });
Test("Preview zero duration and reversed/out of bounds selection fail", () => { foreach (var (a, b) in new[] { (.2, .2), (.4, .1), (-.1, .5), (0.0, 2.0) }) { try { new PreviewSamples(sine, 48000, a, b, false); throw new Exception("Invalid selection accepted"); } catch (ArgumentException) { } } });
Test("Preview routes chosen output, updates cursor and stops", () => { string chosen = ""; var fake = new FakePreviewOutput { Seconds = .15 }; using var player = new PreviewPlayer(factory: id => { chosen = id; return fake; }); player.Play(sine, 48000, .2, .7, "chosen-endpoint", false); Assert(chosen == "chosen-endpoint" && player.Playing && Math.Abs(player.Position - .35) < .0001); player.Stop(); Assert(!player.Playing && fake.Disposed); });
Test("Preview initialization failure is actionable and disposes endpoint", () => { var fake = new FakePreviewOutput { Fail = true }; string message = ""; using var player = new PreviewPlayer(m => message = m, _ => fake); try { player.Play(sine, 48000, 0, .2, "unplugged", false); throw new Exception("No error"); } catch (InvalidOperationException) { Assert(message.Contains("disconnected") && fake.Disposed && !player.Playing); } });
Test("Preview silence is reported without opening an output", () => { bool opened = false; using var p = new PreviewPlayer(factory: _ => { opened = true; return new FakePreviewOutput(); }); try { p.Play(new float[100], 100, 0, 1, "id", false); throw new Exception("No error"); } catch (InvalidOperationException e) { Assert(e.Message.Contains("silence") && !opened); } });
if (args.Contains("--preview-live"))
    await AsyncTest("LIVE quiet physical-output render, cursor, capture while previewing, no settings changed", async () =>
{
    var chosen = MonitorOutputs.List().First(x => x.Allowed && x.Name.Contains("Elgato Wave:3") && !x.Muted && x.Volume > 0);
    using var e = new MMDeviceEnumerator();
    using var render = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var mic = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    using var capture = new CaptureService();
    await capture.Start(new(Environment.ProcessId.ToString(), "Own test-process output", "Process"));
    await Task.Delay(100);
    using var player = new PreviewPlayer(Console.WriteLine);
    var tone = PreviewPlayer.TestTone();
    player.Play(tone, 48000, 0, tone.Length / 48000.0, chosen.Id, false);
    await Task.Delay(200);
    Console.WriteLine($"LIVE position={player.Position:F3}s, playing={player.Playing}");
    Assert(player.Position > 0 && capture.Running);
    await Task.Delay(400);
    var recorded = capture.Buffer!.Snapshot(2);
    float peak = recorded.Max(x => Math.Abs(x));
    Console.WriteLine($"LIVE rendered loopback frames={recorded.Length}; peak={peak:F4}; {player.Status}");
    Assert(peak > .005, "No rendered test signal in own process loopback");
    player.Stop();
    capture.Stop();
    using var after = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var micAfter = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    var outputAfter = MonitorOutputs.List().Single(x => x.Id == chosen.Id);
    Assert(render.ID == after.ID && mic.ID == micAfter.ID && chosen.Muted == outputAfter.Muted && chosen.Volume == outputAfter.Volume);
});
Test("NAudio WaveBuffer alias regression for both sample providers", () =>
{
    foreach (ISampleProvider provider in new ISampleProvider[] { new ArraySamples([.25f, -.5f], 48000), new PreviewSamples([.25f, -.5f], 48000, 0, 2 / 48000.0, false) })
    {
        var bytes = new byte[8];
        var reader = new NAudio.Wave.SampleProviders.SampleToWaveProvider(provider);
        Assert(reader.Read(bytes, 0, 8) == 8);
        Assert(BitConverter.ToSingle(bytes, 0) == .25f && BitConverter.ToSingle(bytes, 4) == -.5f);
    }
});
Test("Playback runtime errors stop the playing indication and remain visible", () => { var fake = new FakePreviewOutput(); using var p = new PreviewPlayer(factory: _ => fake); p.Play(sine, 48000, 0, .5, "id", false); fake.Emit(new IOException("Device removed during playback")); Assert(!p.Playing && p.Status.Contains("Device removed")); });
Test("Windows session mute blocks preview with an actionable message", () => { var fake = new FakePreviewOutput { Muted = true }; using var p = new PreviewPlayer(factory: _ => fake); try { p.Play(sine, 48000, 0, .5, "id", false); throw new Exception("Muted session looked successful"); } catch (InvalidOperationException e) { Assert(e.Message.Contains("Volume Mixer") && fake.Disposed && !p.Playing); } });
Test("Playback seek retains original selection and cursor time", () =>
{
    FakePreviewOutput? latest = null;
    using var p = new PreviewPlayer(factory: _ => latest = new FakePreviewOutput());
    p.Play(sine, 48000, .2, .7, "selected", true);
    p.Seek(.4);
    Assert(Math.Abs(p.Position - .4) < 1e-7);
    float[] data = new float[10];
    latest!.Source!.Read(data, 0, 10);
    Assert(data.SequenceEqual(sine[19200..19210]));
    try
    {
        p.Seek(.9);
        throw new Exception("Seek escaped selection");
    }
    catch (InvalidOperationException) { }
});
Test("Missing preview output gives a selection instruction", () => { using var p = new PreviewPlayer(); try { p.Play(sine, 48000, 0, .2, "", false); throw new Exception("No output accepted"); } catch (InvalidOperationException e) { Assert(e.Message.Contains("Select a Monitor output")); } });
Test("Historical ring selection remains exact after new audio arrives", () => { var r = new RingBuffer(100, 3); var data = Enumerable.Range(0, 400).Select(i => (float)Math.Sin(i * .13)).ToArray(); r.Write(data[..250]); var view = r.View(3, 30); long first = view.Last - 100, last = view.Last - 50; r.Write(data[250..300]); Assert(r.ReadRange(first, last).SequenceEqual(data[150..200])); r.Write(data[300..]); Assert(r.ReadRange(first, last).SequenceEqual(data[150..200])); });
Test("Expired ring selection is rejected rather than shifted", () => { var r = new RingBuffer(100, 3); r.Write(new float[300]); long first = 0, last = 50; r.Write(new float[51]); Assert(!r.Contains(first, last)); try { r.ReadRange(first, last); throw new Exception("Expired selection captured newer data"); } catch (InvalidOperationException) { } Assert(r.Id != new RingBuffer(100, 3).Id); });
Test("Warmup live view pads silence on the left and newest signal on right", () => { var r = new RingBuffer(10, 3); r.Write(Enumerable.Repeat(.5f, 10).ToArray()); var f = r.View(3, 3); Assert(f.First == -20 && f.Last == 10 && f.Seconds == 3); Assert(f.Peaks[0] == (0, 0) && f.Peaks[1] == (0, 0) && f.Peaks[2] == (.5f, .5f)); });
Test("Live summary peaks match full PCM after non-aligned ring wrap", () => { var r = new RingBuffer(44100, 3); var data = Enumerable.Range(0, 44100 * 5).Select(i => (float)(Math.Sin(i * .17) * .6)).ToArray(); r.Write(data); var frame = r.View(3, 701); var exact = AudioFiles.Peaks(r.Snapshot(3), 701); Assert(frame.Peaks.SequenceEqual(exact)); });
await AsyncTest("Background writes continue while live views and clip reads run", async () =>
{
    var ring = new RingBuffer(48000, 30);
    ring.Write(sine);
    using var cancel = new CancellationTokenSource();
    var writer = Task.Run(async () => { while (!cancel.IsCancellationRequested) { ring.Write(sine.AsSpan(0, 480)); await Task.Delay(1); } });
    long before = ring.Written;
    for (int i = 0; i < 40; i++)
    {
        ring.View(30, 1200);
        AudioFiles.Read(Path.Combine(root, "sine.wav"));
        await Task.Delay(1);
    }
    cancel.Cancel();
    await writer;
    Assert(ring.Written > before && ring.Count <= 48000 * 30);
});
await AsyncTest("Historical selected audio saves exact full-quality WAV and restores", async () => { var ring = new RingBuffer(48000, 3); ring.Write(sine); var selected = ring.ReadRange(9600, 33600); var s = new Store(Path.Combine(root, "selected-save")); s.Settings.StorageDirectory = Path.Combine(s.Root, "Clips"); s.Settings.AutoImport = false; Clip clip; await using (var p = new ClipPipeline(s, new FixtureSpeech(), new SoundpadService(new FakeSoundpad()))) { clip = await p.SaveCapture(selected, 48000, "historical selection"); await Wait(() => !p.Busy(clip)); } var restored = new Store(s.Root).Clips.Single(); var audio = AudioFiles.Read(restored.AudioPath); Assert(audio.Samples.Length == 24000 && audio.Samples.Zip(selected).All(x => Math.Abs(x.First - x.Second) < .0001)); });
Test("Live waveform aggregation performance (1200 bins, 30 second buffer)", () => { var ring = new RingBuffer(48000, 30); for (int i = 0; i < 30; i++) ring.Write(sine); var sw = System.Diagnostics.Stopwatch.StartNew(); for (int i = 0; i < 100; i++) ring.View(30, 1200); sw.Stop(); Console.WriteLine($"LIVE VIEW benchmark: {sw.Elapsed.TotalMilliseconds / 100:F3} ms/frame"); Assert(sw.Elapsed.TotalSeconds < 8, "Live aggregation is too slow for 25 FPS"); });
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
Directory.Delete(root, true);
return failed == 0 ? 0 : 1;

static async Task Wait(Func<bool> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); while (!predicate()) await Task.Delay(30, timeout.Token); }
sealed class BrokenSpeech : ITranscriber
{
    public Task<string> Transcribe(string path, CancellationToken token = default) => throw new IOException("Fixture inference failure");
}
sealed class FixtureSpeech : ITranscriber
{
    public Task<string> Transcribe(string path, CancellationToken token = default) => Task.FromResult("WHAT THE HELL WAS THAT?");
}
sealed class FakeSoundpad : ISoundpadTransport
{
    public bool Offline, Reject; private bool category; public int Adds; public List<string> Commands = []; private readonly List<string> paths = [];
    public Task<string> Request(string command, CancellationToken token = default)
    {
        Commands.Add(command);
        if (Offline)
            throw new IOException("Fixture disconnected");
        string response;
        if (command == "IsAlive()")
            response = "R-200";
        else if (command == "GetSoundlist()")
            response = new XElement("Soundlist", paths.Select(p => new XElement("Sound", new XAttribute("index", "7"), new XAttribute("url", p)))).ToString();
        else if (command == "GetCategories(false,false)")
            response = category ? "<Categories><Category index=\"4\" name=\"Voice Replay Studio\"/></Categories>" : "<Categories/>";
        else if (command == "DoAddCategory(\"Voice Replay Studio\", -1)")
        {
            category = true;
            response = "R-200";
        }
        else if (command.StartsWith("DoAddSound(\""))
        {
            if (Reject)
                response = "R-403: Trial version";
            else
            {
                Adds++;
                paths.Add(command.Split('"')[1]);
                response = "R-200";
            }
        }
        else
            throw new Exception("Unexpected command: " + command);
        return Task.FromResult(response);
    }
}
sealed class FalseAckSoundpad : ISoundpadTransport
{
    public Task<string> Request(string cmd, CancellationToken token = default) => Task.FromResult(cmd switch { "GetSoundlist()" => "<Soundlist/>", "GetCategories(false,false)" => "<Categories><Category index=\"4\" name=\"Voice Replay Studio\"/></Categories>", _ => "R-200" });
}
static class NativePipe
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverPid);
}
sealed class FakePreviewOutput : IPreviewOutput
{
    public bool Playing
    {
        get; private set;
    }
    public double Seconds
    {
        get; set;
    }
    public bool Disposed, Fail, Muted; public event Action<Exception?>? Stopped;
    public ISampleProvider? Source;
    public void Initialize(ISampleProvider source)
    {
        if (Fail)
            throw new IOException("Output disconnected — refresh monitor devices");
        Source = source;
    }
    public void Play() => Playing = true; public void Stop()
    {
        Playing = false;
        Stopped?.Invoke(null);
    }
    public void Emit(Exception? error) => Stopped?.Invoke(error); public string Diagnostic() => Muted ? "app session mute=True, volume=0%" : "Fixture output; no audio rendered"; public void Dispose() => Disposed = true;
}
