using System.Diagnostics;
using SoundpadWeb;

/// <summary>
/// Owner-approved net-zero probe on the real Soundpad (2.36): does Soundpad read a library file again each time it
/// is played? Exact word and region playback rests on that (reusable "slot" files rewritten before each play).
/// Everything written is pure digital silence, so nothing can be heard in a game or call. It adds one category
/// with two slot files, measures, removes the category (Soundpad removes its entries with it) and checks that
/// the library is exactly as before. Refuses to run unless Soundpad is idle.
/// </summary>
internal static class RealSlotProbe
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);
    private static string LongPath(string path) { var buffer = new System.Text.StringBuilder(1024); uint n = GetLongPathName(path, buffer, 1024); return n > 0 && n < 1024 ? buffer.ToString() : path; }
    public static async Task<int> Run()
    {
        if (Process.GetProcessesByName("SoundpadWeb").Length > 0) { Console.WriteLine("ABORT: the companion is running. Close it first."); return 2; }
        if (Process.GetProcessesByName("VRChat").Length > 0) { Console.WriteLine("ABORT: VRChat is running; this probe waits for a quiet moment."); return 2; }
        using var real = new SoundpadPipe();
        string status;
        try { status = await real.Request("GetPlayStatus()"); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { Console.WriteLine("ABORT: Soundpad is not reachable (" + ex.GetType().Name + ")."); return 2; }
        if (status != "STOPPED") { Console.WriteLine("ABORT: Soundpad is busy (" + status + ")."); return 2; }

        async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { var c = await ManagedSoundpad.Read(real); for (int i = 0; i < 100 && !done(c); i++) { await Task.Delay(150); c = await ManagedSoundpad.Read(real); } return c; }
        var tag = Guid.NewGuid().ToString("N")[..8];
        // Windows can hand out TEMP in its short 8.3 form, which Soundpad reports back expanded: use the long form.
        var folder = Path.Combine(LongPath(Path.GetTempPath()), "soundpad-web-slot-probe-" + tag);
        var name = "Soundpad Web slot probe [" + tag + "]";
        var slots = new[] { Path.Combine(folder, "slot-1.wav"), Path.Combine(folder, "slot-2.wav") };
        var before = await ManagedSoundpad.Read(real);
        Console.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
        bool added = false;
        try
        {
            Directory.CreateDirectory(folder);
            byte[] Silence(int ms) { var wav = SliceWav.Silence(SliceRenderer.FrameOf(ms, 48000), 48000, 2); if (!SliceWav.IsDigitalSilence(wav)) throw new InvalidOperationException("not silence"); return wav; }
            foreach (var slot in slots) File.WriteAllBytes(slot, Silence(400));
            Console.WriteLine("add category: " + await real.Request($"DoAddCategory(\"{name}\", -1)")); added = true;
            var withCategory = await Settle(c => c.Categories.Any(x => x.Name == name));
            int category = withCategory.Categories.Single(c => c.Name == name).Index;
            var ready = withCategory;
            foreach (var slot in slots)
            {
                Console.WriteLine("add slot: " + await real.Request($"DoAddSound(\"{slot}\", {category}, 1)"));
                ready = await Settle(c => c.Entries.Any(e => e.Path.Equals(slot, StringComparison.OrdinalIgnoreCase)));
                if (!ready.Entries.Any(e => e.Path.Equals(slot, StringComparison.OrdinalIgnoreCase))) { Console.WriteLine("ABORT: Soundpad did not list the slot; it reports: " + string.Join(" | ", ready.Entries.Where(e => e.Path.Contains(tag, StringComparison.OrdinalIgnoreCase)).Select(e => e.Path))); return 2; }
            }
            int IndexOf(string path) => ready.Entries.Single(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Index;
            Console.WriteLine($"slots registered at indexes {IndexOf(slots[0])} and {IndexOf(slots[1])}");
            async Task Play(string path) { var reply = await real.Request($"DoPlaySound({IndexOf(path)})"); if (!reply.StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("play refused: " + reply); }
            async Task<long?> Duration() => long.TryParse(await real.Request("GetPlaybackDurationInMs()"), out var d) && d > 0 ? d : null;
            Task<string> Status() => real.Request("GetPlayStatus()");
            async Task<long> UntilStopped(int limitMs) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < limitMs && await Status() != "STOPPED") await Task.Delay(10); return clock.ElapsedMilliseconds; }

            // 1. The main question.
            var result = await SlotProbe.Run(Play, Duration, Status, slots[0]);
            Console.WriteLine($"RESULT reread: {result.Outcome} — {result.Detail}");
            Console.WriteLine($"  reported {result.FirstReportedMs} ms then {result.SecondReportedMs} ms; busy {result.FirstPlayedMs} ms then {result.SecondPlayedMs} ms; write attempts {result.FirstWriteAttempts}, {result.SecondWriteAttempts}");
            await UntilStopped(3000);

            // 2. How exactly does a short file end, and how soon after the command is it "playing"?
            foreach (int ms in new[] { 150, 500, 1200 })
            {
                var write = await SlotPool.WriteInPlace(slots[0], Silence(ms), 6, 25, default);
                var clock = Stopwatch.StartNew(); await Play(slots[0]); long accepted = clock.ElapsedMilliseconds;
                long? firstBusy = null; while (clock.ElapsedMilliseconds < 400 && firstBusy == null) { if (await Status() != "STOPPED") firstBusy = clock.ElapsedMilliseconds; }
                long? reported = await Duration();
                long stopped = accepted + await UntilStopped(ms + 3000);
                Console.WriteLine($"RESULT length {ms} ms: write ok={write.Ok} attempts={write.Attempts}; play accepted after {accepted} ms; first seen busy at {firstBusy?.ToString() ?? "never"} ms; reported duration {reported?.ToString() ?? "none"} ms; stopped again {stopped} ms after the command");
            }

            // 3. Can a slot be rewritten while it plays, and how soon after another slot interrupts it?
            await SlotPool.WriteInPlace(slots[0], Silence(3000), 6, 25, default);
            await SlotPool.WriteInPlace(slots[1], Silence(3000), 6, 25, default);
            await Play(slots[0]); await Task.Delay(300);
            var whilePlaying = await SlotPool.WriteInPlace(slots[0], Silence(700), 3, 25, default);
            Console.WriteLine($"RESULT rewrite while that slot plays: ok={whilePlaying.Ok} attempts={whilePlaying.Attempts} error={whilePlaying.Error}; status now {await Status()}, reported duration {await Duration()}");
            await real.Request("DoStopSound()"); await UntilStopped(2000);
            await SlotPool.WriteInPlace(slots[0], Silence(3000), 6, 25, default);
            await Play(slots[0]); await Task.Delay(300);
            var release = Stopwatch.StartNew(); await Play(slots[1]);
            var afterInterrupt = await SlotPool.WriteInPlace(slots[0], Silence(600), 40, 10, default);
            Console.WriteLine($"RESULT rewrite of slot 1 right after slot 2 interrupted it: ok={afterInterrupt.Ok} attempts={afterInterrupt.Attempts} after {release.ElapsedMilliseconds} ms error={afterInterrupt.Error}");
            await real.Request("DoStopSound()"); await UntilStopped(2000);
            var afterStop = Stopwatch.StartNew(); await Play(slots[1]); await Task.Delay(200); await real.Request("DoStopSound()");
            var stoppedWrite = await SlotPool.WriteInPlace(slots[1], Silence(600), 40, 10, default);
            Console.WriteLine($"RESULT rewrite right after Stop: ok={stoppedWrite.Ok} attempts={stoppedWrite.Attempts} after {afterStop.ElapsedMilliseconds} ms");

            // 4. Twenty rapid alternating rewrites and plays, as two people competing would cause.
            int failures = 0; var times = new List<long>();
            for (int i = 0; i < 20; i++)
            {
                var slot = slots[i % 2]; int ms = 300 + i * 20; var clock = Stopwatch.StartNew();
                var write = await SlotPool.WriteInPlace(slot, Silence(ms), 40, 10, default);
                if (!write.Ok) { failures++; continue; }
                await Play(slot); long? reported = null;
                for (int look = 0; look < 12 && (reported == null || Math.Abs(reported.Value - ms) > 60); look++) reported = await Duration();
                if (reported == null || Math.Abs(reported.Value - ms) > 60) failures++;
                times.Add(clock.ElapsedMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"RESULT 20 alternating rewrite+play rounds: {failures} wrong or blocked; write+play+confirm took median {times[times.Count / 2]} ms, max {times[^1]} ms");
            await real.Request("DoStopSound()"); await UntilStopped(2000);
            return result.Reread && failures == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                if (await real.Request("GetPlayStatus()") != "STOPPED") await real.Request("DoStopSound()");
                if (added)
                {
                    var now = await Settle(c => c.Categories.Any(x => x.Name == name));
                    var mine = now.Categories.SingleOrDefault(c => c.Name == name);
                    // Only ever remove the probe's own category, and only if everything in it is one of the probe's slots.
                    if (mine != null && mine.Paths.All(p => p.Contains("soundpad-web-slot-probe-" + tag, StringComparison.OrdinalIgnoreCase))) Console.WriteLine("remove probe category: " + await real.Request($"DoRemoveCategory({mine.Index})"));
                    else if (mine != null) Console.WriteLine("LEFT IN PLACE: the probe category holds something unexpected; remove it by hand: " + name);
                }
                var after = await Settle(c => !c.Categories.Any(x => x.Name == name) && c.Entries.Length == before.Entries.Length);
                bool same = after.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).SequenceEqual(before.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                    && after.Categories.OrderBy(c => c.Index).Select(c => c.Name).SequenceEqual(before.Categories.OrderBy(c => c.Index).Select(c => c.Name));
                Console.WriteLine($"final: {after.Entries.Length} sounds, {after.Categories.Length} categories (before: {before.Entries.Length}, {before.Categories.Length}); library identical to before: {same}");
            }
            catch (Exception ex) { Console.WriteLine("CLEANUP PROBLEM: " + ex.Message + " — check Soundpad for the category \"" + name + "\"."); }
            for (int i = 0; i < 40 && Directory.Exists(folder); i++) { try { Directory.Delete(folder, true); } catch (IOException) { await Task.Delay(150); } catch (UnauthorizedAccessException) { await Task.Delay(150); } }
            if (Directory.Exists(folder)) Console.WriteLine("probe folder still in use: " + folder);
        }
    }
}
