using System.Diagnostics;
using SoundpadWeb;
namespace SoundpadWeb.Testing;

/// <summary>
/// Acceptance test of exact cue starts and regions (2.37) on the REAL Soundpad, through the real Bridge path. Not
/// run automatically and not yet run at all: it waits for a quiet moment with the owner.
///
/// How to run it later: close the Soundpad Web companion and VRChat, start Soundpad normally and leave it idle
/// (nothing playing), then run <c>SoundpadWeb.Harness.exe --real-exact-playback-probe</c> from
/// <c>companion\harness\bin\Release\net10.0-windows10.0.19041.0\win-x64</c>. It takes about half a minute.
///
/// What it does, with the same safety as <c>--real-slice-audio-probe</c>: it refuses unless the companion and
/// VRChat are closed and Soundpad is reachable and stopped. It adds one throwaway category holding one fixture
/// file (three 1-second tones at about -48 dBFS: 700, 1900 and 3100 Hz), starts a Bridge on a throwaway data
/// folder (never the companion's), registers the three slots in that Bridge's own Temp category, and captures
/// Soundpad's own output (process loopback, an output stream; never a microphone) while it plays a region
/// [1.0 s, 2.0 s) and a cue start at 2.0 s, three times each, exactly as a click on the website would. Exact means:
/// the wanted tone for its full second, and nothing at all of a tone before it (a leaked beginning) or after it
/// (an overrun). For comparison it plays the same cue start once the 2.36 way (play, then seek) and reports how
/// much of the beginning that let through. Then it stops, waits until Soundpad has been stopped for a second
/// (Soundpad 4.0.35 exited once when a category was removed right after one of its sounds played), ends the
/// Bridge's session (which removes its Temp category with the slots), removes its own category the same careful
/// way, and checks that Soundpad's library is exactly as before. Exit code 0: exact and restored; 1: not exact;
/// 2: refused or aborted before measuring; 3: nothing was captured (no output device?).
/// </summary>
internal static class RealExactPlaybackProbe
{
    /// <summary>What the probe needs from the PC, so its refusals can be tested with stand-ins.</summary>
    internal sealed record Host(Func<string, bool> Running, Func<int?> SoundpadProcess, Func<ISoundpad> Pipe, Func<IMimicCapture> Capture, string TempFolder);

    private static Host Real() => new(
        name => Process.GetProcessesByName(name).Length > 0,
        () => Process.GetProcessesByName("Soundpad").FirstOrDefault()?.Id,
        () => new SoundpadPipe(),
        () => new WasapiMimicCapture(),
        SlotFiles.LongPath(Path.GetTempPath()));

    public static Task<int> Run() => Run(Real(), Console.Out);

