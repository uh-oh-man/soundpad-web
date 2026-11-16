using System.Diagnostics;
using NAudio.CoreAudioApi;
using SoundpadWeb;
using SoundpadWeb.Testing;

/// <summary>
/// Tools for the real-hardware proof of Mimic's capture into the recording history (2.44): a real companion listens
/// to an output's loopback while these tools feed Soundpad and watch what it plays. Nothing here opens a microphone
/// or any capture endpoint: they read render endpoints' session meters (no stream), and record Soundpad's own output
/// through process loopback (an output stream).
/// <list type="bullet">
/// <item><c>--real-audio-sessions [--watch-ms N]</c>: every active output, the default ones, and each app session on
/// it with its state, volume and (watched for N ms) its highest peak. Read-only.</item>
/// <item><c>--real-mimic-feed --data-dir D --source A [--peak-db X] [--lead-ms N]</c>: a quiet WAV copy of A (peak
/// X dBFS, default -24), added to a new probe category, played once through Soundpad while its output is recorded,
/// then the probe category is removed with the 2 s quiet rule and the list is compared with the start.</item>
/// <item><c>--real-soundpad-listen --data-dir D --seconds N --out W [--ref R] [--pid P]</c>: records Soundpad's own output
/// (or process P's) for N seconds, and with a reference file says where in the recording it was heard and how exactly.</item>
/// </list>
/// </summary>
internal static class RealMimicCaptureProbe
{
    private const int Rate = 48000;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Stamp => DateTime.Now.ToString("HH:mm:ss.fff");
    private static string? Arg(string[] args, string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    public static async Task<int> Sessions(string[] args)
    {
        int watch = int.TryParse(Arg(args, "--watch-ms"), out int w) ? Math.Clamp(w, 0, 60000) : 0;
        using var devices = new MMDeviceEnumerator();
        string Default(Role role) { try { using var d = devices.GetDefaultAudioEndpoint(DataFlow.Render, role); return d.ID; } catch (Exception) { return "?"; } }
        var defaults = new Dictionary<string, string> { ["console"] = Default(Role.Console), ["multimedia"] = Default(Role.Multimedia), ["communications"] = Default(Role.Communications) };
        var endpoints = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var peaks = new Dictionary<string, float>();
        var rows = new List<(string Key, string Text)>();
        foreach (var d in endpoints)
        {
            var roles = string.Join(",", defaults.Where(p => p.Value == d.ID).Select(p => p.Key));
            Console.WriteLine($"OUTPUT {d.FriendlyName} | id {d.ID} | flow {d.DataFlow} | default: {(roles.Length == 0 ? "-" : roles)}");
            var sessions = d.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                uint pid = s.GetProcessID; string name;
                try { name = pid == 0 ? "(system)" : Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { name = "(gone)"; }
                Console.WriteLine($"  session pid {pid} {name} state {s.State} volume {s.SimpleAudioVolume.Volume:0.00} mute {s.SimpleAudioVolume.Mute} peak {s.AudioMeterInformation.MasterPeakValue:0.0000}");
            }
        }
        if (watch > 0)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < watch)
            {
                foreach (var d in endpoints)
                {
                    var sessions = d.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i]; var key = d.FriendlyName + " / pid " + s.GetProcessID;
                        float p = s.AudioMeterInformation.MasterPeakValue;
                        peaks[key] = Math.Max(peaks.GetValueOrDefault(key), p);
                    }
                }
                await Task.Delay(20);
            }
            foreach (var (key, peak) in peaks.OrderByDescending(p => p.Value)) Console.WriteLine($"WATCH max peak {peak:0.0000} ({(peak > 0 ? 20 * Math.Log10(peak) : -999):0.0} dBFS) {key}");
        }
        foreach (var d in endpoints) d.Dispose();
        return 0;
    }

    /// <summary>Decodes a file to mono at 48 kHz (nearest-sample resampling is enough for a speech clip).</summary>
    private static float[] Mono48(string path)
    {
        using var decoder = new DecodedAudio(path);
        var all = new List<float>(); var buffer = new float[4096 * decoder.Channels]; int read;
        while ((read = decoder.Read(buffer)) > 0)
            for (int i = 0; i + decoder.Channels <= read; i += decoder.Channels)
            {
                float sum = 0; for (int c = 0; c < decoder.Channels; c++) sum += buffer[i + c];
                all.Add(sum / decoder.Channels);
            }
        if (decoder.Rate == Rate) return [.. all];
        return MimicTranscriber.Resample([.. all], decoder.Rate, Rate);
    }

    private static (double Peak, double Rms) Level(ReadOnlySpan<float> x)
    {
        double peak = 0, sum = 0; foreach (var v in x) { peak = Math.Max(peak, Math.Abs(v)); sum += (double)v * v; }
        return (peak, x.Length == 0 ? 0 : Math.Sqrt(sum / x.Length));
    }
    private static string Db(double v) => v > 0 ? (20 * Math.Log10(v)).ToString("0.0") : "-inf";

    /// <summary>First and last 10 ms window whose RMS is above <paramref name="floor"/>, in frames.</summary>
    private static (int First, int Last)? Active(float[] data, double floor)
    {
        int window = Rate / 100, first = -1, last = -1;
        for (int at = 0; at + window <= data.Length; at += window)
            if (Level(data.AsSpan(at, window)).Rms > floor) { if (first < 0) first = at; last = at + window; }
        return first < 0 ? null : (first, last);
    }

    public static async Task<int> Feed(string[] args, string root)
    {
        var source = Arg(args, "--source") ?? throw new InvalidDataException("--source <audio file> is required.");
        double peakDb = double.TryParse(Arg(args, "--peak-db"), System.Globalization.CultureInfo.InvariantCulture, out var p) ? Math.Clamp(p, -60, -12) : -24;
        int leadMs = int.TryParse(Arg(args, "--lead-ms"), out int l) ? Math.Clamp(l, 0, 3000) : 300;
        var soundpad = Process.GetProcessesByName("Soundpad").FirstOrDefault();
        if (soundpad == null) { Console.WriteLine("ABORT: Soundpad is not running."); return 2; }
        using var real = new SoundpadPipe();
        var status = await real.Request("GetPlayStatus()");
        if (status != "STOPPED") { Console.WriteLine("ABORT: Soundpad is busy (" + status + ")."); return 2; }
        async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { var c = await ManagedSoundpad.Read(real); for (int i = 0; i < 100 && !done(c); i++) { await Task.Delay(150); c = await ManagedSoundpad.Read(real); } return c; }

        // The quiet copy: mono speech scaled to the chosen peak, a little digital silence before it, stereo 16-bit 48 kHz.
        var speech = Mono48(source);
        var (srcPeak, _) = Level(speech);
        float gain = (float)(Math.Pow(10, peakDb / 20) / Math.Max(1e-6, srcPeak));
        int lead = leadMs * Rate / 1000;
        var stereo = new float[(lead + speech.Length) * 2];
        for (int i = 0; i < speech.Length; i++) stereo[(lead + i) * 2] = stereo[(lead + i) * 2 + 1] = speech[i] * gain;
        var (filePeak, fileRms) = Level(speech.Select(x => x * gain).ToArray());
        var tag = DateTime.Now.ToString("HH.mm.ss");
        var folder = Path.Combine(root, "feed");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "spw-mimic-" + tag.Replace(".", "") + ".wav");
        File.WriteAllBytes(file, SliceWav.Encode(stereo, Rate, 2));
        double fileMs = (lead + speech.Length) * 1000.0 / Rate;
        Console.WriteLine($"file: {file} ({file.Length} chars), {fileMs:0} ms, speech from {leadMs} ms, peak {Db(filePeak)} dBFS, RMS {Db(fileRms)} dBFS");

        var name = "SPW probe " + tag + " mimic";
        var before = await ManagedSoundpad.Read(real);
        Console.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
        bool added = false; int code = 0;
        using var capture = new WasapiMimicCapture();
        var ring = new MimicRing(capture.Rate, 60);
        try
        {
            Console.WriteLine("add category: " + await real.Request($"DoAddCategory(\"{name}\", -1)")); added = true;
            var ready = await Settle(c => c.Categories.Any(x => x.Name == name));
            int category = ready.Categories.Single(c => c.Name == name).Index;
            var reply = await real.Request($"DoAddSound(\"{file}\", {category}, -1)");
            ready = await Settle(c => c.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase)));
            var entry = ready.Entries.SingleOrDefault(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
            if (!reply.StartsWith("R-200", StringComparison.Ordinal) || entry == null) { Console.WriteLine("ABORT: Soundpad did not list the file: " + reply); return 2; }
            Console.WriteLine($"added: index {entry.Index} in category {category} ({reply})");
            await capture.Start(new("app:" + soundpad.Id, "Soundpad", "app"), ring);
            await Task.Delay(400);
            capture.Pump(); long at = ring.Written; long wall = Now;
            Console.WriteLine($"PLAY at {Stamp} (unix ms {wall}): " + await real.Request($"DoPlaySound({entry.Index})"));
            await Task.Delay((int)fileMs + 1200);
            capture.Pump();
            var heard = ring.Read(at, ring.Written);
            var active = Active(heard, 0.0005);
            var (hp, hr) = Level(heard);
            if (active is { } a)
            {
                var (ap, ar) = Level(heard.AsSpan(a.First, a.Last - a.First));
                Console.WriteLine($"RESULT heard from Soundpad: sound from {a.First * 1000.0 / Rate:0} ms to {a.Last * 1000.0 / Rate:0} ms after the command ({(a.Last - a.First) * 1000.0 / Rate:0} ms; speech in the file {speech.Length * 1000.0 / Rate:0} ms), peak {Db(ap)} dBFS, RMS while sounding {Db(ar)} dBFS (file peak {Db(filePeak)}): Soundpad's output gain {Db(ap / filePeak)} dB");
                Console.WriteLine($"RESULT speech window (wall clock): {DateTimeOffset.FromUnixTimeMilliseconds(wall + (long)(a.First * 1000.0 / Rate)).ToLocalTime():HH:mm:ss.fff} to {DateTimeOffset.FromUnixTimeMilliseconds(wall + (long)(a.Last * 1000.0 / Rate)).ToLocalTime():HH:mm:ss.fff}");
            }
            else { Console.WriteLine($"RESULT nothing heard from Soundpad (peak {Db(hp)} dBFS)"); code = 3; }
        }
        finally
        {
            try { capture.Stop(); } catch (Exception) { }
            try
            {
                bool quiet = await RealProbes.StoppedAndQuiet(real);
                if (added && !quiet) Console.WriteLine("CLEANUP PROBLEM: Soundpad did not stay stopped; the probe category was left in place: " + name);
                if (added && quiet)
                {
                    var now = await Settle(c => c.Categories.Any(x => x.Name == name));
                    var mine = now.Categories.SingleOrDefault(c => c.Name == name);
                    if (mine != null && mine.Paths.All(x => x.Equals(file, StringComparison.OrdinalIgnoreCase))) Console.WriteLine($"remove probe category ({Stamp}, stopped {RealProbes.QuietMs} ms): " + await real.Request($"DoRemoveCategory({mine.Index})"));
                    else if (mine != null) Console.WriteLine("LEFT IN PLACE: the probe category holds something unexpected; remove it by hand: " + name);
                }
                var after = await Settle(c => !c.Categories.Any(x => x.Name == name) && !c.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase)));
                var gone = !after.Categories.Any(x => x.Name == name) && !after.Entries.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"final: {after.Entries.Length} sounds, {after.Categories.Length} categories (before: {before.Entries.Length}, {before.Categories.Length}); probe category and entry gone: {gone}; list identical to before: {RealProbes.Same(before, after)}");
                if (!gone) code = Math.Max(code, 1);
            }
            catch (Exception ex) { Console.WriteLine("CLEANUP PROBLEM: " + ex.Message + " — check Soundpad for the category \"" + name + "\"."); code = 1; }
        }
        return code;
    }

    /// <summary>Records Soundpad's own output and, with a reference, finds it in the recording by cross-correlation.</summary>
    public static async Task<int> Listen(string[] args, string root)
    {
        int seconds = int.TryParse(Arg(args, "--seconds"), out int s) ? Math.Clamp(s, 1, 60) : 10;
        var output = Arg(args, "--out") ?? Path.Combine(root, "listen.wav");
        var reference = Arg(args, "--ref");
        // --pid N records that process tree instead of Soundpad (the companion's window plays the host's private preview).
        int pid = int.TryParse(Arg(args, "--pid"), out int given) ? given : Process.GetProcessesByName("Soundpad").FirstOrDefault()?.Id ?? 0;
        if (pid == 0) { Console.WriteLine("ABORT: Soundpad is not running."); return 2; }
        using var capture = new WasapiMimicCapture();
        var ring = new MimicRing(capture.Rate, seconds + 5);
        await capture.Start(new("app:" + pid, "pid " + pid, "app"), ring);
        capture.Pump(); long at = ring.Written;
        Console.WriteLine($"LISTENING at {Stamp} (unix ms {Now}) for {seconds} s");
        await Task.Delay(seconds * 1000);
        capture.Pump();
        var heard = ring.Read(at, ring.Written);
        capture.Stop();
        File.WriteAllBytes(output, MimicRing.Wav(heard, Rate));
        var active = Active(heard, 0.0005);
        Console.WriteLine($"recorded {heard.Length * 1000.0 / Rate:0} ms to {output}; " + (active is { } a ? $"sound from {a.First * 1000.0 / Rate:0} to {a.Last * 1000.0 / Rate:0} ms, peak {Db(Level(heard).Peak)} dBFS" : "nothing heard"));
        if (reference == null || active == null) return active == null ? 3 : 0;
        var refMono = Mono48(reference);
        Console.WriteLine(Compare(heard, refMono));
        return 0;
    }

    /// <summary>
    /// "--real-render-tone --device &lt;name part&gt; [--db -48] [--ms 1500]": plays a quiet tone straight to one output
    /// (not through Soundpad) while a fresh loopback of that same output listens, to tell whether the output itself
    /// still delivers loopback audio (2.44 proof: the companion's long-running capture of it went silent).
    /// </summary>
    public static async Task<int> RenderTone(string[] args)
    {
        var part = Arg(args, "--device") ?? throw new InvalidDataException("--device <part of an output's name> is required.");
        double db = double.TryParse(Arg(args, "--db"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, -60, -30) : -48;
        int ms = int.TryParse(Arg(args, "--ms"), out int m) ? Math.Clamp(m, 200, 5000) : 1500;
        using var devices = new MMDeviceEnumerator();
        var found = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Where(x => x.FriendlyName.Contains(part, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count != 1) { Console.WriteLine($"ABORT: {found.Count} outputs match \"{part}\"."); return 2; }
        var device = found[0];
        WasapiMimicCapture.RequireOutput(device.ID, device.DataFlow);
        using var capture = new WasapiMimicCapture();
        var ring = new MimicRing(capture.Rate, 20);
        await capture.Start(new("output:" + device.ID, "Output: " + device.FriendlyName, "output"), ring);
        await Task.Delay(300);
#pragma warning disable CS0618 // a plain shared-mode render stream for the test tone
        using var output = new NAudio.Wave.WasapiOut(device, AudioClientShareMode.Shared, true, 50);
#pragma warning restore CS0618
        var tone = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 1000, Gain = Math.Pow(10, db / 20) };
        output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.OffsetSampleProvider(tone) { TakeSamples = 48000 * 2 * ms / 1000 }));
        capture.Pump(); long at = ring.Written;
        Console.WriteLine($"TONE on {device.FriendlyName} at {Stamp} (unix ms {Now}): 1000 Hz, {db:0} dBFS, {ms} ms");
        output.Play();
        await Task.Delay(ms + 700);
        output.Stop();
        capture.Pump();
        var heard = ring.Read(at, ring.Written);
        capture.Stop();
        var active = Active(heard, Math.Pow(10, (db - 12) / 20));
        Console.WriteLine($"RESULT fresh loopback of {device.FriendlyName}: {capture.Packets} packets; " + (active is { } a ? $"tone heard from {a.First * 1000.0 / Rate:0} to {a.Last * 1000.0 / Rate:0} ms, peak {Db(Level(heard).Peak)} dBFS" : $"nothing heard (peak {Db(Level(heard).Peak)} dBFS)"));
        return active == null ? 3 : 0;
    }

    /// <summary>
    /// Where <paramref name="reference"/> lies in <paramref name="heard"/> (normalised cross-correlation, coarse at 1 ms
    /// then exact), the gain between them, and how much of the heard sound the gained reference explains.
    /// </summary>
    internal static string Compare(float[] heard, float[] reference)
    {
        if (reference.Length == 0 || heard.Length < reference.Length) return "COMPARE: the recording is shorter than the reference";
        double Corr(int lag, int step)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 0; i < reference.Length; i += step) { double x = reference[i], y = heard[lag + i]; xy += x * y; xx += x * x; yy += y * y; }
            return xx <= 0 || yy <= 0 ? 0 : xy / Math.Sqrt(xx * yy);
        }
        int best = 0; double bestC = double.MinValue;
        for (int lag = 0; lag + reference.Length <= heard.Length; lag += 48) { double c = Corr(lag, 4); if (c > bestC) { bestC = c; best = lag; } }
        int coarse = best; bestC = double.MinValue;
        for (int lag = Math.Max(0, coarse - 96); lag <= Math.Min(heard.Length - reference.Length, coarse + 96); lag++) { double c = Corr(lag, 1); if (c > bestC) { bestC = c; best = lag; } }
        double xy = 0, xx = 0; for (int i = 0; i < reference.Length; i++) { xy += reference[i] * heard[best + i]; xx += (double)reference[i] * reference[i]; }
        double gain = xx > 0 ? xy / xx : 0, err = 0, sig = 0;
        for (int i = 0; i < reference.Length; i++) { double e = heard[best + i] - gain * reference[i]; err += e * e; sig += (double)heard[best + i] * heard[best + i]; }
        // Anything heard outside the matched stretch (a leak before it or a tail after it).
        double outside = 0; int outsideFrames = 0;
        for (int i = 0; i < heard.Length; i++) if (i < best || i >= best + reference.Length) { outside = Math.Max(outside, Math.Abs(heard[i])); outsideFrames++; }
        return $"COMPARE: reference {reference.Length * 1000.0 / Rate:0.0} ms found at {best * 1000.0 / Rate:0.0} ms into the recording; correlation {bestC:0.0000}; gain {Db(gain)} dB; residual {Db(Math.Sqrt(err / Math.Max(1e-30, sig)))} dB relative to the heard signal; loudest sample outside the matched stretch {Db(outside)} dBFS";
    }
}
