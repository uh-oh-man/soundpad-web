using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Whisper.net;
namespace SoundpadWeb.Testing;

// Word-timing evaluation, the stages beyond the accuracy tables: round trip (does a cut-out word come back
// as that word?), rolling re-transcription (do word times move when the same audio is recognised in
// different chunks?), a real-speech sample judged without labels, and CPU cost.

public static partial class WordTimingEval
{
    // ---- round trip --------------------------------------------------------------------------------------------

    private sealed record Variant(string Name, string? Method, double PadBefore, double PadAfter);
    private static readonly Variant[] RoundTripVariants =
    [
        new("ground truth", null, 0, 0), new($"ground truth +{PadBefore}/{PadAfter}", null, PadBefore, PadAfter),
        new("P phrase, proportional", "P phrase, proportional", 0, 0), new("D phrase + signal DP", "D phrase + signal DP", 0, 0),
        new("A token", "A token", 0, 0), new("C one word", "C one word", 0, 0),
        new(Best, Best, 0, 0), new($"{Best} +{PadBefore}/{PadAfter}", Best, PadBefore, PadAfter), new($"{Best} +40/40", Best, 40, 40), new($"{Best} +80/80", Best, 80, 80),
    ];

    /// <summary>Cuts [from, to] out, puts 300 ms of silence around it, and asks Whisper (today's settings) what it hears.</summary>
    private static async Task<(bool Exact, bool Contains, string Heard)> Heard(float[] speech16k, double fromMs, double toMs, string norm, string? cacheKey)
    {
        var raw = await engine.Run(WordMode.Phrase, WordTimingSignal.SliceWithSilence(speech16k, fromMs, toMs), threads, cacheKey);
        var words = string.Join(" ", raw.Segments.Select(s => s.Text)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(WordTimingMethods.Normalize).Where(w => w.Length > 0).ToList();
        // "twenty five" as two words is the same as "25".
        bool exact = words.Count == 1 && words[0] == norm || WordTimingMethods.Normalize(string.Join(" ", words)) == norm || string.Concat(words) == norm;
        return (exact, exact || words.Contains(norm), string.Join(" ", words));
    }

    private static async Task RoundTrip()
    {
        // A fixed sample: a third of the fixtures (balanced over voices, speeds and sentences), two words each on clean
        // audio and, from a different third, one word each at pink noise +10 dB.
        var picks = new List<(EvalItem Item, int Word)>();
        foreach (var f in fixtures.Take(limit))
        {
            int bucket = (f.Sentence + f.RateName.Length + f.Voice.Length) % 3, n = f.Words.Length, seed = Seed(f.Id);
            if (bucket == 0) { picks.Add((new(f, "clean"), seed % n)); if (n > 1) picks.Add((new(f, "clean"), (seed % n + 1 + seed / 7 % (n - 1)) % n)); }
            if (bucket == 1) picks.Add((new(f, "pink+10"), seed % n));
        }
        var items = picks.Select(p => p.Item).Distinct().ToList();
        var records = (await Evaluate(items)).Where(r => r.Hit).ToDictionary(r => (r.Item.Key, r.Method, r.GtIndex));
        var methods = RoundTripVariants.Where(v => v.Method != null).Select(v => v.Method!).Distinct().ToList();
        var common = picks.Where(p => methods.All(m => records.ContainsKey((p.Item.Key, m, p.Word)))).ToList();
        var outcomes = new ConcurrentBag<(string Variant, string Condition, bool Exact, bool Contains)>();
        int done = 0;
        await Parallel.ForEachAsync(common, new ParallelOptions { MaxDegreeOfParallelism = workers }, async (pick, _) =>
        {
            var speech = Speech16k(pick.Item.Fixture, pick.Item.Condition); var truth = pick.Item.Fixture.Words[pick.Word];
            for (int v = 0; v < RoundTripVariants.Length; v++)
            {
                var variant = RoundTripVariants[v];
                double from = variant.Method == null ? truth.StartMs : records[(pick.Item.Key, variant.Method, pick.Word)].HypStart, to = variant.Method == null ? truth.EndMs : records[(pick.Item.Key, variant.Method, pick.Word)].HypEnd;
                // The cache key carries the cut itself, so a changed method never reuses an old answer.
                var (exact, contains, _) = await Heard(speech, from - variant.PadBefore, to + variant.PadAfter, truth.Norm, $"rt__{pick.Item.Key}__{pick.Word}__{from - variant.PadBefore:0}_{to + variant.PadAfter:0}");
                outcomes.Add((variant.Name, pick.Item.Condition, exact, contains));
            }
            if (Interlocked.Increment(ref done) % 40 == 0) Console.WriteLine($"   round trip {done}/{common.Count} words");
        });
        Heading("Round trip: a single word is cut out, surrounded by 300 ms of silence and recognised again");
        markdown.AppendLine($"{picks.Count} sampled words; {common.Count} of them were recognised by every method compared, and only those are scored so the methods face the same words. \"Same word\" = Whisper returns exactly that word; \"contains\" = that word is among what it returns.").AppendLine();
        var rows = new List<string[]>(); var json = new List<object>();
        foreach (var variant in RoundTripVariants)
        {
            var row = new List<string> { variant.Name };
            foreach (var condition in new[] { "clean", "pink+10" })
            {
                var set = outcomes.Where(o => o.Variant == variant.Name && o.Condition == condition).ToList();
                double exact = 100.0 * set.Count(o => o.Exact) / Math.Max(1, set.Count), contains = 100.0 * set.Count(o => o.Contains) / Math.Max(1, set.Count);
                row.Add(set.Count.ToString(Inv)); row.Add(N(exact)); row.Add(N(contains));
                json.Add(new { variant = variant.Name, condition, words = set.Count, sameWordPct = Math.Round(exact, 1), containsPct = Math.Round(contains, 1) });
            }
            rows.Add(row.ToArray());
        }
        Table(["cut", "clean: words", "same word %", "contains %", "pink +10 dB: words", "same word %", "contains %"], rows);
        results["roundTrip"] = new { sampled = picks.Count, scored = common.Count, rows = json };
    }

    // ---- rolling re-transcription --------------------------------------------------------------------------------

    private sealed record StreamCase(string Name, float[] Audio, int Rate, List<GtWord> Truth);
    private sealed record Pass(List<TimedWord> Token, List<TimedWord> Anchor, double FromMs, double ToMs);

    /// <summary>About a minute of speech: fourteen fixtures (both voices, all speeds) joined with pauses of 0.3 to 1.5 s.</summary>
    private static StreamCase BuildStream(int index)
    {
        var random = new Random(1000 + index); var audio = new List<float>(new float[24000]); var truth = new List<GtWord>();
        for (int k = 0; k < 14; k++)
        {
            var fixture = fixtures[(index * 37 + k * 23 + 5) % fixtures.Count]; double offset = audio.Count / 48.0;
            truth.AddRange(fixture.Words.Select(w => new GtWord { Text = w.Text, Norm = w.Norm, MarkMs = w.MarkMs + offset, StartMs = w.StartMs + offset, EndMs = w.EndMs + offset }));
            // Each fixture already ends in a little silence; trim it to the last word so the pause is the one chosen here.
            int keep = Math.Min(fixture.Clean.Length, (int)((fixture.Words[^1].EndMs + 20) * 48));
            audio.AddRange(fixture.Clean.AsSpan(0, keep).ToArray());
            audio.AddRange(new float[(int)((0.3 + random.NextDouble() * 1.2) * 48000)]);
        }
        audio.AddRange(new float[48000]);
        return new($"stream {index + 1}", audio.ToArray(), 48000, truth);
    }

    private static bool Quiet(ReadOnlySpan<float> samples) // MimicTranscriber.Quiet
    {
        if (samples.IsEmpty) return true;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return Math.Sqrt(sum / samples.Length) < 0.004;
    }

    /// <summary>Recognises audio[from, to) on its own and returns the words on the stream's timeline.</summary>
    private static async Task<(Pass Pass, RawResult Raw)> Recognise(StreamCase stream, long from, long to, string key)
    {
        var speech = MimicTranscriber.Resample(stream.Audio.AsSpan((int)from, (int)(to - from)).ToArray(), stream.Rate, WordTimingSignal.Rate);
        var raw = await engine.Run(WordMode.Dtw, speech, threads, $"{key}__{from}_{to}");
        var f = WordTimingSignal.Analyze(speech); double offset = from * 1000.0 / stream.Rate;
        var tokens = WordTimingMethods.FromTokens(raw); var anchor = WordTimingMethods.AnchorValley(tokens, f, AnchorOptions.Recommended);
        static List<TimedWord> Shift(List<TimedWord> words, double by) { var list = words.Select(w => w.Clone()).ToList(); foreach (var w in list) { w.StartMs += by; w.EndMs += by; w.AStart += by; w.AEnd += by; w.SegStart += by; w.SegEnd += by; } return list; }
        return (new(Shift(tokens, offset), Shift(anchor, offset), offset, to * 1000.0 / stream.Rate), raw);
    }

    /// <summary>
    /// The live path of MimicTranscriber.Step, replayed offline: audio arrives in real time, the loop looks every
    /// 500 ms, cuts a chunk at a pause (2 to 15 s) and holds a sentence back when 15 s forced a cut. "busy" is how
    /// long each recognition blocks the loop, which changes where the next chunk can start.
    /// </summary>
    private static async Task<Pass> Live(StreamCase stream, string key, double phaseSeconds, double busySeconds)
    {
        var token = new List<TimedWord>(); var anchor = new List<TimedWord>();
        long rate = stream.Rate, total = stream.Audio.Length, committed = 0; double clock = phaseSeconds;
        while (committed < total)
        {
            long written = Math.Min(total, (long)(clock * rate)), length = written - committed; bool final = written >= total;
            if (length < 2 * rate && !final) { clock += 0.5; continue; }
            long end = committed + Math.Min(length, 15 * rate);
            var audio = stream.Audio.AsSpan((int)committed, (int)(end - committed));
            bool full = end - committed >= 15 * rate, pause = Quiet(audio[Math.Max(0, audio.Length - (int)(0.4 * rate))..]);
            if (!final && !full && !pause) { clock += 0.5; continue; }
            if (Quiet(audio)) { committed = end; continue; }
            var (pass, raw) = await Recognise(stream, committed, end, key);
            var shown = raw.Segments.Select((s, i) => (Segment: s, Index: i)).Where(s => WordTimingMethods.Shown(s.Segment)).ToList();
            long next = end; int held = -1;
            if (full && !pause && shown.Count > 1 && (end - committed) - (long)(shown[^1].Segment.EndMs / 1000 * rate) < 0.4 * rate)
            {
                next = committed + (long)(shown[^1].Segment.StartMs / 1000 * rate); held = shown[^1].Index;
            }
            token.AddRange(pass.Token.Where(w => w.Segment != held)); anchor.AddRange(pass.Anchor.Where(w => w.Segment != held));
            committed = Math.Max(next, committed + rate / 10); clock += busySeconds;
        }
        return new(token, anchor, 0, total * 1000.0 / rate);
    }

    private sealed class Movement
    {
        public List<double> Start = [], End = []; public int Matched, Unmatched;
        public void Add(List<TimedWord> a, List<TimedWord> b)
        {
            foreach (var (i, j) in Align(a.Select(w => w.Norm).ToList(), a.Select(w => (w.StartMs + w.EndMs) / 2).ToList(), b.Select(w => w.Norm).ToList(), b.Select(w => (w.StartMs + w.EndMs) / 2).ToList()))
            {
                if (i >= 0 && j >= 0 && a[i].Norm == b[j].Norm && Math.Abs(a[i].StartMs - b[j].StartMs) < 2000) { Matched++; Start.Add(Math.Abs(a[i].StartMs - b[j].StartMs)); End.Add(Math.Abs(a[i].EndMs - b[j].EndMs)); }
                else Unmatched += (i >= 0 ? 1 : 0) + (j >= 0 ? 1 : 0);
            }
        }
        public string[] Row(string comparison, string audio, string method)
        {
            Start.Sort(); End.Sort(); var all = Start.Concat(End).ToList();
            return [comparison, audio, method, Matched.ToString(Inv), N(100.0 * Unmatched / Math.Max(1, 2 * Matched + Unmatched)), $"{N(Percentile(Start, .5))} / {N(Percentile(Start, .9))} / {N(Start.Count > 0 ? Start[^1] : double.NaN)}",
                $"{N(Percentile(End, .5))} / {N(Percentile(End, .9))} / {N(End.Count > 0 ? End[^1] : double.NaN)}", N(100.0 * all.Count(v => v > 20) / Math.Max(1, all.Count)), N(100.0 * all.Count(v => v > 50) / Math.Max(1, all.Count)), N(100.0 * all.Count(v => v > 100) / Math.Max(1, all.Count))];
        }
        public object Json(string comparison, string audio, string method)
        {
            Start.Sort(); End.Sort(); var all = Start.Concat(End).ToList();
            return new { comparison, audio, method, matchedWords = Matched, textDiffersPct = Math.Round(100.0 * Unmatched / Math.Max(1, 2 * Matched + Unmatched), 1), startMedianMs = Percentile(Start, .5), startP90Ms = Percentile(Start, .9), startMaxMs = Start.Count > 0 ? Start[^1] : double.NaN,
                endMedianMs = Percentile(End, .5), endP90Ms = Percentile(End, .9), endMaxMs = End.Count > 0 ? End[^1] : double.NaN, movedOver20Pct = Math.Round(100.0 * all.Count(v => v > 20) / Math.Max(1, all.Count), 1),
                movedOver50Pct = Math.Round(100.0 * all.Count(v => v > 50) / Math.Max(1, all.Count), 1), movedOver100Pct = Math.Round(100.0 * all.Count(v => v > 100) / Math.Max(1, all.Count), 1) };
        }
    }

    private static async Task Rolling(string realPath)
    {
        var cases = new List<(StreamCase Stream, string Audio, double WindowSeconds, double HopSeconds)>();
        for (int i = 0; i < Math.Min(6, limit); i++)
        {
            var clean = BuildStream(i);
            cases.Add((clean, "clean", 15, 5));
            cases.Add((clean with { Name = clean.Name + " pink+10", Audio = WordTimingSignal.Mix(clean.Audio, WordTimingSignal.PinkNoise(clean.Audio.Length, 500 + i), 10, clean.Rate) }, "pink+10", 15, 5));
        }
        if (realPath.Length > 0)
        {
            var (samples, rate) = WordTimingSignal.ReadMono(realPath);
            cases.Add((new("real speech", samples, rate, []), "real speech (" + Path.GetFileName(realPath) + ")", 6, 2));
        }
        string[] comparisons = ["whole file vs live chunks", "live chunks vs live chunks cut elsewhere", "overlapping windows, word at least 1 s inside both", "overlapping windows, word within 1 s of a window edge"];
        var movement = new Dictionary<(string Comparison, string Audio, string Method), Movement>();
        Movement Get(string comparison, string audio, string method) => movement.TryGetValue((comparison, audio, method), out var m) ? m : movement[(comparison, audio, method)] = new();
        var truthRecords = new ConcurrentBag<(string Run, string Audio, List<WordRecord> Records)>(); var chunkCounts = new ConcurrentBag<(string Audio, string Run, int Words)>();
        var gate = new object();
        var cleanFeatures = cases.Where(c => c.Audio == "clean").ToDictionary(c => c.Stream.Name, c => WordTimingSignal.Analyze(MimicTranscriber.Resample(c.Stream.Audio, c.Stream.Rate, WordTimingSignal.Rate)));
        await Parallel.ForEachAsync(cases, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers / 2) }, async (c, _) =>
        {
            var (stream, audio, windowSeconds, hopSeconds) = c; long rate = stream.Rate, total = stream.Audio.Length; string key = "roll__" + stream.Name.Replace(' ', '_');
            var whole = (await Recognise(stream, 0, total, key)).Pass;
            var liveA = await Live(stream, key, 0, 0.3);
            var liveB = await Live(stream, key, 0.25, 2.5);
            var windows = new List<Pass>();
            for (long from = 0; from < total; from += (long)(hopSeconds * rate))
            {
                long to = Math.Min(total, from + (long)(windowSeconds * rate));
                if (to - from < 2 * rate) break;
                if (!Quiet(stream.Audio.AsSpan((int)from, (int)(to - from)))) windows.Add((await Recognise(stream, from, to, key)).Pass);
                if (to >= total) break;
            }
            lock (gate)
            {
                foreach (var (method, pick) in new (string, Func<Pass, List<TimedWord>>)[] { ("A token", p => p.Token), (Best, p => p.Anchor) })
                {
                    Get(comparisons[0], audio, method).Add(pick(whole), pick(liveA));
                    Get(comparisons[1], audio, method).Add(pick(liveA), pick(liveB));
                    for (int i = 0; i < windows.Count; i++)
                        for (int j = i + 1; j < windows.Count; j++)
                        {
                            double from = windows[j].FromMs, to = windows[i].ToMs; // the overlap
                            if (to - from < 1500) continue;
                            bool Inside(TimedWord w) => w.StartMs >= from && w.EndMs <= to;
                            bool Deep(TimedWord w) => w.StartMs >= from + 1000 && w.EndMs <= to - 1000;
                            Get(comparisons[2], audio, method).Add(pick(windows[i]).Where(Deep).ToList(), pick(windows[j]).Where(Deep).ToList());
                            Get(comparisons[3], audio, method).Add(pick(windows[i]).Where(w => Inside(w) && !Deep(w)).ToList(), pick(windows[j]).Where(w => Inside(w) && !Deep(w)).ToList());
                        }
                }
            }
            if (stream.Truth.Count > 0)
            {
                var f = WordTimingSignal.Analyze(MimicTranscriber.Resample(stream.Audio, stream.Rate, WordTimingSignal.Rate)); var item = new EvalItem(fixtures[0], audio);
                foreach (var (run, pass) in new[] { ("whole file", whole), ("live chunks", liveA), ("live chunks cut elsewhere", liveB) })
                    foreach (var (method, words) in new[] { ("A token", pass.Token), (Best, pass.Anchor) })
                        truthRecords.Add((run + " | " + method, audio, Score(item, method, words, f, stream.Truth.ToArray(), cleanFeatures[stream.Name.Replace(" pink+10", "")])));
            }
        });
        Heading("Rolling re-transcription: how far the same word's times move between two recognitions of the same audio");
        Table(["comparison", "audio", "method", "words in both", "text differs %", "start moved med / p90 / max ms", "end moved med / p90 / max ms", "boundaries moved >20 ms %", ">50 ms %", ">100 ms %"],
            movement.OrderBy(m => Array.IndexOf(comparisons, m.Key.Comparison)).ThenBy(m => m.Key.Audio).ThenBy(m => m.Key.Method).Select(m => m.Value.Row(m.Key.Comparison, m.Key.Audio, m.Key.Method)));
        Heading("Accuracy against ground truth when the minute-long streams are recognised whole or in live chunks");
        var groups = truthRecords.GroupBy(t => (t.Run, t.Audio)).OrderBy(g => g.Key.Audio).ThenBy(g => g.Key.Run).ToList();
        Table(new[] { "audio" }.Concat(SummaryHeader).ToArray(), groups.Select(g => new[] { g.Key.Audio }.Concat(SummaryRow(g.Key.Run, g.SelectMany(t => t.Records).ToList())).ToArray()));
        results["rolling"] = new
        {
            movement = movement.Select(m => m.Value.Json(m.Key.Comparison, m.Key.Audio, m.Key.Method)).ToList(),
            accuracy = groups.Select(g => new { audio = g.Key.Audio, run = g.Key.Run, raw = Summarize(g.SelectMany(t => t.Records)), padded = Summarize(g.SelectMany(t => t.Records), PadBefore, PadAfter) }).ToList(),
        };
    }

    // ---- audio longer than one Whisper window ----------------------------------------------------------------------

    /// <summary>
    /// The "whole file" rows of the rolling stage recognise about a minute in one call, which is more than one
    /// 30 s Whisper window. This shows, per kind of pass, how much of such audio comes back as text at all, so a
    /// low recall there is not mistaken for a boundary problem. (The live path never hands over more than 15 s.)
    /// </summary>
    private static async Task LongAudio()
    {
        var modes = new (string Name, WordMode Mode)[] { ("today: phrases only", WordMode.Phrase), ("A token timestamps", WordMode.Tokens), ("B DTW (+ token timestamps)", WordMode.Dtw) };
        var rows = new List<string[]>(); var json = new List<object>();
        foreach (var (name, mode) in modes)
        {
            double seconds = 0, covered = 0; int reference = 0, hits = 0, passes = 0;
            for (int i = 0; i < Math.Min(6, limit); i++)
            {
                var stream = BuildStream(i);
                var speech = MimicTranscriber.Resample(stream.Audio, stream.Rate, WordTimingSignal.Rate);
                var raw = await engine.Run(mode, speech, threads, $"long__stream_{i + 1}");
                var shown = raw.Segments.Where(WordTimingMethods.Shown).ToList();
                // Words from the phrase text alone (no token times needed), aligned with the reference by text and rough position.
                var words = WordTimingMethods.PhraseProportional(raw);
                var pairs = Align(stream.Truth.Select(w => w.Norm).ToList(), stream.Truth.Select(w => (w.StartMs + w.EndMs) / 2).ToList(), words.Select(w => w.Norm).ToList(), words.Select(w => (w.StartMs + w.EndMs) / 2).ToList());
                reference += stream.Truth.Count; hits += pairs.Count(p => p.Ref >= 0 && p.Hyp >= 0 && stream.Truth[p.Ref].Norm == words[p.Hyp].Norm);
                seconds += speech.Length / 16000.0; covered += shown.Count > 0 ? shown[^1].EndMs / 1000 : 0; passes++;
            }
            rows.Add([name, passes.ToString(Inv), N(seconds / passes), N(covered / passes), N(100.0 * hits / Math.Max(1, reference))]);
            json.Add(new { pass = name, streams = passes, meanAudioSeconds = Math.Round(seconds / passes, 1), meanLastPhraseEndSeconds = Math.Round(covered / passes, 1), recallPct = Math.Round(100.0 * hits / Math.Max(1, reference), 1) });
        }
        Heading("Audio longer than one 30 s Whisper window, recognised in a single call (clean streams)");
        Table(["pass", "streams", "mean audio s", "mean end of the last phrase s", "words recognised %"], rows);
        results["longAudio"] = json;
    }

    // ---- the pause margin on cleaner recordings --------------------------------------------------------------------

    /// <summary>
    /// The 1.5 dB pause margin was tuned on heavy synthetic noise; the real-speech sample (a clean recording of a
    /// real room) showed it missing pauses. This compares it with a second, looser look for pauses (margin widened
    /// by signal-to-noise, longer minimum, a guard kept on each word) on every condition plus two cleaner ones
    /// (pink noise 20 and 30 dB under the speech), to see what the looser look costs where the fixed margin worked.
    /// </summary>
    private static async Task Margin()
    {
        string[] conditions = ["clean", "pink+30", "pink+20", "pink+10", "pink0", "music0", "voice-6"];
        var prepared = await Prepare(conditions.SelectMany(c => fixtures.Take(limit).Select(f => new EvalItem(f, c))), false);
        var rows = new List<string[]>(); var json = new List<object>();
        foreach (var condition in conditions)
        {
            var set = prepared.Where(p => p.Item.Condition == condition).ToList();
            double margin = set.Count > 0 ? set.Average(p => p.Features.MarginBySignalToNoiseDb) : double.NaN, range = set.Count > 0 ? set.Average(p => p.Features.PeakDb - p.Features.FloorDb) : double.NaN;
            var variants = new List<(string, AnchorOptions)> { ($"fixed {N(WordFeatures.NoiseMarginDb)} dB only", AnchorOptions.Recommended) };
            foreach (var (minimum, guard) in new[] { (120.0, 0.0), (200.0, 40.0), (250.0, 40.0), (250.0, 60.0), (350.0, 40.0), (350.0, 60.0) })
                variants.Add(($"+ wide, at least {minimum} ms, guard {guard} ms" + (minimum == WideOptions.WidePauseMs && guard == WideOptions.WideGuardMs ? " (chosen)" : ""), AnchorOptions.Recommended with { WidePauseMs = minimum, WideGuardMs = guard }));
            foreach (var (name, options) in variants)
            {
                var records = set.SelectMany(p => Score(p.Item, name, WordTimingMethods.AnchorValley(WordTimingMethods.FromTokens(p.Dtw), p.Features, options), p.Features, p.Item.Fixture.Words)).ToList();
                var words = set.SelectMany(p => WordTimingMethods.AnchorValley(WordTimingMethods.FromTokens(p.Dtw), p.Features, options)).ToList();
                double pauses = 100.0 * words.Sum(w => (w.PauseBefore ? 1 : 0) + (w.PauseAfter ? 1 : 0)) / Math.Max(1, 2 * words.Count);
                rows.Add(new[] { condition, N(range), N(margin), name, N(pauses) }.Concat(SummaryRow(name, records).Skip(1)).ToArray());
                json.Add(new { condition, loudSpeechOverBackgroundDb = Math.Round(range, 1), wideMarginDb = Math.Round(margin, 2), pauses = name, edgesAtAPausePct = Math.Round(pauses, 1), raw = Summarize(records), padded = Summarize(records, PadBefore, PadAfter) });
            }
        }
        Heading($"Pauses: the fixed margin alone against a second, looser look on clean recordings, {Best}");
        Table(new[] { "audio", "loud speech over background dB (mean)", "wide margin dB (mean)", "pauses found with", "word edges at a pause %" }.Concat(SummaryHeader.Skip(1)).ToArray(), rows);
        results["pauseMargin"] = json;
    }

    // ---- real speech, no labels ------------------------------------------------------------------------------------

    private static async Task RealSpeech(string path)
    {
        var (samples, rate) = WordTimingSignal.ReadMono(path);
        var speech = MimicTranscriber.Resample(samples, rate, WordTimingSignal.Rate); string key = "real__" + Path.GetFileNameWithoutExtension(path);
        var dtw = await engine.Run(WordMode.Dtw, speech, threads, key); var oneWord = await engine.Run(WordMode.OneWord, speech, threads, key);
        var f = WordTimingSignal.Analyze(speech); var methods = Methods(dtw, oneWord, f);
        string[] shown = ["P phrase, proportional", "D phrase + signal DP", "A token", "C one word", "A + snap 80 + trim", Best, Wide];
        Heading($"Real speech ({Path.GetFileName(path)}, {speech.Length / 16000.0:0.0} s): word times per method, ms");
        var anchor = methods.First(m => m.Name == Best).Words; var token = methods.First(m => m.Name == "A token").Words; var wide = methods.First(m => m.Name == Wide).Words;
        markdown.AppendLine($"Loud speech stands {N(f.PeakDb - f.FloorDb)} dB over the background here, so the wide margin is {N(f.MarginBySignalToNoiseDb)} dB (fixed: {N(WordFeatures.NoiseMarginDb)} dB).").AppendLine();
        Table(["word", "A token", Best, "pause before / after", "status", Wide, "pause before / after", "status", "min token probability", "level at its cuts (dB under peak)"], anchor.Select((w, i) =>
        {
            var v = wide[i]; double peak = f.Peak(v.StartMs, v.EndMs), startDb = f.At(v.StartMs) - peak, endDb = f.At(v.EndMs) - peak;
            static string Sides(TimedWord word) => (word.PauseBefore ? "yes" : "no") + " / " + (word.PauseAfter ? "yes" : "no");
            return new[] { w.Text, $"{token[i].StartMs:0}-{token[i].EndMs:0}", $"{w.StartMs:0}-{w.EndMs:0}", Sides(w), Status(w.MinP, Math.Max(w.GapBeforeMs, w.GapAfterMs)),
                $"{v.StartMs:0}-{v.EndMs:0}", Sides(v), Status(v.MinP, Math.Max(v.GapBeforeMs, v.GapAfterMs)), N(w.MinP, "0.00"), $"{N(startDb, "0")} / {N(endDb, "0")}" };
        }));
        var rows = new List<string[]>(); var json = new List<object>();
        foreach (var name in shown)
        {
            var words = methods.First(m => m.Name == name).Words;
            var levels = words.SelectMany(w => { double peak = f.Peak(w.StartMs, w.EndMs); return new[] { f.At(w.StartMs) - peak, f.At(w.EndMs) - peak }; }).OrderBy(v => v).ToList();
            var row = new List<string> { name, words.Count.ToString(Inv), N(Percentile(levels, .5)), N(Percentile(levels, .9)), N(100.0 * levels.Count(v => v > -10) / Math.Max(1, levels.Count)) };
            var trips = new Dictionary<string, double>();
            foreach (var (before, after) in new[] { (0.0, 0.0), (PadBefore, PadAfter), (40.0, 40.0) })
            {
                int exact = 0;
                await Parallel.ForEachAsync(Enumerable.Range(0, words.Count), new ParallelOptions { MaxDegreeOfParallelism = workers }, async (i, _) =>
                {
                    if ((await Heard(speech, words[i].StartMs - before, words[i].EndMs + after, words[i].Norm, $"rt__{key}__{i}__{words[i].StartMs - before:0}_{words[i].EndMs + after:0}")).Exact) Interlocked.Increment(ref exact);
                });
                row.Add(N(100.0 * exact / Math.Max(1, words.Count))); trips[$"+{before}/{after}"] = Math.Round(100.0 * exact / Math.Max(1, words.Count), 1);
            }
            rows.Add(row.ToArray());
            json.Add(new { method = name, words = words.Count, cutLevelMedianDb = Percentile(levels, .5), cutLevelP90Db = Percentile(levels, .9), cutsWithin10DbOfPeakPct = Math.Round(100.0 * levels.Count(v => v > -10) / Math.Max(1, levels.Count), 1), roundTripSameWordPct = trips,
                wordList = words.Select(w => new { w.Text, startMs = Math.Round(w.StartMs), endMs = Math.Round(w.EndMs), minProbability = Math.Round(w.MinP, 3) }).ToList() });
        }
        Heading("Real speech: label-free scores");
        Table(["method", "words", "cut level median dB", "p90 dB", "cuts within 10 dB of peak %", "round trip same word %, no padding", $"+{PadBefore}/{PadAfter} ms", "+40/40 ms"], rows);
        results["realSpeech"] = new { file = Path.GetFileName(path), seconds = Math.Round(speech.Length / 16000.0, 2), text = string.Join("", dtw.Segments.Select(s => s.Text)).Trim(), methods = json };
    }

    // ---- shorter Whisper window -----------------------------------------------------------------------------------

    /// <summary>
    /// The cost of a pass is the 30 s window, not the audio in it. Does a window sized to the chunk (audio context)
    /// keep the words and the boundaries? Same fixtures, clean and pink +10 dB, anchor-valley method.
    /// </summary>
    private static async Task ShortContext()
    {
        var items = new[] { "clean", "pink+10" }.SelectMany(c => fixtures.Take(limit).Select(f => new EvalItem(f, c))).ToList();
        var full = new ConcurrentBag<WordRecord>(); var small = new ConcurrentBag<WordRecord>(); int done = 0;
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = workers }, async (item, _) =>
        {
            var speech = Speech16k(item.Fixture, item.Condition); var f = WordTimingSignal.Analyze(speech);
            var normal = await engine.Run(WordMode.Dtw, speech, threads, item.Key);
            var reduced = await engine.Run(WordMode.Dtw, speech, threads, item.Key, WordTimingEngine.ShortContext(speech.Length));
            foreach (var r in Score(item, "full", WordTimingMethods.AnchorValley(WordTimingMethods.FromTokens(normal), f, AnchorOptions.Recommended), f, item.Fixture.Words)) full.Add(r);
            foreach (var r in Score(item, "short", WordTimingMethods.AnchorValley(WordTimingMethods.FromTokens(reduced), f, AnchorOptions.Recommended), f, item.Fixture.Words)) small.Add(r);
            if (Interlocked.Increment(ref done) % 100 == 0) Console.WriteLine($"   short window {done}/{items.Count} items");
        });
        Heading($"Shorter Whisper window (audio context sized to the chunk) against the full 30 s window, {Best}");
        var rows = new List<string[]>(); var json = new List<object>();
        foreach (var condition in new[] { "clean", "pink+10" })
            foreach (var (name, records) in new[] { ("full 30 s window (today)", full), ("window sized to the chunk", small) })
            {
                var set = records.Where(r => r.Item.Condition == condition).ToList();
                rows.Add(new[] { condition }.Concat(SummaryRow(name, set)).ToArray());
                json.Add(new { condition, window = name, raw = Summarize(set), padded = Summarize(set, PadBefore, PadAfter) });
            }
        Table(new[] { "audio" }.Concat(SummaryHeader).ToArray(), rows);
        results["shortContext"] = json;
    }

    // ---- cost ------------------------------------------------------------------------------------------------------

    private static async Task Cost()
    {
        var sentences = fixtures.Where(f => f.RateName == "normal" && f.Sentence % 4 == 1).Take(Math.Min(limit, 16)).Select(f => Speech16k(f, "clean")).ToList();
        var chunks = Enumerable.Range(0, Math.Min(limit, 6)).Select(i => { var s = BuildStream(i); return MimicTranscriber.Resample(s.Audio.AsSpan(24000, 14 * 48000).ToArray(), 48000, WordTimingSignal.Rate); }).ToList();
        var sets = new (string Name, List<float[]> Audio)[] { ("single sentences (" + N(sentences.Average(s => s.Length / 16000.0)) + " s each)", sentences), ("14 s chunks", chunks) };
        var modes = new (string Name, WordMode Mode, bool Short)[] { ("today: phrases only", WordMode.Phrase, false), ("A token timestamps", WordMode.Tokens, false), ("B DTW (+ token timestamps)", WordMode.Dtw, false),
            ("C one word per segment", WordMode.OneWord, false), ("B DTW, window sized to the chunk", WordMode.Dtw, true) };
        var rows = new List<string[]>(); var json = new List<object>(); var process = Process.GetCurrentProcess();
        foreach (var (setName, audio) in sets)
        {
            double minutes = audio.Sum(a => a.Length / 16000.0) / 60;
            foreach (int count in new[] { 1, 2 })
                foreach (var (modeName, mode, small) in modes)
                {
                    int Context(float[] a) => small ? WordTimingEngine.ShortContext(a.Length) : 0;
                    foreach (var context in audio.Select(Context).Distinct()) await engine.Run(mode, audio.First(a => Context(a) == context), count, null, context); // warm-up: loads the model, builds the processor
                    double wall = double.MaxValue, cpu = 0;
                    for (int repeat = 0; repeat < 2; repeat++)
                    {
                        process.Refresh(); var before = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
                        foreach (var a in audio) await engine.Run(mode, a, count, null, Context(a));
                        clock.Stop(); process.Refresh();
                        if (clock.Elapsed.TotalSeconds < wall) { wall = clock.Elapsed.TotalSeconds; cpu = (process.TotalProcessorTime - before).TotalSeconds; }
                    }
                    rows.Add([setName, modeName, count.ToString(Inv), N(wall / minutes, "0.0"), N(cpu / minutes, "0.0"), N(minutes * 60 / wall, "0.0"), N(wall / audio.Count, "0.00")]);
                    json.Add(new { audio = setName, mode = modeName, threads = count, wallSecondsPerAudioMinute = Math.Round(wall / minutes, 2), cpuSecondsPerAudioMinute = Math.Round(cpu / minutes, 2), timesRealTime = Math.Round(minutes * 60 / wall, 2), wallSecondsPerPass = Math.Round(wall / audio.Count, 3) });
                }
            // The signal work on top of the DTW pass: energy analysis + anchor-valley boundaries.
            var raws = new List<RawResult>();
            foreach (var a in audio) raws.Add(await engine.Run(WordMode.Dtw, a, 2));
            process.Refresh(); var cpuBefore = process.TotalProcessorTime; var timer = Stopwatch.StartNew();
            for (int i = 0; i < audio.Count; i++) WordTimingMethods.AnchorValley(WordTimingMethods.FromTokens(raws[i]), WordTimingSignal.Analyze(audio[i]), AnchorOptions.Recommended);
            timer.Stop(); process.Refresh();
            rows.Add([setName, "energy analysis + anchor-valley boundaries (no Whisper)", "1", N(timer.Elapsed.TotalSeconds / minutes, "0.000"), N((process.TotalProcessorTime - cpuBefore).TotalSeconds / minutes, "0.000"), N(minutes * 60 / timer.Elapsed.TotalSeconds, "0"), N(timer.Elapsed.TotalSeconds / audio.Count, "0.0000")]);
            json.Add(new { audio = setName, mode = "signal analysis + anchor-valley", threads = 1, wallSecondsPerAudioMinute = Math.Round(timer.Elapsed.TotalSeconds / minutes, 4), cpuSecondsPerAudioMinute = Math.Round((process.TotalProcessorTime - cpuBefore).TotalSeconds / minutes, 4), timesRealTime = Math.Round(minutes * 60 / timer.Elapsed.TotalSeconds), wallSecondsPerPass = Math.Round(timer.Elapsed.TotalSeconds / audio.Count, 5) });
        }
        Heading("Cost: wall time and CPU time per minute of audio (warm model, one pass at a time, best of two runs)");
        Table(["audio", "pass", "threads", "wall s per audio minute", "CPU s per audio minute", "x real time", "wall s per pass"], rows);
        results["cost"] = json;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Can the recognition be kept out of a game's way with a thread priority? Runs the synchronous Process() on a
    /// dedicated below-normal thread and measures how much of the process's CPU time was spent on that very thread.
    /// </summary>
    private static void Priority(string model)
    {
        var speech = Speech16k(fixtures.First(f => f.RateName == "normal"), "clean");
        var rows = new List<string[]>(); var json = new List<object>();
        using var factory = WhisperFactory.FromPath(model, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = WhisperAlignmentHeadsPreset.BaseEn });
        foreach (int count in new[] { 1, 2 })
        {
            double onCaller = 0, total = 0;
            var thread = new Thread(() =>
            {
                using var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(count).WithProbabilities().WithNoSpeechThreshold(.5f).WithTokenTimestamps().WithSegmentEventHandler(_ => { }).Build();
                processor.Process(speech); // warm-up
                uint id = GetCurrentThreadId(); var process = Process.GetCurrentProcess();
                TimeSpan Mine() { process.Refresh(); foreach (ProcessThread t in process.Threads) if (t.Id == id) return t.TotalProcessorTime; return TimeSpan.Zero; }
                var mineBefore = Mine(); var allBefore = process.TotalProcessorTime;
                for (int i = 0; i < 3; i++) processor.Process(speech);
                onCaller = (Mine() - mineBefore).TotalSeconds; process.Refresh(); total = (process.TotalProcessorTime - allBefore).TotalSeconds;
            }) { Priority = ThreadPriority.BelowNormal, IsBackground = true, Name = "word-timing priority probe" };
            thread.Start(); thread.Join();
            rows.Add([count.ToString(Inv), N(total, "0.00"), N(onCaller, "0.00"), N(100 * onCaller / Math.Max(1e-9, total))]);
            json.Add(new { threads = count, cpuSeconds = Math.Round(total, 2), cpuSecondsOnCallingThread = Math.Round(onCaller, 2), sharePct = Math.Round(100 * onCaller / Math.Max(1e-9, total), 1) });
        }
        Heading("Thread priority: share of the recognition's CPU time spent on the calling (below-normal) thread, synchronous Process()");
        Table(["WithThreads", "CPU s, whole process", "CPU s on the calling thread", "share %"], rows);
        results["priority"] = json;
    }
}