    private const int Rate = 48000;
    internal static async Task<int> Run(Host host, TextWriter log)
    {
        if (host.Running("SoundpadWeb")) { log.WriteLine("ABORT: the companion is running. Close it first."); return 2; }
        if (host.Running("VRChat")) { log.WriteLine("ABORT: VRChat is running; this probe waits for a quiet moment."); return 2; }
        if (host.SoundpadProcess() is not int soundpad) { log.WriteLine("ABORT: Soundpad is not running."); return 2; }
        var pipe = host.Pipe();
        try
        {
            string status;
            try { status = await pipe.Request("GetPlayStatus()"); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { log.WriteLine("ABORT: Soundpad is not reachable (" + ex.GetType().Name + ")."); return 2; }
            if (status != "STOPPED") { log.WriteLine("ABORT: Soundpad is busy (" + status + ")."); return 2; }
            return await Measure(host, pipe, soundpad, log);
        }
        finally { (pipe as IDisposable)?.Dispose(); }
    }

    private static async Task<int> Measure(Host host, ISoundpad real, int soundpad, TextWriter log)
    {
        async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { var c = await ManagedSoundpad.Read(real); for (int i = 0; i < 100 && !done(c); i++) { await Task.Delay(150); c = await ManagedSoundpad.Read(real); } return c; }
        async Task<bool> StoppedAndQuiet()
        {
            // Stopped, a second later still stopped: only then is a category removed (see the class note).
            for (int i = 0; i < 20; i++)
            {
                if (await real.Request("GetPlayStatus()") != "STOPPED") { await real.Request("DoStopSound()"); await Task.Delay(300); continue; }
                await Task.Delay(1000);
                if (await real.Request("GetPlayStatus()") == "STOPPED") return true;
            }
            return false;
        }
        var tag = Guid.NewGuid().ToString("N")[..8];
        var folder = Path.Combine(host.TempFolder, "soundpad-web-exact-probe-" + tag);
        var fixture = Path.Combine(folder, "exact-probe-" + tag + ".wav");
        var name = "Soundpad Web exact probe [" + tag + "]";
        var before = await ManagedSoundpad.Read(real);
        log.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
        bool added = false; Bridge? bridge = null; IMimicCapture? capture = null; int result = 2;
        try
        {
            Directory.CreateDirectory(folder);
            var mono = ExactPlaybackAnalysis.Tones(Rate, ExactPlaybackAnalysis.QuietLevel, (700, 1000), (1900, 1000), (3100, 1000));
            File.WriteAllBytes(fixture, SliceWav.Encode(mono.SelectMany(v => new[] { v, v }).ToArray(), Rate, 2));
            log.WriteLine("add category: " + await real.Request($"DoAddCategory(\"{name}\", -1)")); added = true;
            var ready = await Settle(c => c.Categories.Any(x => x.Name == name));
            int category = ready.Categories.Single(c => c.Name == name).Index;
            var reply = await real.Request($"DoAddSound(\"{fixture}\", {category}, 1)");
            ready = await Settle(c => c.Entries.Any(e => e.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase)));
            if (!reply.StartsWith("R-200", StringComparison.Ordinal) || !ready.Entries.Any(e => e.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase))) { log.WriteLine("ABORT: Soundpad did not list the fixture: " + reply); return 2; }

            // The real Bridge on a throwaway data folder, with no capture device, hotkey or speech model of its own.
            bridge = new Bridge(real, new StateStore(Path.Combine(folder, "data")));
            bridge.UseMimicCapture(new MimicFixture()); bridge.UseTranscriptEngine(new TranscriptFixture()); bridge.UseHotkey(new HotkeyFixture());
            await bridge.Poll(true);
            if (bridge.SoundIdOf(fixture) is not string sound) { log.WriteLine("ABORT: the Bridge does not see the fixture."); return 2; }
            if (!await bridge.RegisterSlotsNow()) { log.WriteLine("ABORT: the slots could not be registered (see the lines above)."); return 2; }
            log.WriteLine("slots registered: " + string.Join(", ", bridge.SlotPaths));
            await bridge.WarmExact(fixture);

            capture = host.Capture();
            await capture.Start(new MimicSource("app:" + soundpad, "Soundpad", "app"), 120);
            if (!capture.Running || capture.Ring == null) { log.WriteLine("ABORT: could not capture Soundpad's output: " + capture.Status); return 2; }
            var ring = capture.Ring; int rate = ring.Rate;
            await Task.Delay(400);
            float[] Captured(long from, int ms) { long end = Math.Min(ring.Written, from + (long)ms * rate / 1000); return end > from ? ring.Read(from, end) : []; }
            long idleAt = ring.Written; await Task.Delay(300);
            double floor = ExactPlaybackAnalysis.Floor(Captured(idleAt, 300));
            log.WriteLine($"capture: {capture.Status}, idle floor {floor:0.000000}");
            Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op, sound);

