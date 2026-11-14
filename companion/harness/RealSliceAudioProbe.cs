using System.Diagnostics;
using SoundpadWeb;

/// <summary>
/// Owner-approved net-zero probe on the real Soundpad (2.36): what does Soundpad actually put out when a slot
/// file is rewritten and played? It captures Soundpad's own output (process loopback: an output stream, never a
/// microphone) while playing very quiet test tones (about -48 dBFS) from two slot files, and measures: does the
/// sound follow the file's new contents, is the very start and end of the file played, how long after the command
/// does sound begin, how much of the beginning leaks with the old play-then-seek method, and how one play
/// interrupts another. It adds one category, removes it again and checks the library is exactly as before.
/// Refuses to run while the companion or VRChat is running or Soundpad is busy.
/// </summary>
internal static class RealSliceAudioProbe
{
    private const int Rate = 48000;
    private const float Level = 0.004f;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);
    private static string LongPath(string path) { var buffer = new System.Text.StringBuilder(1024); uint n = GetLongPathName(path, buffer, 1024); return n > 0 && n < 1024 ? buffer.ToString() : path; }

    /// <summary>Tone segments (frequency Hz, length ms) back to back, stereo 16-bit, with no fades: edges are exact.</summary>
    private static byte[] Tones(params (double Hz, int Ms)[] parts)
    {
        var samples = new List<float>();
        foreach (var (hz, ms) in parts)
        {
            long frames = SliceRenderer.FrameOf(ms, Rate);
            for (long i = 0; i < frames; i++) { float v = hz <= 0 ? 0 : Level * MathF.Sin((float)(2 * Math.PI * hz * i / Rate)); samples.Add(v); samples.Add(v); }
        }
        return SliceWav.Encode(samples.ToArray(), Rate, 2);
    }
    /// <summary>Power of one frequency in a window (Goertzel), relative to the window's total power (0..1).</summary>
    private static (double Share, double Rms) Tone(float[] data, int start, int length, double hz)
    {
        if (start < 0 || start + length > data.Length) return (0, 0);
        double w = 2 * Math.PI * hz / Rate, coeff = 2 * Math.Cos(w), s0, s1 = 0, s2 = 0, energy = 0;
        for (int i = 0; i < length; i++) { double x = data[start + i]; energy += x * x; s0 = x + coeff * s1 - s2; s2 = s1; s1 = s0; }
        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return (energy <= 0 ? 0 : Math.Min(1, 2 * power / (length * energy)), Math.Sqrt(energy / length));
    }
    /// <summary>First and last frame (in the capture) where this frequency dominates, scanning 5 ms windows every 1 ms.</summary>
    private static (int First, int Last, int TotalMs)? Span(float[] data, double hz, double floor)
    {
        int window = Rate / 200, hop = Rate / 1000, first = -1, last = -1, count = 0;
        for (int at = 0; at + window <= data.Length; at += hop)
        {
            var (share, rms) = Tone(data, at, window, hz);
            if (rms > floor && share > 0.6) { if (first < 0) first = at; last = at + window; count++; }
        }
        return first < 0 ? null : (first, last, count);
    }
    private static string Ms(int frames) => (frames * 1000.0 / Rate).ToString("0.0");

    public static async Task<int> Run()
    {
        if (Process.GetProcessesByName("SoundpadWeb").Length > 0) { Console.WriteLine("ABORT: the companion is running. Close it first."); return 2; }
        if (Process.GetProcessesByName("VRChat").Length > 0) { Console.WriteLine("ABORT: VRChat is running; this probe waits for a quiet moment."); return 2; }
        var soundpad = Process.GetProcessesByName("Soundpad").FirstOrDefault();
        if (soundpad == null) { Console.WriteLine("ABORT: Soundpad is not running."); return 2; }
        using var real = new SoundpadPipe();
        string status;
        try { status = await real.Request("GetPlayStatus()"); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { Console.WriteLine("ABORT: Soundpad is not reachable (" + ex.GetType().Name + ")."); return 2; }
        if (status != "STOPPED") { Console.WriteLine("ABORT: Soundpad is busy (" + status + ")."); return 2; }

        async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { var c = await ManagedSoundpad.Read(real); for (int i = 0; i < 100 && !done(c); i++) { await Task.Delay(150); c = await ManagedSoundpad.Read(real); } return c; }
        var tag = Guid.NewGuid().ToString("N")[..8];
        var folder = Path.Combine(LongPath(Path.GetTempPath()), "soundpad-web-slot-probe-" + tag);
        var name = "Soundpad Web slot probe [" + tag + "]";
        var files = new[] { Path.Combine(folder, "slot-1.wav"), Path.Combine(folder, "slot-2.wav"), Path.Combine(folder, "slot-3.wav") };
        var before = await ManagedSoundpad.Read(real);
        Console.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
        bool added = false;
        using var capture = new WasapiMimicCapture();
        var probeRing = new MimicRing(capture.Rate, 120);
        MimicRing CaptureRing() { capture.Pump(); return probeRing; }
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var file in files) File.WriteAllBytes(file, SliceWav.Silence(Rate / 4, Rate, 2));
            Console.WriteLine("add category: " + await real.Request($"DoAddCategory(\"{name}\", -1)")); added = true;
            var ready = await Settle(c => c.Categories.Any(x => x.Name == name));
            int category = ready.Categories.Single(c => c.Name == name).Index;
            foreach (var file in files)
            {
                var reply = await real.Request($"DoAddSound(\"{file}\", {category}, 1)");
                ready = await Settle(c => c.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase)));
                if (!reply.StartsWith("R-200", StringComparison.Ordinal) || !ready.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) { Console.WriteLine("ABORT: Soundpad did not list " + Path.GetFileName(file) + ": " + reply); return 2; }
            }
            int IndexOf(string path) => ready.Entries.Single(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Index;
            async Task Write(string path, byte[] wav) { var w = await SlotPool.WriteInPlace(path, wav, 80, 10, default); if (!w.Ok) throw new IOException("slot write blocked: " + w.Error); }
            async Task<long> Play(string path) { long at = CaptureRing().Written; var reply = await real.Request($"DoPlaySound({IndexOf(path)})"); if (!reply.StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("play refused: " + reply); return at; }
            await capture.Start(new("app:" + soundpad.Id, "Soundpad", "app"), probeRing);
            if (!capture.Running) { Console.WriteLine("ABORT: could not capture Soundpad's output: " + capture.Status); return 2; }
            await Task.Delay(400);
            float[] Captured(long from, int ms) { var ring = CaptureRing(); long end = Math.Min(ring.Written, from + (long)ms * Rate / 1000); return end > from ? ring.Read(from, end) : []; }
            async Task Quiet() { await real.Request("DoStopSound()"); await Task.Delay(350); }

            // Noise floor of the capture while nothing plays.
            long idleAt = CaptureRing().Written; await Task.Delay(300);
            var idle = Captured(idleAt, 300); double floor = Math.Max(1e-5, Math.Sqrt(idle.Select(x => (double)x * x).DefaultIfEmpty(0).Average()) * 4);
            Console.WriteLine($"capture: {capture.Status}, {capture.Packets} packets, idle level {floor / 4:0.000000}");

            // T1: does the sound follow the file's contents?
            await Write(files[0], Tones((1000, 600)));
            long t = await Play(files[0]); await Task.Delay(1000);
            var first = Captured(t, 1000); var a1 = Span(first, 1000, floor); var b1 = Span(first, 2500, floor);
            await Quiet();
            await Write(files[0], Tones((2500, 600)));
            t = await Play(files[0]); await Task.Delay(1000);
            var second = Captured(t, 1000); var a2 = Span(second, 1000, floor); var b2 = Span(second, 2500, floor);
            Console.WriteLine($"RESULT content: first play heard 1000 Hz for {a1?.TotalMs ?? 0} ms and 2500 Hz for {b1?.TotalMs ?? 0} ms; after rewriting the same file, 1000 Hz for {a2?.TotalMs ?? 0} ms and 2500 Hz for {b2?.TotalMs ?? 0} ms");
            bool follows = a1 != null && b1 == null && b2 != null && a2 == null;
            if (a1 == null && b2 == null) { Console.WriteLine("INCONCLUSIVE: nothing was captured from Soundpad's output (peak " + first.Select(Math.Abs).DefaultIfEmpty(0).Max().ToString("0.000000") + "). It may have no output device right now."); return 3; }
            await Quiet();

            // T2: are the very first and last milliseconds of the file played? Markers of 20 ms at both ends.
            await Write(files[0], Tones((3000, 20), (1000, 400), (3000, 20)));
            var onsets = new List<double>();
            for (int round = 0; round < 5; round++)
            {
                t = await Play(files[0]); await Task.Delay(900);
                var data = Captured(t, 900); var marker = Span(data, 3000, floor); var body = Span(data, 1000, floor);
                if (marker == null || body == null) { Console.WriteLine($"RESULT edges round {round + 1}: marker or body not found"); continue; }
                // Leading marker: the 3000 Hz windows before the body starts; trailing: after it ends.
                int lead = Math.Max(0, body.Value.First - marker.Value.First), trail = Math.Max(0, marker.Value.Last - body.Value.Last), whole = marker.Value.Last - marker.Value.First;
                onsets.Add(marker.Value.First * 1000.0 / Rate);
                Console.WriteLine($"RESULT edges round {round + 1}: sound began {Ms(marker.Value.First)} ms after the command; leading marker {Ms(lead)} ms (file: 20), body {Ms(body.Value.Last - body.Value.First)} ms (file: 400), trailing marker {Ms(trail)} ms (file: 20), whole {Ms(whole)} ms (file: 440)");
                await Quiet();
            }
            if (onsets.Count > 0) Console.WriteLine($"RESULT onset: command to first sound, {onsets.Count} plays: min {onsets.Min():0.0} ms, median {onsets.OrderBy(x => x).ElementAt(onsets.Count / 2):0.0} ms, max {onsets.Max():0.0} ms (includes capture buffering)");

            // T3: the old method. Start a library file, then seek to the middle: how much of the beginning is heard?
            await Write(files[2], Tones((700, 1000), (1900, 1000)));
            for (int round = 0; round < 3; round++)
            {
                t = await Play(files[2]);
                // The companion's startAt today: sample status/position/duration, then seek.
                await real.Request("GetPlayStatus()"); await real.Request("GetPlaybackPositionInMs()"); await real.Request("GetPlaybackDurationInMs()");
                var seek = await real.Request("DoSeekMs(1000)");
                await Task.Delay(1400);
                var data = Captured(t, 1400); var leak = Span(data, 700, floor); var wanted = Span(data, 1900, floor);
                Console.WriteLine($"RESULT play-then-seek round {round + 1} (seek {seek}): heard {leak?.TotalMs ?? 0} ms of the beginning before the wanted part; wanted part began {(wanted == null ? "never" : Ms(wanted.Value.First))} ms after the command and lasted {(wanted == null ? "0" : Ms(wanted.Value.Last - wanted.Value.First))} ms (file: 1000)");
                await Quiet();
            }
            // The same request as an exact slice in a slot.
            await Write(files[1], Tones((1900, 1000)));
            t = await Play(files[1]); await Task.Delay(1400);
            { var data = Captured(t, 1400); var leak = Span(data, 700, floor); var wanted = Span(data, 1900, floor); Console.WriteLine($"RESULT slice in a slot: heard {leak?.TotalMs ?? 0} ms of the beginning; wanted part began {(wanted == null ? "never" : Ms(wanted.Value.First))} ms after the command and lasted {(wanted == null ? "0" : Ms(wanted.Value.Last - wanted.Value.First))} ms (file: 1000)"); }
            await Quiet();

            // T4: one play interrupting another, and Stop.
            await Write(files[0], Tones((1000, 2500)));
            await Write(files[1], Tones((2500, 500)));
            for (int round = 0; round < 3; round++)
            {
                t = await Play(files[0]); await Task.Delay(400);
                long second2 = await Play(files[1]); await Task.Delay(900);
                var data = Captured(t, 1300); int offset = (int)(second2 - t);
                var old = Span(data, 1000, floor); var fresh = Span(data, 2500, floor);
                Console.WriteLine($"RESULT interrupt round {round + 1}: old sound ended {(old == null ? "?" : Ms(old.Value.Last - offset))} ms after the new command; new sound began {(fresh == null ? "never" : Ms(fresh.Value.First - offset))} ms after it and lasted {(fresh == null ? "0" : Ms(fresh.Value.Last - fresh.Value.First))} ms (file: 500)");
                await Quiet();
            }
            for (int round = 0; round < 3; round++)
            {
                t = await Play(files[0]); await Task.Delay(400);
                long stopAt = CaptureRing().Written; await real.Request("DoStopSound()"); await Task.Delay(500);
                var data = Captured(t, 900); var old = Span(data, 1000, floor);
                Console.WriteLine($"RESULT stop round {round + 1}: sound ended {(old == null ? "?" : Ms(old.Value.Last - (int)(stopAt - t)))} ms after Stop was sent");
                await Task.Delay(200);
            }
            Console.WriteLine("RESULT follows file contents: " + follows);
            return follows ? 0 : 1;
        }
        finally
        {
            try { capture.Stop(); } catch (Exception) { }
            try
            {
                if (await real.Request("GetPlayStatus()") != "STOPPED") await real.Request("DoStopSound()");
                if (added)
                {
                    var now = await Settle(c => c.Categories.Any(x => x.Name == name));
                    var mine = now.Categories.SingleOrDefault(c => c.Name == name);
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
