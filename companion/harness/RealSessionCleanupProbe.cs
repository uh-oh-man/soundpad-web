using System.Diagnostics;
using System.Text.Json;
namespace SoundpadWeb.Testing;

/// <summary>
/// Session-end cleanup on the REAL Soundpad (2.44 real-hardware proof), with a throwaway data folder inside
/// --data-dir (never the companion's own; the older --real-session-cleanup runs on the companion's real data and is
/// not what this measures). It runs exactly what the companion does at End hosting and on window close
/// (<see cref="Bridge.CleanupSession"/>) three times: while a quiet Temp sound (about -48 dBFS) is PAUSED, while it is
/// PLAYING, and after Stop once Soundpad has stayed stopped for 2 s. The claim: the app's Temp category is removed
/// only in the last case (stopped and quiet), and Soundpad's list then is exactly as before. It never removes a
/// category itself, and refuses while the companion or VRChat runs or Soundpad is busy.
/// Exit code 0: the claim holds and the list is restored; 1: it does not; 2: refused or aborted.
/// </summary>
internal static class RealSessionCleanupProbe
{
    public static async Task<int> Run(string root)
    {
        var log = Console.Out;
        using var real = new SoundpadPipe();
        if (await RealProbes.Refusal(real) is string refused) { log.WriteLine("ABORT: " + refused); return 2; }
        HostHotkeys.ForbidWindowsHotkeys = true;
        var tag = Guid.NewGuid().ToString("N")[..8];
        // Short names: Soundpad refuses a path of 260 characters or more, and --data-dir can be deep.
        var folder = Path.Combine(root, "c" + tag[..4]);
        Directory.CreateDirectory(folder); folder = SlotFiles.LongPath(folder);
        var data = folder;
        var before = await ManagedSoundpad.Read(real);
        log.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
        Bridge? bridge = null; int result = 2;
        async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { var c = await ManagedSoundpad.Read(real); for (int i = 0; i < 40 && !done(c); i++) { await Task.Delay(150); c = await ManagedSoundpad.Read(real); } return c; }
        AudioJournal Journal() => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(data, "managed-audio", "journal.json")), Json.Options)!;
        try
        {
            var live = new Bridge(real, new StateStore(data));
            bridge = live;
            live.MimicLog += message => log.WriteLine("  companion log: " + message);
            live.UseMimicCapture(new MimicFixture()); live.UseTranscriptEngine(new TranscriptFixture()); live.UseHotkeys(key => new HotkeyFixture(key));
            await live.Poll(true);
            Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
            var begin = await live.Execute(Cmd("beginSession") with { AudienceId = "cleanup-probe-" + tag }, "host");
            log.WriteLine("begin session: " + begin.Ok + " " + begin.Error);
            var tone = ExactPlaybackAnalysis.Tones(48000, ExactPlaybackAnalysis.QuietLevel, (800, 8000));
            var upload = await live.Upload(MimicRing.Wav(tone, 48000), "Tone.wav", "Probe", true, "host");
            log.WriteLine("upload (8 s, 800 Hz, about -48 dBFS): " + upload.Ok + " " + upload.Error);
            if (!upload.Ok) return 2;
            await live.Poll(true);
            var temp = Journal().TempCategory;
            if (temp?.Name is not string tempName) { log.WriteLine("ABORT: the upload made no Temp category"); return 2; }
            var listed = await Settle(c => c.Categories.Any(x => x.Name == tempName));
            var category = listed.Categories.SingleOrDefault(x => x.Name == tempName);
            log.WriteLine($"Temp category in Soundpad: [{tempName}] index {category?.Index}, {category?.Paths.Length ?? 0} entries");
            var sound = live.Snapshot.Library.Sounds.First(x => x.AssetId != null && !x.InternalVariant);

            async Task<(bool Kept, long Ms, string Status)> Cleanup(string label)
            {
                string status = await real.Request("GetPlayStatus()");
                var clock = Stopwatch.StartNew(); await live.CleanupSession(); long ms = clock.ElapsedMilliseconds;
                var now = await ManagedSoundpad.Read(real);
                bool kept = now.Categories.Any(x => x.Name == tempName);
                var journal = Journal();
                log.WriteLine($"cleanup while {label} (Soundpad said {status} just before): took {ms} ms; Temp category {(kept ? "KEPT" : "REMOVED")}; Soundpad now {now.Entries.Length} sounds, {now.Categories.Length} categories; journal tempCategory={(journal.TempCategory == null ? "none" : journal.TempCategory.State)}, issues: {string.Join("; ", journal.Issues.Select(i => i.Code))}");
                return (kept, ms, status);
            }

            // 1. Paused: Soundpad is not stopped, so nothing may be removed.
            var play = await live.Execute(Cmd("play") with { SoundId = sound.Id, LibraryVersion = live.Snapshot.Library.Version }, "host");
            log.WriteLine("play the Temp sound: " + play.Ok + " " + play.Error);
            await Task.Delay(500);
            log.WriteLine("pause: " + await real.Request("DoTogglePause()"));
            await Task.Delay(300);
            var paused = await Cleanup("paused");
            // 2. Playing again.
            log.WriteLine("resume: " + await real.Request("DoTogglePause()"));
            await Task.Delay(400);
            var playing = await Cleanup("playing");
            // 3. Stopped, and still stopped 2 s later: now it goes.
            log.WriteLine("stop: " + await real.Request("DoStopSound()"));
            bool quiet = await RealProbes.StoppedAndQuiet(real);
            log.WriteLine("stopped and quiet for 2 s: " + quiet);
            if (!quiet) { log.WriteLine("ABORT: Soundpad did not stay stopped"); return 1; }
            var stopped = await Cleanup("stopped and quiet");
            bool claim = paused.Status == "PAUSED" && paused.Kept && playing.Status == "PLAYING" && playing.Kept && !stopped.Kept;
            log.WriteLine($"RESULT kept while paused: {paused.Kept} (status {paused.Status}); kept while playing: {playing.Kept} (status {playing.Status}); removed once stopped and quiet: {!stopped.Kept}");
            result = claim ? 0 : 1;
        }
        catch (Exception ex) { log.WriteLine("PROBLEM: " + ex.GetType().Name + ": " + ex.Message); result = result == 0 ? 1 : result; }
        finally
        {
            try
            {
                // If anything was left (a failure above), end the session the careful way once more.
                if (bridge != null && await RealProbes.StoppedAndQuiet(real)) await bridge.CleanupSession();
                bridge?.Dispose();
                var after = await Settle(c => RealProbes.Same(c, before));
                bool same = RealProbes.Same(after, before);
                log.WriteLine($"final: {after.Entries.Length} sounds, {after.Categories.Length} categories (before: {before.Entries.Length}, {before.Categories.Length}); library identical to before: {same}");
                if (!same) { log.WriteLine("CLEANUP PROBLEM: check Soundpad for a \"Temp - Soundpad Web\" category"); if (result == 0) result = 1; }
            }
            catch (Exception ex) { log.WriteLine("CLEANUP PROBLEM: " + ex.Message); }
            await RealProbes.Delete(folder, log);
        }
        return result;
    }
}