            async Task<(bool Exact, ExactPlaybackAnalysis.Verdict Verdict)> Play(Command command, double wantedHz, double[] beforeHz, double[] afterHz, bool exactWanted)
            {
                long starts = bridge.ExactStarts, at = ring.Written;
                var answer = await bridge.Execute(command, "host");
                await Task.Delay(1600);
                var verdict = ExactPlaybackAnalysis.Judge(Captured(at, 1600), rate, floor, wantedHz, 1000, beforeHz, afterHz);
                bool sliced = bridge.ExactStarts > starts;
                await bridge.Execute(Cmd("stop"), "host"); await Task.Delay(400);
                if (!answer.Ok) log.WriteLine("  the command failed: " + answer.Error);
                if (exactWanted && !sliced) log.WriteLine("  it was NOT played as an exact slice (the old way was taken)");
                return (answer.Ok && sliced == exactWanted && verdict.Exact, verdict);
            }
            bool all = true; bool heard = false;
            for (int round = 1; round <= 3; round++)
            {
                var region = await Play(Cmd("region") with { PositionMs = 1000, EndMs = 2000 }, 1900, [700], [3100], true);
                log.WriteLine($"RESULT region 1.0-2.0 s round {round}: {(region.Exact ? "EXACT" : "NOT EXACT")}: {region.Verdict.Detail}");
                var cue = await Play(Cmd("startAt") with { PositionMs = 2000 }, 3100, [700, 1900], [], true);
                log.WriteLine($"RESULT cue start at 2.0 s round {round}: {(cue.Exact ? "EXACT" : "NOT EXACT")}: {cue.Verdict.Detail}");
                all &= region.Exact && cue.Exact; heard |= region.Verdict.WantedMs != null || cue.Verdict.WantedMs != null;
            }
            bridge.UseExactSlices(false);
            var old = await Play(Cmd("startAt") with { PositionMs = 1000 }, 1900, [700], [], false);
            bridge.UseExactSlices(true);
            log.WriteLine($"REFERENCE the 2.36 way (play, then seek) for a cue start at 1.0 s: {old.Verdict.Detail}");
            result = !heard ? 3 : all ? 0 : 1;
            log.WriteLine(result == 0 ? "RESULT: exact starts and ends confirmed" : result == 3 ? "INCONCLUSIVE: nothing was captured from Soundpad's output" : "RESULT: NOT EXACT (see the rounds above)");
        }
        finally
        {
            try { capture?.Stop(); capture?.Dispose(); } catch (Exception) { }
            try
            {
                if (!await StoppedAndQuiet()) log.WriteLine("CLEANUP PROBLEM: Soundpad did not stay stopped; nothing was removed.");
                else
                {
                    // Ends the throwaway session: removes the Bridge's Temp category with the slots (stopped + quiet itself).
                    if (bridge != null) { await bridge.CleanupSession(); bridge.Dispose(); bridge = null; }
                    if (added && await StoppedAndQuiet())
                    {
                        var now = await Settle(c => c.Categories.Any(x => x.Name == name));
                        var mine = now.Categories.SingleOrDefault(c => c.Name == name);
                        // Only ever the probe's own category, and only while it holds nothing but the probe's fixture.
                        if (mine != null && mine.Paths.All(p => p.Equals(fixture, StringComparison.OrdinalIgnoreCase))) log.WriteLine("remove probe category: " + await real.Request($"DoRemoveCategory({mine.Index})"));
                        else if (mine != null) log.WriteLine("LEFT IN PLACE: the probe category holds something unexpected; remove it by hand: " + name);
                    }
                }
                var after = await Settle(c => !c.Categories.Any(x => x.Name == name) && c.Entries.Length == before.Entries.Length);
                bool same = after.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).SequenceEqual(before.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                    && after.Categories.OrderBy(c => c.Index).Select(c => c.Name).SequenceEqual(before.Categories.OrderBy(c => c.Index).Select(c => c.Name));
                log.WriteLine($"final: {after.Entries.Length} sounds, {after.Categories.Length} categories (before: {before.Entries.Length}, {before.Categories.Length}); library identical to before: {same}");
                if (!same) { log.WriteLine("CLEANUP PROBLEM: check Soundpad for \"" + name + "\" and a \"Temp - Soundpad Web\" category holding slot-N.wav entries."); if (result == 0) result = 1; }
            }
            catch (Exception ex) { log.WriteLine("CLEANUP PROBLEM: " + ex.Message + " — check Soundpad for the category \"" + name + "\"."); }
            bridge?.Dispose();
            for (int i = 0; i < 40 && Directory.Exists(folder); i++) { try { Directory.Delete(folder, true); } catch (IOException) { await Task.Delay(150); } catch (UnauthorizedAccessException) { await Task.Delay(150); } }
            if (Directory.Exists(folder)) log.WriteLine("probe folder still in use: " + folder);
        }
        return result; // after the clean-up, which turns a pass into a failure if the library was not restored
    }
}
