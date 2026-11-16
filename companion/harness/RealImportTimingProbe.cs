using System.Diagnostics;
namespace SoundpadWeb.Testing;

/// <summary>
/// "--real-import-timing-probe --data-dir &lt;folder&gt;" (2.44 real-hardware proof): how soon after DoAddSound does the real
/// Soundpad list a new file, in its main list and in its category? <see cref="ManagedSoundpad.Import"/> reads once,
/// straight after the add. Adds one category with three 100 ms digital-silence files (what new slot files hold) and
/// one 3 s silence file, one at a time, reading the list right after each add and then every 25 ms until it shows
/// the file in both places. Nothing is played. Then the category is removed (after the probes' quiet wait) and the
/// list compared with before.
/// </summary>
internal static class RealImportTimingProbe
{
    public static async Task<int> Run(string root)
    {
        var log = Console.Out;
        using var real = new SoundpadPipe();
        if (await RealProbes.Refusal(real) is string refused) { log.WriteLine("ABORT: " + refused); return 2; }
        var tag = Guid.NewGuid().ToString("N")[..8];
        var folder = Path.Combine(root, "soundpad-web-import-probe-" + tag);
        Directory.CreateDirectory(folder); folder = SlotFiles.LongPath(folder);
        var name = "Soundpad Web import probe [" + tag + "]";
        var before = await ManagedSoundpad.Read(real);
        log.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
        bool added = false; int result = 2;
        async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { var c = await ManagedSoundpad.Read(real); for (int i = 0; i < 100 && !done(c); i++) { await Task.Delay(150); c = await ManagedSoundpad.Read(real); } return c; }
        try
        {
            var files = new[] { ("slot-a.wav", 100), ("slot-b.wav", 100), ("slot-c.wav", 100), ("longer.wav", 3000) }
                .Select(f => (Path: Path.Combine(folder, f.Item1), Ms: f.Item2)).ToArray();
            foreach (var (path, ms) in files) File.WriteAllBytes(path, SliceWav.Silence(SliceRenderer.FrameOf(ms, 48000), 48000, 2));
            log.WriteLine("add category: " + await real.Request($"DoAddCategory(\"{name}\", -1)")); added = true;
            var ready = await Settle(c => c.Categories.Any(x => x.Name == name));
            int category = ready.Categories.Single(c => c.Name == name).Index;
            bool allAtOnce = true;
            foreach (var (path, ms) in files)
            {
                var clock = Stopwatch.StartNew();
                var reply = await real.Request($"DoAddSound(\"{path}\", {category}, 1)");
                long answered = clock.ElapsedMilliseconds;
                long? inList = null, inCategory = null; int reads = 0; bool firstRead = false;
                while (clock.ElapsedMilliseconds < 10000 && (inList == null || inCategory == null))
                {
                    var c = await ManagedSoundpad.Read(real); reads++;
                    bool listed = c.Entries.Count(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) == 1;
                    bool categorised = c.Categories.Any(x => x.Index == category && x.Name == name && x.Paths.Contains(path, StringComparer.OrdinalIgnoreCase));
                    if (reads == 1) firstRead = listed && categorised;
                    if (listed) inList ??= clock.ElapsedMilliseconds;
                    if (categorised) inCategory ??= clock.ElapsedMilliseconds;
                    if (inList == null || inCategory == null) await Task.Delay(25);
                }
                allAtOnce &= firstRead;
                log.WriteLine($"RESULT {Path.GetFileName(path)} ({ms} ms silence): DoAddSound answered {reply} after {answered} ms; the first read right after it showed the file {(firstRead ? "in the list and its category" : "NOT in both")}; listed after {inList?.ToString() ?? "never"} ms, in its category after {inCategory?.ToString() ?? "never"} ms ({reads} reads)");
            }
            log.WriteLine($"RESULT the read straight after DoAddSound (what ManagedSoundpad.Import does) found every file: {allAtOnce}");
            result = allAtOnce ? 0 : 1;
        }
        finally
        {
            try
            {
                bool quiet = await RealProbes.StoppedAndQuiet(real);
                if (added && quiet)
                {
                    var now = await Settle(c => c.Categories.Any(x => x.Name == name));
                    var mine = now.Categories.SingleOrDefault(c => c.Name == name);
                    if (mine != null && mine.Paths.All(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) log.WriteLine("remove probe category: " + await real.Request($"DoRemoveCategory({mine.Index})"));
                    else if (mine != null) log.WriteLine("LEFT IN PLACE: the probe category holds something unexpected; remove it by hand: " + name);
                }
                else if (added) log.WriteLine("CLEANUP PROBLEM: Soundpad did not stay stopped; the probe category was left in place: " + name);
                var after = await Settle(c => RealProbes.Same(c, before));
                log.WriteLine($"final: {after.Entries.Length} sounds, {after.Categories.Length} categories (before: {before.Entries.Length}, {before.Categories.Length}); library identical to before: {RealProbes.Same(after, before)}");
            }
            catch (Exception ex) { log.WriteLine("CLEANUP PROBLEM: " + ex.Message); }
            await RealProbes.Delete(folder, log);
        }
        return result;
    }
}
