using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Forms;
namespace SoundpadWeb;

/// <summary>
/// Entry point. Since 2.36 the companion is a desktop app: the STA main thread runs the Soundpad Web window (one
/// WebView2 that owns the guest session), and the companion itself (Bridge, loopback server, poll loop) runs on the
/// thread pool. --no-window, or a PC without the WebView2 Runtime, gets the 2.35 behaviour: a console and the browser.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Soundpad Web requires Windows 11 x64."); return 1; }
        ConsoleLink.Start();
        // Stage H (2.44): puts a backup's state.json back while the companion is not running; no window, pipe or network.
        if (args.Contains("--restore-state")) return OfflineRestore.Run(args);
        if (args.Contains("--licenses"))
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains("Licenses"))) { using var stream = assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream); Console.WriteLine(reader.ReadToEnd()); }
            return 0;
        }
        if (args.FirstOrDefault() == "--transcribe-file" && args.Length >= 2) return Task.Run(() => Transcribe(args)).GetAwaiter().GetResult();
        string? fallback = null;
        bool window = !args.Contains("--no-window");
        if (window && WebViewRuntime.Version() == null) { window = false; fallback = WebViewRuntime.Missing + (args.Contains("--no-browser") ? "" : " Until then the companion opens your browser, as before."); }
        if (!window)
        {
            // The 2.35 companion: a console window, Enter or its X stops it.
            ConsoleLink.EnsureWindow();
            int result = Task.Run(() => Run(args, null, fallback)).GetAwaiter().GetResult();
            // 2.44: everything of this process is closed now (server, pipe, lock); the swap and its watchdog run here.
            return pendingUpdate is { } swap ? swap.Finish() : result;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var ui = new DesktopUi();
        var companion = Task.Run(() => Run(args, ui, null));
        _ = companion.ContinueWith(_ => ui.Quit(), TaskScheduler.Default);
        Application.Run(ui.Context);
        int code = companion.GetAwaiter().GetResult();
        if (ui.DataFolder != null) ui.ForgetLaunch(ui.DataFolder);
        return pendingUpdate is { } update ? update.Finish() : code;
    }

    /// <summary>Set when the host confirmed an update (2.44): Main finishes it once Run has closed everything.</summary>
    private static PendingUpdate? pendingUpdate;

    /// <summary>The build's GitHub repository for updates (AssemblyMetadata UpdateRepository, set only by CI), or null.</summary>
    private static string? Metadata(string key) => System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>The flags a relaunch after an update keeps (2.44): where the data is, which Soundpad and website, how it shows.</summary>
    private static List<string> SafeFlags(string[] args, string directory)
    {
        var kept = new List<string> { "--data-dir", directory };
        foreach (var key in new[] { "--pipe", "--site", "--update-source" })
        {
            int i = Array.IndexOf(args, key);
            if (i >= 0 && i + 1 < args.Length) { kept.Add(key); kept.Add(args[i + 1]); }
        }
        foreach (var flag in new[] { "--dev", "--hidden-window", "--no-window", "--no-browser" }) if (args.Contains(flag)) kept.Add(flag);
        return kept;
    }

    /// <summary>
    /// The data folder's lock: one companion at a time. A second one says so in plain words (2.44). After an update the
    /// new process waits up to 30 s for the old one to let go of it.
    /// </summary>
    private static FileStream Lock(string directory, bool resuming)
    {
        var path = Path.Combine(directory, "companion.lock");
        for (long until = Environment.TickCount64 + (resuming ? 30000 : 0); ; Thread.Sleep(200))
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < until) { }
            catch (IOException) { throw new InvalidOperationException("Soundpad Web is already running. Switch to its window, or quit it first."); }
        }
    }

    private static async Task<int> Transcribe(string[] args)
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
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine("Transcription failed: " + ex.Message); return 1; }
    }

    /// <summary>The companion itself, in the 2.35 startup order. <paramref name="ui"/> is null without a window.</summary>
    private static async Task<int> Run(string[] args, DesktopUi? ui, string? fallback)
    {
        try
        {
            string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var directory = Path.GetFullPath(Arg("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb"));
            Directory.CreateDirectory(directory);
            // 2.44: started by the companion that updated itself, to continue its session (see docs/UPDATES.md).
            string? resumeId = Arg("--resume-update") is { } rid && System.Text.RegularExpressions.Regex.IsMatch(rid, "^[a-f0-9]{32}$") ? rid : null;
            int? waitPid = int.TryParse(Arg("--wait-pid"), out int wp) && wp > 0 ? wp : null;
            using var instanceLock = Lock(directory, resumeId != null);
            // Only the instance that holds the lock writes the log.
            CompanionLog.Start(directory);
            var exePath = Environment.ProcessPath is { } running && Path.GetFileName(running).Equals("SoundpadWeb.exe", StringComparison.OrdinalIgnoreCase) ? running : null;
            UpdateHandoff? handoff = null;
            if (resumeId != null)
            {
                handoff = UpdateHandoffFile.TryConsume(directory, resumeId, waitPid, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UpdateHandoffFile.Running, out var problem);
                if (handoff == null)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {problem} Starting as after an unexpected exit.");
                    // The companion that updated is still waiting: it puts the previous version back and starts that instead.
                    if (waitPid is int watcher && UpdateHandoffFile.Running(watcher)) return 3;
                }
            }
            // A normal start: whatever an earlier update left is removed, the hand-over's private key first (2.44).
            else UpdateFiles.CleanLeftovers(directory, exePath);
            using var pipe = new SoundpadPipe(Arg("--pipe") ?? "sp_remote_control");
            using var bridge = new Bridge(pipe, new StateStore(directory));
            var builtInWebsite = Metadata("WebsiteUrl");
            bridge.MimicLog += message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Mimic: {message}");
            // Updates (2.44): from this project's GitHub releases in a CI build, from a loopback test server with
            // --update-source, and not at all otherwise.
            UpdateSource? updateSource = null;
            if (Arg("--update-source") is { } testSource)
            {
                updateSource = UpdateSource.ForTest(testSource, out var refusal);
                if (updateSource == null) Console.WriteLine(refusal);
            }
            else updateSource = UpdateSource.ForRepository(Metadata("UpdateRepository"));
            bridge.ConfigureUpdates(new UpdateOptions { Source = updateSource, Current = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", ExePath = exePath, DataDir = directory });
            // Recording history left by a launch that did not close normally is recovered (the host can discard it);
            // anything else left over is removed (2.39). This instance holds the data folder's lock, so nothing else uses it.
            // After an update the session goes on instead (2.44): its history, drafts, Temp, guests and capture.
            if (handoff != null) await bridge.ResumeFromHandoff(handoff.Bridge);
            else bridge.RecoverHistory();
            var site = Arg("--site") ?? bridge.Website ?? Environment.GetEnvironmentVariable("SOUNDPAD_WEB_URL") ?? builtInWebsite;
            if (string.IsNullOrWhiteSpace(site))
            {
                // A window without a console to type in asks in a small dialog instead (2.36).
                if (ui != null && !ConsoleLink.InputRedirected) site = await ui.AskWebsite();
                else
                {
                    Console.WriteLine("Soundpad Web — first launch\nPaste your published HTTPS website URL (saved for future launches):");
                    site = Console.ReadLine();
                }
            }
            if (!Uri.TryCreate(site, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(args.Contains("--dev") && uri.IsLoopback && uri.Scheme == "http")) || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Use an HTTPS website URL. Developers may use --dev with an HTTP loopback URL.");
            var clean = new UriBuilder(uri) { Fragment = "", Query = "" }.Uri;
            bridge.SetWebsite(clean.ToString());
            var origin = clean.GetLeftPart(UriPartial.Authority);
            // The window's page proves it is the window with this key (2.36). New every launch, kept in memory only:
            // it is never printed or saved by the companion.
            var ownerKey = ui == null ? null : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await using var server = new LocalServer(bridge, origin, ownerKey, handoff?.Server) { Log = line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}") };
            await server.Start();
            bool updating = false;
            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            // Closing a console window with X (or logging off / shutting down) gives a console app about 5 seconds
            // before Windows ends it. Use them for the same cleanup as End hosting, so temporary audio and the Temp
            // category disappear then and there (2.16). Anything not finished in time is removed on the next start.
            void CleanUpBeforeWindowsCloses(System.Runtime.InteropServices.PosixSignalContext context)
            {
                Console.WriteLine("Closing: removing temporary audio...");
                // An ordinary quit deletes the recording history and its transcript (2.39). Windows ends the process
                // without unwinding Main, so Bridge.Dispose would never do it here; it is local files, so it goes first.
                try { bridge.CloseHistory(); } catch (Exception) { /* removed by the next launch's recovery */ }
                try
                {
                    // Cleanup never removes anything while audio plays; stop it first, but only if it's a Temp sound.
                    // The Bridge does it (2.35), so the stop also cancels every start still pending and updates the record.
                    bridge.StopTemporaryPlayback().Wait(TimeSpan.FromSeconds(1));
                    bridge.CleanupSession().Wait(TimeSpan.FromSeconds(3.5));
                }
                catch (Exception) { /* finished on next start */ }
            }
            using var onClose = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, CleanUpBeforeWindowsCloses);
            using var onShutdown = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, CleanUpBeforeWindowsCloses);
            // Enter stops the companion when standard input is ours: a script's pipe or the console window we opened.
            // A terminal the window was started from keeps its keyboard (Ctrl+C still works there).
            void WatchEnter() => _ = Task.Run(() => { if (Console.ReadLine() != null) shutdown.Cancel(); });
            if (ConsoleLink.ReadsEnter) WatchEnter();
            // After a crash the last session's Temp waits for the host's Recover or Discard (2.40); otherwise it is cleaned up as before.
            // After an update nothing is cleaned up: the session continues (2.44).
            if (handoff == null) { try { await bridge.StartupCleanup(); } catch (Exception) { Console.WriteLine("Temporary audio cleanup will retry when Soundpad is available."); } }
            var url = clean + "#host=" + server.Port + "." + server.Token;
            // Tests only (2.44, scripts/update-swap-test.ps1): with --dev, --launch-file writes the launch link (it holds the
            // token, which the companion never prints) to a file the test made, so a test client can connect without a browser.
            if (args.Contains("--dev") && Arg("--launch-file") is { } launchFile) { try { File.WriteAllText(launchFile, url); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Console.WriteLine("The launch file could not be written: " + ex.Message); } }
            // 2.44: the host confirmed an update. Pages that can show it are told, the hand-over is kept in memory (it is
            // written just before the new version starts), and the companion stops without its quit cleanup.
            bridge.UpdateExitRequested += async exit =>
            {
                await server.NotifyUpdating(exit.Version);
                var window = ui != null ? await ui.Placement(args.Contains("--hidden-window")) : new WindowHandoff(true, false);
                var id = Guid.NewGuid().ToString("N");
                var current = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
                var serverPart = server.Export();
                UpdateHandoff Make() => new() { Id = id, CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DataDir = directory, OldPid = Environment.ProcessId, FromVersion = current, ToVersion = exit.Version, Server = serverPart, Bridge = exit.Bridge, Window = window };
                if (exePath == null || exit.DownloadPath == null) { Console.WriteLine("This build cannot replace its own EXE."); return; }
                pendingUpdate = new PendingUpdate(new SwapPlan(exePath, exit.DownloadPath, exit.Version, current, directory, id, SafeFlags(args, directory), () => UpdateHandoffFile.Protect(Make()), ConsoleLink.OwnConsole));
                updating = true;
                ui?.CloseForUpdate();
                shutdown.Cancel();
            };
            bool closedWindow = false;
            var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OpenBrowser() { if (!args.Contains("--no-browser")) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            string stops = ConsoleLink.ReadsEnter ? "Enter or Ctrl+C stops the companion." : "Ctrl+C stops the companion.";
            // The first line is read by scripts/smoke-exe.ps1: keep "loopback port N" in it.
            if (ui == null) Console.WriteLine($"Soundpad Web is running on loopback port {server.Port}.\nKeep this window and your host browser tab open. {stops}\nAudio devices and Soundpad settings are unchanged.");
            else Console.WriteLine($"Soundpad Web is running on loopback port {server.Port}.\nHosting runs in the Soundpad Web window; closing it stops the companion.\nAudio devices and Soundpad settings are unchanged.");
            if (fallback != null) Console.WriteLine(fallback);
            // After an update open pages reconnect by themselves; a browser opens only when the port had to change (2.44).
            if (ui == null) { if (handoff == null || !server.PortKept) OpenBrowser(); }
            else
            {
                await ui.Open(url + "&owner=" + ownerKey, origin, directory,
                    failed: reason =>
                    {
                        // WebView2 is missing or broke: carry on the 2.35 way, with a console and the default browser.
                        bool hadEnter = ConsoleLink.ReadsEnter;
                        ConsoleLink.EnsureWindow();
                        if (!hadEnter && ConsoleLink.ReadsEnter) WatchEnter();
                        Console.WriteLine(reason + " The companion keeps running without its window" + (args.Contains("--no-browser") ? "." : " and opens your browser instead.") + $"\nKeep your host browser tab open. {(ConsoleLink.ReadsEnter ? "Enter or Ctrl+C" : "Ctrl+C")} stops the companion.");
                        OpenBrowser();
                    },
                    closing: sessionEnding =>
                    {
                        // Closing the window quits the companion completely (2.36), with the cleanup of every other exit.
                        closedWindow = true; shutdown.Cancel();
                        // Windows is logging off or shutting down: it ends the process soon after this returns, so
                        // give the cleanup the same few seconds the console window's X had.
                        if (sessionEnding) cleaned.Task.Wait(TimeSpan.FromSeconds(4.5));
                    },
                    hidden: args.Contains("--hidden-window"),
                    asking: () =>
                    {
                        // Clips in Temp would be removed by quitting (2.40): the window's page asks Keep selected / Quit anyway
                        // / Cancel; with no page to ask (after End hosting, say), the window asks Keep them / Quit anyway / Cancel.
                        return QuitQuestion.Ask(bridge.TemporaryClipCount, server.AskQuit, ui.AskQuit, bridge.TemporaryClipIds, answer =>
                        {
                            if (answer.Action == "cancel") return;
                            _ = Task.Run(async () =>
                            {
                                if (answer.Action == "keep") { try { await bridge.KeepForQuit(answer.AssetIds); } catch (Exception) { /* quits anyway, as asked */ } }
                                closedWindow = true; shutdown.Cancel();
                            });
                        });
                    },
                    resumed: handoff?.Window);
            }
            long nextLibrary = 0; int pollFailures = 0; bool readyWritten = handoff == null;
            using var wake = new SemaphoreSlim(0, 1);
            // Commands finish on several threads at once (a Stop does not wait for the gate, 2.35): two may both see the
            // semaphore empty, and the second Release must not turn an accepted command into a failure.
            bridge.CommandAccepted += () => { try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } };
            try
            {
                while (!shutdown.IsCancellationRequested)
                {
                    var now = Environment.TickCount64;
                    bool refresh = now >= nextLibrary;
                    if (refresh) nextLibrary = now + 30000;
                    // The poll loop must never end the companion (2.35): whatever one poll runs into, the next one tries again.
                    try { await bridge.Poll(refresh); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { if (pollFailures++ == 0) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] A status check failed and will be retried: {ex.Message}"); }
                    if (!readyWritten)
                    {
                        // 2.44: the server runs and the first poll ran: the old process's watchdog may let go. The hand-over goes.
                        readyWritten = true;
                        try { UpdateFiles.WriteAtomic(Path.Combine(directory, "update-ready." + handoff!.Id), System.Text.Encoding.UTF8.GetBytes(Json.Write(new UpdateReady(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", Environment.ProcessId)))); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("The update could not report that it started: " + ex.Message); }
                        UpdateHandoffFile.Finish(directory);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Updated to version {typeof(Program).Assembly.GetName().Version?.ToString(3)}; the session continues.");
                    }
                    else if (refresh && updateSource != null) bridge.Updates.StartLaunchCheck();
                    try { await wake.WaitAsync(TimeSpan.FromMilliseconds(bridge.Snapshot.Playback.Status is "PLAYING" or "SEEKING" ? 250 : bridge.Snapshot.Playback.Status == "PAUSED" ? 500 : 1500), shutdown.Token); }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                // Leaving for an update (2.44): no quit cleanup. Temp, the history and the journal session stay for the next process.
                if (updating) cleaned.TrySetResult();
                else if (closedWindow)
                {
                    // As the console window's X did in 2.35: a Temp sound still playing is stopped so cleanup can remove it.
                    Console.WriteLine("Closing: removing temporary audio...");
                    try { await bridge.StopTemporaryPlayback().WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { /* cleanup below retries next start */ }
                }
                if (!updating)
                {
                    try { await bridge.CleanupSession(); } catch (Exception) { Console.WriteLine("Temporary entries retained for cleanup on next launch."); }
                    // Before `cleaned`: on logoff or shutdown the window's closing callback returns once it is set, and Windows
                    // may end the process before Bridge.Dispose runs (which still does it, as a backstop, on every other exit).
                    try { bridge.CloseHistory(); } catch (Exception) { /* removed by the next launch's recovery */ }
                    cleaned.TrySetResult();
                }
            }
            return Environment.ExitCode;
        }
        catch (OperationCanceledException) { return Environment.ExitCode; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Soundpad Web: " + ex.Message);
            // Started from Explorer, nobody would see the line above.
            if (ui != null && !ConsoleLink.HasOutput) { try { await ui.ShowError(ex.Message); } catch (Exception) { } }
            return 1;
        }
    }
}
