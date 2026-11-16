using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace SoundpadWeb.Testing;

/// <summary>
/// "--real-organize-probe --data-dir &lt;folder&gt; [--spl-watch-s 60]" (2.44 real-hardware proof): Organize's native
/// operations on the REAL Soundpad. Works only in new categories whose names start with "SPW probe &lt;time&gt;", holding
/// about -48 dBFS one-second tones it writes under --data-dir. Nothing is ever played.
/// 1. Raw pipe: where DoAddSound(path, category, -1), (…, 1) and (…, 2) put an entry (All sounds and the category).
/// 2. Raw pipe: a sub-category (DoAddCategory(name, parent)): how Soundpad numbers it, and whether DoAddSound with that
///    index reaches it, as Organize's NativeTree indexes it.
/// 3. A real Bridge on a throwaway data folder: an Organize plan (create the "SPW probe &lt;time&gt;" category, add three
///    probe tones that are already in Soundpad, add one to the sub-category, remove an empty probe category, one guided
///    rename), Review changes, Apply, the duplicate-on-add measurement, the checkpoint, verification by re-reading;
///    then a second plan whose created category is removed again with "remove the categories this plan created".
/// 4. All the time: soundlist.spl's time, size and hash every 250 ms, with --spl-watch-s seconds watched after each change;
///    at the end it waits (up to 330 s) for Soundpad's own next save to put the file back to its start bytes.
/// 5. Cleanup with the probes' rule (Stop, stopped for 2 s, re-read, remove), deepest probe category first, recording
///    what removing a category does to sounds that are also in other categories; then the whole list is compared with
///    the start (exact text of GetSoundlist and GetCategories).
/// Exit code 0: every claim held and the list is as before; 1: a claim failed or the list differs; 2: refused or aborted.
/// </summary>
internal static class RealOrganizeProbe
{
    private sealed record SplEvent(long Ms, DateTime WriteUtc, long Size, string Sha, int Sounds, int Categories);
    /// <summary>Polls soundlist.spl every 250 ms (read-only, shared) and records every change of its time, size or hash.</summary>
    private sealed class SplWatch : IAsyncDisposable
    {
        private readonly string path, soundNeedle, categoryNeedle;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource stop = new();
        private readonly List<SplEvent> events = [];
        private readonly Task loop;
        public int Busy;
        public SplEvent? First;
        public SplWatch(string path, string soundNeedle, string categoryNeedle)
        {
            this.path = path; this.soundNeedle = soundNeedle; this.categoryNeedle = categoryNeedle;
            First = Sample();
            loop = Task.Run(Loop);
        }
        public long Now => clock.ElapsedMilliseconds;
        private SplEvent? Sample()
        {
            try
            {
                var info = new FileInfo(path);
                byte[] bytes;
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[input.Length]; input.ReadExactly(bytes); }
                var text = Encoding.UTF8.GetString(bytes);
                return new(clock.ElapsedMilliseconds, info.LastWriteTimeUtc, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), Count(text, soundNeedle), Count(text, categoryNeedle));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref Busy); return null; }
        }
        private static int Count(string text, string needle) { int n = 0, at = 0; while ((at = text.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; at += needle.Length; } return n; }
        private async Task Loop()
        {
            var last = First;
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { return; }
                var now = Sample();
                if (now == null) continue;
                if (last == null || now.Sha != last.Sha || now.WriteUtc != last.WriteUtc || now.Size != last.Size) { lock (events) events.Add(now); last = now; }
            }
        }
        public SplEvent[] Since(long ms) { lock (events) return [.. events.Where(e => e.Ms >= ms)]; }
        public SplEvent? Latest { get { lock (events) return events.Count > 0 ? events[^1] : First; } }
        public SplEvent[] All { get { lock (events) return [.. events]; } }
        public async ValueTask DisposeAsync() { stop.Cancel(); try { await loop; } catch (OperationCanceledException) { } stop.Dispose(); }
    }

    public static async Task<int> Run(string[] args, string root)
    {
        var log = Console.Out;
        int at = Array.IndexOf(args, "--spl-watch-s");
        int watchS = at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var s) ? Math.Clamp(s, 0, 300) : 60;
        using var real = new SoundpadPipe();
        if (await RealProbes.Refusal(real) is string refused) { log.WriteLine("ABORT: " + refused); return 2; }
        HostHotkeys.ForbidWindowsHotkeys = true;
        var stamp = DateTime.Now.ToString("HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var tag = "SPW probe " + stamp;
        // Short names: Soundpad refuses a path of 260 characters or more, and --data-dir can be deep.
        var folder = Path.Combine(root, "o" + Guid.NewGuid().ToString("N")[..4]);
        Directory.CreateDirectory(folder); folder = SlotFiles.LongPath(folder);
        var wavs = Path.Combine(folder, "w"); var data = Path.Combine(folder, "d");
        Directory.CreateDirectory(wavs); Directory.CreateDirectory(data);
        string startSoundlist = await real.Request("GetSoundlist()"), startCategories = await real.Request("GetCategories(true, false)");
        var start = NativeTree.Parse(startSoundlist, startCategories);
        var startNames = start.Categories.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        log.WriteLine($"probe {tag}; folder {folder} ({folder.Length} chars)");
        log.WriteLine($"start: {start.Entries.Length} sounds, {start.Categories.Count()} categories (+ All sounds); fingerprint {start.Fingerprint[..16]}");
        foreach (var c in start.All) log.WriteLine($"  [{c.Index}] {(c.Builtin ? "(builtin) " : "")}{string.Join(" / ", c.Chain.Append(c.Name))}: {c.Paths.Length} sounds, {c.Children.Length} sub");
        if (start.Categories.Any(c => c.Name.StartsWith("SPW probe", StringComparison.Ordinal))) { log.WriteLine("ABORT: a \"SPW probe\" category is already in Soundpad"); return 2; }
        var spl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Leppsoft", "soundlist.spl");
        await using var watch = new SplWatch(spl, "spwp-", "SPW probe");
        log.WriteLine($"soundlist.spl at start: {watch.First?.Size} bytes, written {watch.First?.WriteUtc:O} (UTC), SHA-256 {watch.First?.Sha}");

        var results = new List<(string Claim, bool? Pass, string Detail)>();
        void Claim(string claim, bool? pass, string detail) { results.Add((claim, pass, detail)); log.WriteLine($"RESULT {(pass == null ? "INFO" : pass.Value ? "PASS" : "FAIL")} {claim}: {detail}"); }
        async Task<NativeTree> Read() => await NativeTree.Read(real);
        async Task<(NativeTree Tree, long Ms)> Settle(Func<NativeTree, bool> done, int maxMs = 5000)
        {
            var clock = Stopwatch.StartNew(); var t = await Read();
            while (!done(t) && clock.ElapsedMilliseconds < maxMs) { await Task.Delay(50); t = await Read(); }
            return (t, clock.ElapsedMilliseconds);
        }
        NativeNode? Named(NativeTree t, string name) => t.Categories.SingleOrDefault(c => c.Name == name);
        string Short(string path) => path.StartsWith(wavs, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(path) : "(owner's)";
        string Order(NativeNode? n) => n == null ? "(missing)" : "[" + string.Join(", ", n.Paths.Select(Short)) + "]";
        // The owner's categories, exactly: where they are, their names and their contents in order.
        string Owners(NativeTree t) => string.Join("\n", t.All.Where(c => !c.Name.StartsWith("SPW probe", StringComparison.Ordinal)).Select(c => c.Index + "|" + string.Join("/", c.Chain) + "|" + c.Name + "|" + string.Join(";", c.Paths)));
        string OwnerEntries(NativeTree t) => string.Join("\n", t.Entries.Where(e => !e.Path.StartsWith(wavs, StringComparison.OrdinalIgnoreCase)).Select(e => e.Index + "|" + e.Path));
        var ownersAtStart = Owners(start); var ownerEntriesAtStart = OwnerEntries(start);
        async Task Watch(string label, long mark)
        {
            if (watchS == 0) return;
            log.WriteLine($"WATCH {label}: watching soundlist.spl for {watchS} s");
            await Task.Delay(watchS * 1000);
            var seen = watch.Since(mark);
            if (seen.Length == 0) log.WriteLine($"SPL {label}: not written in {watchS} s after the change (still {watch.Latest?.Size} bytes, written {watch.Latest?.WriteUtc:HH:mm:ss.fff} UTC)");
            foreach (var e in seen) log.WriteLine($"SPL {label}: written +{e.Ms - mark} ms after the change: {e.Size} bytes, write time {e.WriteUtc:HH:mm:ss.fff} UTC, sha {e.Sha[..12]}, holds {e.Sounds} probe-file mentions and {e.Categories} probe-category mentions");
        }

        // The probe tones: one second each, about -48 dBFS, at different pitches.
        var file = new Dictionary<string, string>();
        int hz = 500;
        foreach (var k in new[] { "a", "b", "c", "d", "e" })
        {
            var path = Path.Combine(wavs, "spwp-" + k + ".wav");
            File.WriteAllBytes(path, MimicRing.Wav(ExactPlaybackAnalysis.Tones(48000, ExactPlaybackAnalysis.QuietLevel, (hz, 1000)), 48000));
            file[k] = path; hz += 150;
        }
        string nest = tag + " nest", src = tag + " src", child = tag + " child", empty = tag + " (empty, x)", made = tag, again = tag + " R";
        Bridge? bridge = null; bool aborted = false;
        try
        {
            async Task<NativeTree> AddCategory(string name, int parent, string label)
            {
                long mark = watch.Now;
                var clock = Stopwatch.StartNew();
                var reply = await real.Request($"DoAddCategory({ManagedSoundpad.Quote(name)}, {parent})");
                long answered = clock.ElapsedMilliseconds;
                var (t, ms) = await Settle(x => x.Categories.Any(c => !startNames.Contains(c.Name) && c.Name == name));
                var node = Named(t, name);
                log.WriteLine($"{label}: DoAddCategory(\"{name}\", {parent}) answered {reply} in {answered} ms; listed after {ms} ms: {(node == null ? "NOT LISTED" : $"index {node.Index}, parents [{string.Join(" / ", node.Chain)}], root {node.Root}")}");
                if (node == null) throw new InvalidDataException("the category did not appear: " + name);
                return t;
            }
            async Task<NativeTree> AddSound(string key, string category, int position, string label)
            {
                var before = await Read();
                var target = Named(before, category) ?? throw new InvalidDataException("no category " + category);
                var path = file[key];
                var clock = Stopwatch.StartNew();
                var reply = await real.Request($"DoAddSound({ManagedSoundpad.Quote(path)}, {target.Index}, {position})");
                long answered = clock.ElapsedMilliseconds;
                var (t, ms) = await Settle(x => Named(x, category)?.Paths.Contains(path, StringComparer.OrdinalIgnoreCase) == true && x.Count(path) > 0);
                var entry = t.Entries.Where(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Select(e => e.Index).ToArray();
                var elsewhere = t.Categories.Where(c => c.Name != category && c.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)).Select(c => c.Name).ToArray();
                log.WriteLine($"{label}: DoAddSound(spwp-{key}, {target.Index} = \"{category}\", {position}) answered {reply} in {answered} ms; settled after {ms} ms; All sounds entries for it: {entry.Length} (index {string.Join(", ", entry)}; list has {t.Entries.Length}); \"{category}\" now {Order(Named(t, category))}; also in: [{string.Join(", ", elsewhere)}]");
                return t;
            }

            // ---- 1. Where DoAddSound puts an entry ----
            long m = watch.Now;
            await AddCategory(nest, -1, "A nest parent");
            await Watch("after the first DoAddCategory", m);
            var t1 = await AddCategory(src, -1, "B source category");
            int nestBefore = Named(t1, nest)!.Index, srcBefore = Named(t1, src)!.Index;
            m = watch.Now;
            var t2 = await AddSound("a", src, -1, "C first add at -1");
            bool q1 = Named(t2, src)!.Paths.SequenceEqual([file["a"]], StringComparer.OrdinalIgnoreCase) && t2.Count(file["a"]) == 1 && t2.Entries.Length == start.Entries.Length + 1 && t2.Entries.Single(e => e.Path.Equals(file["a"], StringComparison.OrdinalIgnoreCase)).Index == start.Entries.Max(e => e.Index) + 1;
            Claim("Q1 DoAddSound(path, index, -1) of a new file is accepted and lands in that category, listed once at the end of All sounds", q1, $"src {Order(Named(t2, src))}, All sounds {t2.Entries.Length}");
            await Watch("after the first DoAddSound", m);
            m = watch.Now;
            await AddSound("b", src, -1, "D add at -1");
            await AddSound("c", src, 1, "D add at 1");
            var t3 = await AddSound("d", src, 2, "D add at 2");
            var order = Named(t3, src)!.Paths.Select(Short).ToArray();
            Claim("Q1 insert positions: -1 = end, 1 = first, 2 = second", order.SequenceEqual(["spwp-c.wav", "spwp-d.wav", "spwp-a.wav", "spwp-b.wav"]), "src order " + Order(Named(t3, src)) + " (expected [c, d, a, b] if positions are 1-based inserts)");
            await Watch("after three more DoAddSound", m);

            // ---- 2. A sub-category ----
            m = watch.Now;
            var t4 = await AddCategory(child, Named(t3, nest)!.Index, "E sub-category");
            var childNode = Named(t4, child)!; var nestNode = Named(t4, nest)!; var srcNode = Named(t4, src)!;
            string numbering = childNode.Index == nestNode.Index + 1 && srcNode.Index == srcBefore + 1 ? "depth first (the child right after its parent; later categories move up by one)"
                : childNode.Index == t4.All.Max(c => c.Index) && srcNode.Index == srcBefore ? "appended (the child gets the next free index; others keep theirs)" : "other";
            Claim("Q3 sub-category numbering", null, $"nest {nestBefore}->{nestNode.Index}, src {srcBefore}->{srcNode.Index}, child {childNode.Index} with parents [{string.Join(" / ", childNode.Chain)}], ParentIndex {childNode.ParentIndex}: {numbering}");
            Claim("Q3 the owner's categories keep their indexes when a probe sub-category is made", Owners(t4) == ownersAtStart, "");
            Claim("Q3 NativeTree places the sub-category under its parent", childNode.Chain.SequenceEqual([nest]) && nestNode.Children.Any(c => c.Name == child) && childNode.ParentIndex == nestNode.Index, "");
            var t5 = await AddSound("e", child, -1, "E add into the sub-category by the index NativeTree read");
            bool reached = Named(t5, child)!.Paths.SequenceEqual([file["e"]], StringComparer.OrdinalIgnoreCase) && Named(t5, src)!.Paths.Length == 4 && Named(t5, nest)!.Paths.Length == 0;
            Claim("Q3 DoAddSound with the sub-category's index (as NativeTree reads it) reaches that sub-category", reached, $"child {Order(Named(t5, child))}, nest {Order(Named(t5, nest))}, src {Order(Named(t5, src))}");
            await Watch("after the sub-category and its sound", m);
            await AddCategory(empty, -1, "F empty category with ( ) and , in its name");

            // ---- 3. Organize through a real Bridge ----
            var live = new Bridge(real, new StateStore(data));
            bridge = live;
            live.MimicLog += message => log.WriteLine("  companion log: " + message);
            live.UseMimicCapture(new MimicFixture()); live.UseTranscriptEngine(new TranscriptFixture()); live.UseHotkeys(key => new HotkeyFixture(key));
            await live.Poll(true);
            Command Cmd(string op, object? body = null) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op) { Body = body == null ? null : JsonSerializer.SerializeToElement(body, Json.Options) };
            JsonElement Data(Reply r) => JsonSerializer.SerializeToElement(r.Data ?? new { }, Json.Options);
            async Task<JsonElement> Get() { var r = await live.Execute(Cmd("organizeGet"), "host"); if (!r.Ok) throw new InvalidDataException("organizeGet: " + r.Error); return Data(r); }
            IEnumerable<Category> Flat(IEnumerable<Category> cats) => cats.SelectMany(c => Flat(c.Children).Prepend(c));
            string SoundId(string key) => live.Snapshot.Library.Sounds.Single(x => x.Name.Contains("spwp-" + key, StringComparison.OrdinalIgnoreCase)).Id;
            string CatId(string name) => Flat(live.Snapshot.Library.Categories).Single(c => c.Name == name).Id;
            void Show(string label, JsonElement view)
            {
                var plan = view.GetProperty("plan");
                log.WriteLine($"{label}: plan {plan.GetProperty("state").GetString()}{(plan.GetProperty("paused").ValueKind == JsonValueKind.String ? " (paused: " + plan.GetProperty("paused").GetString() + ")" : "")}, addBehaviour {plan.GetProperty("addBehaviour").GetString()}, revision {plan.GetProperty("revision").GetInt64()}");
                if (plan.GetProperty("pausedText").ValueKind == JsonValueKind.String) log.WriteLine("  paused text: " + plan.GetProperty("pausedText").GetString());
                foreach (var st in plan.GetProperty("steps").EnumerateArray())
                {
                    string Text(string n) => st.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                    var target = st.TryGetProperty("target", out var tg) && tg.ValueKind == JsonValueKind.Object ? tg.GetProperty("name").GetString() + " (id " + (tg.GetProperty("categoryId").ValueKind == JsonValueKind.String ? tg.GetProperty("categoryId").GetString() : "gone") + ")" : "";
                    var added = st.TryGetProperty("addedToAllSounds", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? " addedToAllSounds=" + a.GetBoolean() : "";
                    log.WriteLine($"  step {Text("kind")} {Text("soundName")}{Text("name")} -> {target}{(Text("targetStepId").Length > 0 ? "(new category)" : "")}: {Text("state")} / {Text("support")}: {Text("reason")}{(Text("note").Length > 0 ? " Note: " + Text("note") : "")}{added}");
                }
                if (view.TryGetProperty("checkpoint", out var cp) && cp.ValueKind == JsonValueKind.Object) log.WriteLine($"  checkpoint: {cp.GetProperty("copy").GetString()}, taken {cp.GetProperty("takenAtUtcMs")}");
                if (view.TryGetProperty("recovery", out var rc) && rc.ValueKind == JsonValueKind.Object) log.WriteLine("  recovery: " + rc.GetRawText());
            }
            async Task<JsonElement> ApplyAndWait(string label)
            {
                var view = await Get();
                var clock = Stopwatch.StartNew();
                var reply = await live.Execute(Cmd("organizeApply", new { planId = view.GetProperty("plan").GetProperty("id").GetString(), planRevision = view.GetProperty("revision").GetInt64() }), "host");
                log.WriteLine($"{label}: organizeApply ok={reply.Ok} {reply.Error} {(reply.Data == null ? "" : Data(reply).GetRawText())} (answered in {clock.ElapsedMilliseconds} ms)");
                if (!reply.Ok) throw new InvalidDataException("Apply refused: " + reply.Error);
                await live.OrganizeApplyIdle().WaitAsync(TimeSpan.FromSeconds(120));
                log.WriteLine($"{label}: Apply finished or paused after {clock.ElapsedMilliseconds} ms");
                return await Get();
            }

            var beforePlan = await Read();
            var edit = await live.Execute(Cmd("organizeEdit", new
            {
                planRevision = 0,
                add = new object[]
                {
                    new { kind = "createCategory", name = made, key = "k" },
                    new { kind = "addSound", soundId = SoundId("a"), targetKey = "k" },
                    new { kind = "addSound", soundId = SoundId("b"), targetKey = "k" },
                    new { kind = "addSound", soundId = SoundId("c"), targetKey = "k" },
                    new { kind = "addSound", soundId = SoundId("a"), categoryId = CatId(child), categoryName = child, categoryParents = new[] { nest } },
                    new { kind = "removeCategory", categoryId = CatId(empty), categoryName = empty },
                    new { kind = "renameCategory", categoryId = CatId(src), categoryName = src, name = tag + " renamed" },
                },
            }), "host");
            log.WriteLine($"organizeEdit ok={edit.Ok} {edit.Error}");
            if (!edit.Ok) throw new InvalidDataException("the plan was refused: " + edit.Error);
            var review = await Get();
            Show("Review changes before Apply", review);
            var supports = review.GetProperty("plan").GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("kind").GetString() + ":" + x.GetProperty("support").GetString()).ToArray();
            Claim("Review changes classifies the steps (native: create, adds, empty removal; guided: rename)", supports.SequenceEqual(["createCategory:native", "addSound:native", "addSound:native", "addSound:native", "addSound:native", "removeCategory:native", "renameCategory:manual"]), string.Join(", ", supports));
            string splBeforeApply = watch.Latest?.Sha ?? "";
            m = watch.Now;
            var view1 = await ApplyAndWait("Apply 1");
            Show("after Apply 1", view1);
            bool pausedForDuplicates = view1.GetProperty("plan").GetProperty("paused").ValueKind == JsonValueKind.String && view1.GetProperty("plan").GetProperty("paused").GetString() == "duplicates";
            var mid = await Read();
            log.WriteLine($"after Apply 1: \"{made}\" {Order(Named(mid, made))}; All sounds {mid.Entries.Length}; entries for spwp-a: {mid.Count(file["a"])}");
            var duplicateAnswer = view1.GetProperty("plan").GetProperty("addBehaviour").GetString();
            Claim("Q2 adding a file already in Soundpad to a second category adds an All sounds entry", null, $"Organize measured addBehaviour={duplicateAnswer}; All sounds {beforePlan.Entries.Length} -> {mid.Entries.Length}; spwp-a entries {beforePlan.Count(file["a"])} -> {mid.Count(file["a"])}; plan paused for duplicates: {pausedForDuplicates}");
            JsonElement view2 = view1;
            if (pausedForDuplicates)
            {
                var cont = await live.Execute(Cmd("organizeResolve", new { resolution = "continue" }), "host");
                log.WriteLine($"Continue: ok={cont.Ok} {cont.Error}");
                await live.OrganizeApplyIdle().WaitAsync(TimeSpan.FromSeconds(120));
                view2 = await Get();
                Show("after Continue", view2);
            }
            var states = view2.GetProperty("plan").GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("state").GetString()).ToArray();
            var applied = await Read();
            bool contents = Named(applied, made)?.Paths.SequenceEqual([file["a"], file["b"], file["c"]], StringComparer.OrdinalIgnoreCase) == true
                && Named(applied, child)?.Paths.SequenceEqual([file["e"], file["a"]], StringComparer.OrdinalIgnoreCase) == true
                && Named(applied, empty) == null && Named(applied, src)?.Paths.Length == 4 && Named(applied, made)?.Root == true;
            Claim("Q5 Apply: every native step verified, the guided step left to the host, plan done", states.SequenceEqual(["verified", "verified", "verified", "verified", "verified", "verified", "manual"]) && view2.GetProperty("plan").GetProperty("state").GetString() == "done", string.Join(", ", states) + "; plan " + view2.GetProperty("plan").GetProperty("state").GetString());
            Claim("Q5 re-read: the new category holds a, b, c in order; the sub-category e, a; the empty category is gone; src unchanged", contents, $"\"{made}\" {Order(Named(applied, made))}, child {Order(Named(applied, child))}, src {Order(Named(applied, src))}, empty {(Named(applied, empty) == null ? "gone" : "STILL THERE")}");
            var addFlags = view2.GetProperty("plan").GetProperty("steps").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "addSound").Select(x => x.TryGetProperty("addedToAllSounds", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean().ToString() : "none").ToArray();
            int grew = applied.Entries.Length - beforePlan.Entries.Length;
            Claim("Q2 each add step's \"added to All sounds\" matches what Soundpad did", addFlags.All(f => f == "True") == (grew == addFlags.Length) && addFlags.Count(f => f == "True") == grew, $"All sounds grew by {grew} for {addFlags.Length} adds; the steps say [{string.Join(", ", addFlags)}]");
            Claim("Q5 the owner's categories and entries are untouched by Apply", Owners(applied) == ownersAtStart && OwnerEntries(applied) == ownerEntriesAtStart, "");
            // The checkpoint.
            var planId = view2.GetProperty("plan").GetProperty("id").GetString()!;
            var checkpoints = Path.Combine(data, "organize", "checkpoints");
            var json = Path.Combine(checkpoints, planId + ".v1.json"); var bak = Path.Combine(checkpoints, planId + ".spl.bak");
            if (File.Exists(json) && File.Exists(bak))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
                var cpTree = NativeTree.Parse(doc.RootElement.GetProperty("soundlist").GetString()!, doc.RootElement.GetProperty("categories").GetString()!);
                var bakSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bak)));
                bool bakHasProbe = Encoding.UTF8.GetString(File.ReadAllBytes(bak)).Contains("SPW probe", StringComparison.Ordinal);
                Claim("Q5 checkpoint: the tree before Apply is recorded exactly", cpTree.Fingerprint == beforePlan.Fingerprint, $"checkpoint fingerprint {cpTree.Fingerprint[..12]}, read before Apply {beforePlan.Fingerprint[..12]}");
                Claim("Q5 checkpoint: the .spl.bak is soundlist.spl as Soundpad last saved it", bakSha == splBeforeApply, $"bak {bakSha[..12]} vs the file just before Apply {splBeforeApply[..Math.Min(12, splBeforeApply.Length)]}; start {watch.First?.Sha[..12]}; the .spl.bak {(bakHasProbe ? "already holds" : "does NOT hold")} the probe categories made before Apply");
            }
            else Claim("Q5 checkpoint written", false, $"json {File.Exists(json)}, spl.bak {File.Exists(bak)}");
            await Watch("after Apply", m);

            // A second plan: a created category removed again ("Remove the empty categories this plan created").
            var view3 = await Get();
            var edit2 = await live.Execute(Cmd("organizeEdit", new { planRevision = view3.GetProperty("revision").GetInt64(), add = new object[] { new { kind = "createCategory", name = again } } }), "host");
            log.WriteLine($"organizeEdit 2 ok={edit2.Ok} {edit2.Error}");
            var view4 = await ApplyAndWait("Apply 2");
            Show("after Apply 2", view4);
            var withAgain = await Read();
            log.WriteLine($"\"{again}\" after Apply 2: {(Named(withAgain, again) is { } r ? $"index {r.Index}, root {r.Root}" : "MISSING")}");
            var clock2 = Stopwatch.StartNew();
            var removeCreated = await live.Execute(Cmd("organizeResolve", new { resolution = "removeCreated" }), "host");
            log.WriteLine($"removeCreated ok={removeCreated.Ok} {removeCreated.Error} {(removeCreated.Data == null ? "" : Data(removeCreated).GetRawText())} in {clock2.ElapsedMilliseconds} ms");
            var afterRemove = await Read();
            Claim("Q5 removeCreated removes the plan's empty created category, and only it", removeCreated.Ok && Named(afterRemove, again) == null && Named(afterRemove, made) != null && Owners(afterRemove) == ownersAtStart, $"\"{again}\" {(Named(afterRemove, again) == null ? "gone" : "STILL THERE")}, \"{made}\" {(Named(afterRemove, made) == null ? "GONE" : "kept (holds sounds)")}");
            Show("after removeCreated", await Get());
            live.Dispose(); bridge = null;
        }
        catch (Exception ex) { aborted = true; log.WriteLine("PROBLEM: " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            try { bridge?.Dispose(); } catch (Exception ex) { log.WriteLine("dispose: " + ex.Message); }
            // ---- 5. Cleanup: deepest probe category first, each after Stop, 2 s stopped and a re-read ----
            try
            {
                bool first = true;
                for (int round = 0; round < 12; round++)
                {
                    var now = await Read();
                    var mine = now.Categories.Where(c => !startNames.Contains(c.Name) && c.Name.StartsWith(tag, StringComparison.Ordinal)).OrderByDescending(c => c.Chain.Length).ThenByDescending(c => c.Index).ToArray();
                    if (mine.Length == 0) break;
                    var victim = mine[0];
                    if (victim.Children.Length > 0 || victim.Paths.Any(p => !p.StartsWith(wavs, StringComparison.OrdinalIgnoreCase))) { log.WriteLine("LEFT IN PLACE: " + victim.Name + " holds something that is not the probe's"); break; }
                    if (!await RealProbes.StoppedAndQuiet(real)) { log.WriteLine("CLEANUP PROBLEM: Soundpad did not stay stopped"); break; }
                    var fresh = await Read();
                    var node = fresh.Categories.SingleOrDefault(c => c.Name == victim.Name && c.Chain.SequenceEqual(victim.Chain));
                    if (node == null || node.Children.Length > 0 || node.Paths.Any(p => !p.StartsWith(wavs, StringComparison.OrdinalIgnoreCase))) { log.WriteLine("cleanup: " + victim.Name + " changed; reading again"); continue; }
                    var held = node.Paths.ToArray();
                    long mark = watch.Now;
                    var reply = await real.Request($"DoRemoveCategory({node.Index})");
                    var (after, ms) = await Settle(x => !x.Categories.Any(c => c.Name == node.Name && c.Chain.SequenceEqual(node.Chain)));
                    var fate = held.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => $"{Short(p)}: All sounds {fresh.Count(p)}->{after.Count(p)}, in [{string.Join(", ", after.Categories.Where(c => c.Paths.Contains(p, StringComparer.OrdinalIgnoreCase)).Select(c => c.Name.Replace(tag, "~", StringComparison.Ordinal)))}]");
                    log.WriteLine($"cleanup: DoRemoveCategory({node.Index}) \"{node.Name}\" ({held.Length} sounds) answered {reply}; gone after {ms} ms; All sounds {fresh.Entries.Length}->{after.Entries.Length}; {string.Join("; ", fate)}");
                    if (first) { first = false; await Watch("after the first category removal", mark); }
                }
                // A probe file left in All sounds outside every category goes into a fresh probe category, which is removed.
                var rest = await Read();
                var loose = rest.Entries.Where(e => e.Path.StartsWith(wavs, StringComparison.OrdinalIgnoreCase)).Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (loose.Length > 0 && !rest.Categories.Any(c => c.Name.StartsWith(tag, StringComparison.Ordinal)))
                {
                    log.WriteLine($"cleanup: {loose.Length} probe files are still in All sounds: {string.Join(", ", loose.Select(Short))}; collecting them in a probe category");
                    var name = tag + " cleanup";
                    await real.Request($"DoAddCategory({ManagedSoundpad.Quote(name)}, -1)");
                    var (withCleanup, _) = await Settle(x => Named(x, name) != null);
                    foreach (var p in loose) await real.Request($"DoAddSound({ManagedSoundpad.Quote(p)}, {Named(withCleanup, name)!.Index}, -1)");
                    var (filled, _) = await Settle(x => loose.All(p => Named(x, name)?.Paths.Contains(p, StringComparer.OrdinalIgnoreCase) == true));
                    if (await RealProbes.StoppedAndQuiet(real))
                    {
                        var node = Named(await Read(), name);
                        if (node != null && node.Paths.All(p => p.StartsWith(wavs, StringComparison.OrdinalIgnoreCase))) log.WriteLine("cleanup: remove " + name + ": " + await real.Request($"DoRemoveCategory({node.Index})"));
                    }
                    _ = filled;
                }
                var (final, _) = await Settle(x => x.Categories.Count() == start.Categories.Count() && x.Entries.Length == start.Entries.Length, 5000);
                string endSoundlist = await real.Request("GetSoundlist()"), endCategories = await real.Request("GetCategories(true, false)");
                bool exact = endSoundlist == startSoundlist && endCategories == startCategories;
                bool structure = Owners(final) == ownersAtStart && OwnerEntries(final) == ownerEntriesAtStart && final.Entries.Length == start.Entries.Length && final.Categories.Count() == start.Categories.Count();
                Claim("cleanup: Soundpad's list is exactly as at the start (GetSoundlist and GetCategories text identical)", exact, $"{final.Entries.Length} sounds, {final.Categories.Count()} categories (start {start.Entries.Length}, {start.Categories.Count()}); same categories, entries and order: {structure}");
                long mark2 = watch.Now;
                await Watch("after cleanup", mark2);
                // Soundpad saves soundlist.spl on its own timer, every 5 minutes and only when the list changed
                // (measured 2026-11-15), so the file can still hold probe categories: wait for the next save.
                var waitFile = Stopwatch.StartNew();
                if (exact) while (watch.Latest?.Sha != watch.First?.Sha && waitFile.ElapsedMilliseconds < 330000) await Task.Delay(500);
                var last = watch.Latest;
                Claim("soundlist.spl is saved back to exactly its start bytes by Soundpad's own next save", last?.Sha == watch.First?.Sha, $"now {last?.Sha[..12]} ({last?.Size} bytes, written {last?.WriteUtc:HH:mm:ss.fff} UTC), start {watch.First?.Sha[..12]}; read-busy samples {watch.Busy}");
                log.WriteLine("SPL timeline (every write seen, ms since the probe started):");
                foreach (var e in watch.All) log.WriteLine($"  +{e.Ms} ms: {e.Size} bytes, write {e.WriteUtc:HH:mm:ss.fff} UTC, sha {e.Sha[..12]}, probe files {e.Sounds}, probe categories {e.Categories}");
            }
            catch (Exception ex) { log.WriteLine("CLEANUP PROBLEM: " + ex.GetType().Name + ": " + ex.Message); results.Add(("cleanup", false, ex.Message)); }
            await RealProbes.Delete(folder, log);
        }
        log.WriteLine($"SUMMARY {results.Count(r => r.Pass == true)} pass, {results.Count(r => r.Pass == false)} fail, {results.Count(r => r.Pass == null)} info{(aborted ? ", ABORTED" : "")}");
        return aborted ? 2 : results.Any(r => r.Pass == false) ? 1 : 0;
    }
}
