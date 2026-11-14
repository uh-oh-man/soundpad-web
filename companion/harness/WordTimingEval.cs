using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace SoundpadWeb.Testing;

// Harness command: evaluates how to get per-word cut points out of the local Whisper pipeline (overhaul
// plan, "Rolling recording and clip editing": "evaluate word timing/alignment rather than treating phrase
// timestamps or token timestamps as proven cuts").
//
//   SoundpadWeb.Harness.exe --word-timing-eval <fixtures folder> [--out results.json] [--md tables.md]
//       [--stage tune,accuracy,roundtrip,rolling,long,margin,real,context,cost,priority | all] [--cache <folder>] [--real <speech.wav>]
//       [--workers 5] [--threads 2] [--limit N] [--model <ggml file>] [--cached-only]
//   Stages: tune (grid search of the anchor-valley settings on half the sentences), accuracy, roundtrip, rolling, long
//   (a minute of audio in one call), margin (the pause margin on cleaner recordings: pink noise at +20 and +30 dB),
//   real, context (a shorter Whisper window for short chunks), cost, priority.
//   Extra, not part of "all": dump (--files a,b [--condition pink0]: print every method's words for those fixtures).
//   --noise-margin <dB> overrides WordFeatures.NoiseMarginDb for experiments.
//
// <fixtures folder> holds <id>.wav + <id>.json pairs with ground-truth word starts (made by the throwaway
// script make-fixtures.ps1 with the Windows speech synthesizer; test tooling only, never a product feature).
// Noisy and overlapping variants are mixed here, in code, deterministically. Whisper results are cached in
// --cache so the metric code can be rerun in seconds. The command reads the model and the fixtures, writes
// only --out, --md and --cache, opens no audio device and never touches Soundpad or the companion's data.
//
// Files: WordTimingEngine.cs (Whisper passes), WordTimingSignal.cs (audio, noise, energy), WordTimingMethods.cs
// (the candidate methods), WordTimingEval.cs (fixtures, ground truth, accuracy tables), WordTimingStages.cs
// (round trip, rolling re-transcription, real speech, cost).

public sealed class GtWord
{
    public string Text = "", Norm = "";
    /// <summary>The synthesizer's own word-start mark, and the extent of the word's sound in the clean audio.</summary>
    public double MarkMs, StartMs, EndMs;
}

public sealed class Fixture
{
    public string Id = "", Voice = "", RateName = "", Tag = "", Text = "", Path = "";
    public int Sentence, Index;
    public GtWord[] Words = [];
    /// <summary>Energy analysis of the clean speech: what is audible where, for judging cuts.</summary>
    public WordFeatures Features = null!;
    private float[]? clean;
    /// <summary>Clean speech at the fixture's own rate (48 kHz, the companion's capture rate).</summary>
    public float[] Clean => clean ??= WordTimingSignal.ReadMono(Path).Samples;
    public int Rate = 48000;
}

public sealed record EvalItem(Fixture Fixture, string Condition)
{
    public string Key => Fixture.Id + "__" + Condition;
}

/// <summary>One reference word and/or one recognised word of one method on one item.</summary>
public sealed class WordRecord
{
    public string Method = "", Norm = "";
    public EvalItem Item = null!;
    public bool HasRef, HasHyp, Hit;
    public int GtIndex = -1;
    public double HypStart, HypEnd, GtStart, GtEnd, PrevStart = double.NaN, PrevEnd = double.NaN, NextStart = double.NaN, NextEnd = double.NaN;
    // Signals a UI could use to mark a word "uncertain" (all available without ground truth).
    public double MinP, MeanP, SegP, StartDb, EndDb, PeakOverFloor, TokenDisagreeMs, MsPerChar, GapMs, GapLevelDb;
    public int PauseSides;

    /// <summary>Clean-speech analysis the reference times refer to, and how far its timeline is shifted from the times above.</summary>
    public WordFeatures? Truth; public double TruthOffsetMs;

    /// <summary>Milliseconds of [from, to) in which the clean speech is audible: within 35 dB of the loudest frame of [wordFrom, wordTo).</summary>
    private double Audible(double from, double to, double wordFrom, double wordTo)
    {
        if (to <= from) return 0;
        if (Truth == null) return to - from;
        double limit = Truth.Peak(wordFrom - TruthOffsetMs, wordTo - TruthOffsetMs) - 35; int count = 0;
        for (int k = (int)Math.Ceiling((from - TruthOffsetMs) / WordFeatures.HopMs), last = (int)Math.Ceiling((to - TruthOffsetMs) / WordFeatures.HopMs); k < last; k++)
            if (k >= 0 && k < Truth.Db.Length && Truth.Db[k] > limit) count++;
        return count * WordFeatures.HopMs;
    }

    /// <summary>
    /// What a cut with the given padding does to the word: milliseconds of it cut off, and milliseconds of a
    /// neighbouring word let in. "strict" counts every millisecond of the reference span; otherwise only the
    /// milliseconds where the clean speech is audible count (losing the silent closure before a "t" costs nothing).
    /// </summary>
    public (double Clip, double Neighbour) Damage(double padBefore, double padAfter, bool strict = false)
    {
        double from = HypStart - padBefore, to = HypEnd + padAfter;
        double Span(double a, double b, double wordFrom, double wordTo) => strict ? Math.Max(0, b - a) : Audible(a, b, wordFrom, wordTo);
        double clip = Span(GtStart, Math.Min(from, GtEnd), GtStart, GtEnd) + Span(Math.Max(to, GtStart), GtEnd, GtStart, GtEnd);
        double before = double.IsNaN(PrevEnd) ? 0 : Span(Math.Max(PrevStart, from), Math.Min(PrevEnd, to), PrevStart, PrevEnd);
        double after = double.IsNaN(NextStart) ? 0 : Span(Math.Max(NextStart, from), Math.Min(NextEnd, to), NextStart, NextEnd);
        return (clip, Math.Max(before, after));
    }
    /// <summary>The plan's test for a cut that does not sound like the word: clips more than 30 ms of it or lets in more than 60 ms of a neighbour.</summary>
    public bool Bad(double padBefore, double padAfter, bool strict = false) { var (clip, neighbour) = Damage(padBefore, padAfter, strict); return clip > 30 || neighbour > 60; }
}

public sealed record Summary(int Words, int Recognised, int Hits, double Recall, double Precision,
    double StartMedian, double StartP90, double StartMax, double StartBias, double EndMedian, double EndP90, double EndMax, double EndBias,
    double BadPct, double ClipPct, double NeighbourPct, double UsablePct, double StrictBadPct);

public static partial class WordTimingEval
{
    public static readonly string[] Conditions = ["clean", "pink+10", "pink0", "music0", "voice-6"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly StringBuilder markdown = new();
    private static readonly Dictionary<string, object?> results = new();
    private static WordTimingEngine engine = null!;
    private static int workers = 5, threads = 2, limit = int.MaxValue;
    private static List<Fixture> fixtures = [];
    /// <summary>Padding the report recommends (ms before the start, ms after the end); used wherever one padded figure is shown.</summary>
    public const double PadBefore = 20, PadAfter = 10;

    public static async Task Run(string[] args)
    {
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var model = Option("--model", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb", "models", "ggml-base.en.bin"));
        if (!File.Exists(model)) { Console.Error.WriteLine("Speech model not found: " + model); Environment.ExitCode = 2; return; }
        workers = int.Parse(Option("--workers", "5"), Inv); threads = int.Parse(Option("--threads", "2"), Inv); limit = int.Parse(Option("--limit", int.MaxValue.ToString(Inv)), Inv);
        var cache = Option("--cache", "");
        using var whisper = new WordTimingEngine(model, cache.Length > 0 ? cache : null); engine = whisper;
        fixtures = LoadFixtures(args[1]);
        var stages = Option("--stage", "all").Split(',');
        void Save()
        {
            results["whisperPasses"] = engine.Calls;
            if (Option("--out", "") is { Length: > 0 } output) File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            if (Option("--md", "") is { Length: > 0 } tables) File.WriteAllText(tables, markdown.ToString());
        }
        bool Wanted(string stage) => stages.Contains("all") || stages.Contains(stage);
        results["meta"] = new { date = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv), model = Path.GetFileName(model), modelBytes = new FileInfo(model).Length, whisperNet = "1.9.1",
            fixtures = fixtures.Count, fixtureMinutes = Math.Round(fixtures.Sum(f => f.Words.Length > 0 ? f.Words[^1].EndMs : 0) / 60000, 2), voices = fixtures.Select(f => f.Voice).Distinct().ToArray(),
            logicalProcessors = Environment.ProcessorCount, workers, threadsPerPass = threads, padBeforeMs = PadBefore, padAfterMs = PadAfter };
        Console.WriteLine($"{fixtures.Count} fixtures, {fixtures.Sum(f => f.Words.Length)} reference words; stages: {string.Join(",", stages)}");
        if (stages.Contains("dump")) { await Dump(Option("--files", fixtures[0].Id).Split(','), Option("--condition", "clean")); return; }
        cachedOnly = args.Contains("--cached-only"); WordFeatures.NoiseMarginDb = double.Parse(Option("--noise-margin", "1.5"), Inv);
        if (Wanted("tune")) await Tune();
        if (Wanted("accuracy")) await Accuracy();
        Save(); // after each stage, so a long run that is stopped still leaves its finished tables
        if (Wanted("roundtrip")) await RoundTrip();
        Save();
        if (Wanted("rolling")) await Rolling(Option("--real", ""));
        Save();
        if (Wanted("long")) await LongAudio();
        if (Wanted("margin")) await Margin();
        if (Wanted("real") && Option("--real", "") is { Length: > 0 } real) await RealSpeech(real);
        Save();
        if (Wanted("context")) await ShortContext();
        Save();
        if (Wanted("cost")) await Cost();
        if (Wanted("priority")) Priority(model);
        Save();
        Console.WriteLine($"Done: {engine.Calls} Whisper passes run (cached ones not counted).");
    }

    // ---- fixtures and ground truth -----------------------------------------------------------------------------

    /// <summary>Fixtures with ground truth (also used by WordPipelineEval.cs, the 2.38 production pipeline check).</summary>
    public static List<Fixture> LoadFixtures(string folder)
    {
        var list = new List<Fixture>();
        foreach (var path in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var meta = JsonDocument.Parse(File.ReadAllText(path));
            var root = meta.RootElement;
            var fixture = new Fixture
            {
                Id = root.GetProperty("id").GetString()!, Voice = root.GetProperty("voice").GetString()!.Replace("Microsoft ", "").Replace(" Desktop", ""), RateName = root.GetProperty("rateName").GetString()!,
                Tag = root.GetProperty("tags")[0].GetString()!, Text = root.GetProperty("text").GetString()!, Sentence = root.GetProperty("sentence").GetInt32(), Path = Path.ChangeExtension(path, ".wav"), Index = list.Count,
            };
            // A number such as "25" is spoken as two words but is one written word: the synthesizer reports it twice
            // at the same text position. Keep one reference word per written word.
            var words = new List<GtWord>(); int lastPosition = -1;
            foreach (var w in root.GetProperty("words").EnumerateArray())
            {
                int position = w.GetProperty("charPos").GetInt32();
                if (position == lastPosition) continue;
                lastPosition = position;
                var text = w.GetProperty("text").GetString()!;
                words.Add(new GtWord { Text = text, Norm = WordTimingMethods.Normalize(text), MarkMs = w.GetProperty("startMs").GetDouble() });
            }
            fixture.Words = words.ToArray();
            list.Add(fixture);
        }
        // Ground-truth extents from the clean audio: a word runs from its mark to the next mark, shrunk to the sound
        // when silence (more than 50 dB under the file's peak) separates the two.
        Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, fixture =>
        {
            var f = WordTimingSignal.Analyze(MimicTranscriber.Resample(fixture.Clean, fixture.Rate, WordTimingSignal.Rate));
            fixture.Features = f;
            double threshold = f.Db.Max() - 50, fileEnd = (f.Db.Length - 1) * WordFeatures.HopMs;
            for (int i = 0; i < fixture.Words.Length; i++)
            {
                var w = fixture.Words[i]; double next = i + 1 < fixture.Words.Length ? fixture.Words[i + 1].MarkMs : fileEnd;
                int mark = f.Frame(w.MarkMs), s = mark, limitFrame = f.Frame(Math.Min(next, w.MarkMs + 150));
                while (s < limitFrame && f.Db[s] < threshold) s++;
                if (s > mark && s < limitFrame) w.StartMs = (s - 1) * WordFeatures.HopMs; // the mark sits in silence: sound starts a little later
                else
                {
                    // Sound may start up to 40 ms before the mark when silence precedes the word.
                    s = mark; int back = 0;
                    bool free = i == 0 || fixture.Words[i - 1].EndMs < w.MarkMs - 40;
                    while (free && s > 0 && back < 8 && f.Db[s - 1] >= threshold) { s--; back++; }
                    w.StartMs = free && back is > 0 and < 8 ? s * WordFeatures.HopMs : w.MarkMs;
                    s = mark;
                }
                // The end: skip up to 40 ms of the next word's own onset before its mark, then any silence before that.
                int q = f.Frame(next) - 1, skipped = 0;
                while (q > s && f.Db[q] >= threshold && skipped < 8) { q--; skipped++; }
                int runEnd = q;
                while (q > s && f.Db[q] < threshold) q--;
                w.EndMs = (runEnd - q) * WordFeatures.HopMs >= 40 ? (q + 1) * WordFeatures.HopMs : next;
            }
        });
        return list;
    }

    private static readonly Lazy<float[]> music = new(() => WordTimingSignal.MusicBed(48000 * 24, 48000));
    private static int Seed(string text) { unchecked { int h = 17; foreach (char c in text) h = h * 31 + c; return h & 0x7fffffff; } }

    /// <summary>The audio of a fixture under a condition, at the fixture's rate. Deterministic: the same call always gives the same samples.</summary>
    public static float[] Audio(Fixture fixture, string condition)
    {
        var clean = fixture.Clean;
        switch (condition)
        {
            case "clean": return clean;
            // "pink+10", "pink0", and for the margin stage "pink+20", "pink+30": pink noise that many dB under the speech.
            case var pink when pink.StartsWith("pink", StringComparison.Ordinal):
                return WordTimingSignal.Mix(clean, WordTimingSignal.PinkNoise(clean.Length, Seed(fixture.Id)), double.Parse(pink[4..], Inv), fixture.Rate);
            case "music0": return WordTimingSignal.Mix(clean, WordTimingSignal.Loop(music.Value, clean.Length, fixture.Index * 7919 % music.Value.Length), 0, fixture.Rate);
            case "voice-6":
                // The other installed voice saying a different sentence at the same speed, 6 dB quieter than the main voice.
                var other = fixtures.Where(f => f.Voice != fixture.Voice && f.RateName == fixture.RateName).OrderBy(f => f.Index).ToList();
                if (other.Count == 0) other = fixtures.Where(f => f.Sentence != fixture.Sentence && f.RateName == fixture.RateName).ToList();
                var interferer = other.FirstOrDefault(f => f.Sentence == (fixture.Sentence + 16) % 48 + 1) ?? other[fixture.Index % other.Count];
                return WordTimingSignal.Mix(clean, WordTimingSignal.Loop(interferer.Clean, clean.Length, 0), 6, fixture.Rate);
            default: throw new ArgumentException(condition);
        }
    }
    public static float[] Speech16k(Fixture fixture, string condition) => MimicTranscriber.Resample(Audio(fixture, condition), fixture.Rate, WordTimingSignal.Rate);

    // ---- methods -------------------------------------------------------------------------------------------------

    /// <summary>The method the report recommends, by name in the tables.</summary>
    public const string Best = "AV anchor-valley";
    /// <summary>The same plus the second, looser look for pauses on clean recordings (<see cref="AnchorOptions.WidePauseMs"/>).</summary>
    public const string Wide = "AV + wide pauses";
    public static readonly AnchorOptions WideOptions = AnchorOptions.Recommended with { WidePauseMs = 250, WideGuardMs = 60 };

    /// <summary>Every candidate, from one DTW pass (tokens + anchors + phrases) and one "-ml 1 -sow" pass.</summary>
    public static List<(string Name, List<TimedWord> Words)> Methods(RawResult dtwPass, RawResult? oneWordPass, WordFeatures f, bool all = true)
    {
        var tokens = WordTimingMethods.FromTokens(dtwPass);
        var list = new List<(string, List<TimedWord>)>
        {
            ("A token", tokens),
            (Best, WordTimingMethods.AnchorValley(tokens, f, AnchorOptions.Recommended)),
        };
        if (!all) return list;
        list.Add(("P phrase, proportional", WordTimingMethods.PhraseProportional(dtwPass)));
        list.Add(("D phrase + signal DP", WordTimingMethods.PhraseSignal(dtwPass, f)));
        list.Add(("A + snap 40", WordTimingMethods.Snap(tokens, f, 40, false)));
        list.Add(("A + snap 80", WordTimingMethods.Snap(tokens, f, 80, false)));
        list.Add(("A + snap 80 + trim", WordTimingMethods.Snap(tokens, f, 80, true)));
        list.Add(("A + snap 150 + trim", WordTimingMethods.Snap(tokens, f, 150, true)));
        list.Add(("B dtw as start", WordTimingMethods.DtwAsStart(tokens)));
        list.Add(("B dtw as end", WordTimingMethods.DtwAsEnd(tokens)));
        list.Add(("B dtw as end + snap 80 + trim", WordTimingMethods.Snap(WordTimingMethods.DtwAsEnd(tokens), f, 80, true)));
        if (oneWordPass != null)
        {
            var oneWord = WordTimingMethods.FromOneWord(oneWordPass);
            list.Add(("C one word", oneWord));
            list.Add(("C + snap 80 + trim", WordTimingMethods.Snap(oneWord, f, 80, true)));
        }
        var r = AnchorOptions.Recommended;
        list.Add(("AV, pause 80 ms", WordTimingMethods.AnchorValley(tokens, f, r with { PauseMs = 80 })));
        list.Add(("AV, pause 200 ms", WordTimingMethods.AnchorValley(tokens, f, r with { PauseMs = 200 })));
        list.Add(("AV, no position prior", WordTimingMethods.AnchorValley(tokens, f, r with { PhiDb = 0 })));
        list.Add(("AV, position not sized by letters", WordTimingMethods.AnchorValley(tokens, f, r with { ByLength = false })));
        list.Add(("AV, no token prior", WordTimingMethods.AnchorValley(tokens, f, r with { TokenDb = 0 })));
        list.Add(("AV, energy only", WordTimingMethods.AnchorValley(tokens, f, r with { PhiDb = 0, TokenDb = 0 })));
        list.Add(("AV + trim", WordTimingMethods.AnchorValley(tokens, f, r with { Trim = true })));
        list.Add((Wide, WordTimingMethods.AnchorValley(tokens, f, WideOptions)));
        return list;
    }

    /// <summary>Levenshtein alignment of two word sequences by normalised text, ties broken by closeness in time. -1 marks a gap.</summary>
    public static List<(int Ref, int Hyp)> Align(IReadOnlyList<string> reference, IReadOnlyList<double> referenceMid, IReadOnlyList<string> hypothesis, IReadOnlyList<double> hypothesisMid)
    {
        int n = reference.Count, m = hypothesis.Count;
        var cost = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++) cost[i, 0] = i;
        for (int j = 1; j <= m; j++) cost[0, j] = j;
        double Pair(int i, int j) => (reference[i] == hypothesis[j] ? 0 : 1) + 1e-4 * Math.Min(10, Math.Abs(referenceMid[i] - hypothesisMid[j]) / 1000);
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                cost[i, j] = Math.Min(cost[i - 1, j - 1] + Pair(i - 1, j - 1), Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1));
        var pairs = new List<(int, int)>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && Math.Abs(cost[i, j] - (cost[i - 1, j - 1] + Pair(i - 1, j - 1))) < 1e-9) { pairs.Add((i - 1, j - 1)); i--; j--; }
            else if (i > 0 && Math.Abs(cost[i, j] - (cost[i - 1, j] + 1)) < 1e-9) { pairs.Add((i - 1, -1)); i--; }
            else { pairs.Add((-1, j - 1)); j--; }
        }
        pairs.Reverse();
        return pairs;
    }

    /// <summary>Scores one method's words on one item against the ground truth.</summary>
    public static List<WordRecord> Score(EvalItem item, string method, List<TimedWord> words, WordFeatures f, GtWord[] truth, WordFeatures? truthFeatures = null)
    {
        var records = new List<WordRecord>();
        var pairs = Align(truth.Select(w => w.Norm).ToList(), truth.Select(w => (w.StartMs + w.EndMs) / 2).ToList(), words.Select(w => w.Norm).ToList(), words.Select(w => (w.StartMs + w.EndMs) / 2).ToList());
        foreach (var (r, h) in pairs)
        {
            var record = new WordRecord { Method = method, Item = item, HasRef = r >= 0, HasHyp = h >= 0, GtIndex = r, Truth = truthFeatures ?? item.Fixture.Features };
            if (r >= 0)
            {
                record.Norm = truth[r].Norm; record.GtStart = truth[r].StartMs; record.GtEnd = truth[r].EndMs;
                if (r > 0) { record.PrevStart = truth[r - 1].StartMs; record.PrevEnd = truth[r - 1].EndMs; }
                if (r + 1 < truth.Length) { record.NextStart = truth[r + 1].StartMs; record.NextEnd = truth[r + 1].EndMs; }
            }
            if (h >= 0)
            {
                var w = words[h];
                record.HypStart = w.StartMs; record.HypEnd = w.EndMs; record.Hit = r >= 0 && truth[r].Norm == w.Norm;
                if (r < 0) record.Norm = w.Norm;
                double peak = f.Peak(w.StartMs, w.EndMs);
                record.MinP = w.MinP; record.MeanP = w.MeanP; record.SegP = w.SegP; record.StartDb = f.At(w.StartMs) - peak; record.EndDb = f.At(w.EndMs) - peak; record.PeakOverFloor = peak - f.FloorDb;
                record.TokenDisagreeMs = Math.Max(Math.Abs(w.StartMs - w.AStart), Math.Abs(w.EndMs - w.AEnd)); record.MsPerChar = (w.EndMs - w.StartMs) / Math.Max(1, w.Norm.Length);
                record.PauseSides = (w.PauseBefore ? 1 : 0) + (w.PauseAfter ? 1 : 0); record.GapMs = Math.Max(w.GapBeforeMs, w.GapAfterMs); record.GapLevelDb = Math.Max(w.LevelBeforeDb, w.LevelAfterDb);
            }
            records.Add(record);
        }
        return records;
    }

    public static double Percentile(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    public static Summary Summarize(IEnumerable<WordRecord> records, double padBefore = 0, double padAfter = 0)
    {
        var list = records.ToList(); var hits = list.Where(r => r.Hit).ToList();
        int reference = list.Count(r => r.HasRef), recognised = list.Count(r => r.HasHyp);
        var start = hits.Select(r => Math.Abs(r.HypStart - r.GtStart)).OrderBy(v => v).ToList(); var end = hits.Select(r => Math.Abs(r.HypEnd - r.GtEnd)).OrderBy(v => v).ToList();
        int bad = 0, clip = 0, neighbour = 0, strict = 0;
        foreach (var r in hits) { var (c, n) = r.Damage(padBefore, padAfter); if (c > 30) clip++; if (n > 60) neighbour++; if (c > 30 || n > 60) bad++; if (r.Bad(padBefore, padAfter, true)) strict++; }
        double Pct(int count, int of) => of == 0 ? double.NaN : Math.Round(100.0 * count / of, 1);
        double R(double v) => Math.Round(v, 1);
        return new Summary(reference, recognised, hits.Count, Pct(hits.Count, reference), Pct(hits.Count, recognised),
            R(Percentile(start, .5)), R(Percentile(start, .9)), R(start.Count > 0 ? start[^1] : double.NaN), R(hits.Count > 0 ? hits.Average(r => r.HypStart - r.GtStart) : double.NaN),
            R(Percentile(end, .5)), R(Percentile(end, .9)), R(end.Count > 0 ? end[^1] : double.NaN), R(hits.Count > 0 ? hits.Average(r => r.HypEnd - r.GtEnd) : double.NaN),
            Pct(bad, hits.Count), Pct(clip, hits.Count), Pct(neighbour, hits.Count), Pct(hits.Count - bad, reference), Pct(strict, hits.Count));
    }

    /// <summary>Area under the ROC curve of a score (higher = more suspicious) against a yes/no label. 0.5 is chance.</summary>
    public static double Auc(IEnumerable<(double Score, bool Positive)> data)
    {
        var sorted = data.Where(d => !double.IsNaN(d.Score)).OrderBy(d => d.Score).ToList();
        long positives = sorted.Count(d => d.Positive), negatives = sorted.Count - positives;
        if (positives == 0 || negatives == 0) return double.NaN;
        double rankSum = 0;
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j < sorted.Count && sorted[j].Score == sorted[i].Score) j++;
            double rank = (i + 1 + j) / 2.0;
            for (int k = i; k < j; k++) if (sorted[k].Positive) rankSum += rank;
            i = j;
        }
        return Math.Round((rankSum - positives * (positives + 1) / 2.0) / (positives * negatives), 3);
    }

    // ---- markdown helpers ----------------------------------------------------------------------------------------

    private static string N(double value, string format = "0.#") => double.IsNaN(value) ? "-" : value.ToString(format, Inv);
    private static void Heading(string text) { markdown.AppendLine().AppendLine("### " + text).AppendLine(); Console.WriteLine("-- " + text); }
    private static void Table(string[] header, IEnumerable<string[]> rows)
    {
        markdown.AppendLine("| " + string.Join(" | ", header) + " |").AppendLine("|" + string.Join("|", header.Select((_, i) => i == 0 ? "---" : "---:")) + "|");
        foreach (var row in rows) markdown.AppendLine("| " + string.Join(" | ", row) + " |");
        markdown.AppendLine();
    }
    private static readonly string[] SummaryHeader = ["method", "recall %", "precision %", "start |err| med / p90 / max ms", "start bias ms", "end |err| med / p90 / max ms", "end bias ms", "bad cut %", "clip >30 %", "neighbour >60 %", "bad cut % padded", "usable % of spoken (padded)", "strict bad % / padded"];
    private static string[] SummaryRow(string name, List<WordRecord> records)
    {
        var raw = Summarize(records); var padded = Summarize(records, PadBefore, PadAfter);
        return [name, N(raw.Recall), N(raw.Precision), $"{N(raw.StartMedian)} / {N(raw.StartP90)} / {N(raw.StartMax)}", N(raw.StartBias), $"{N(raw.EndMedian)} / {N(raw.EndP90)} / {N(raw.EndMax)}", N(raw.EndBias),
            N(raw.BadPct), N(raw.ClipPct), N(raw.NeighbourPct), N(padded.BadPct), N(padded.UsablePct), $"{N(raw.StrictBadPct)} / {N(padded.StrictBadPct)}"];
    }

    // ---- accuracy stage ------------------------------------------------------------------------------------------

    private static List<WordRecord> accuracyRecords = [];
    private static readonly ConcurrentDictionary<string, List<(string Name, List<TimedWord> Words)>> itemWords = new();

    private sealed record Prepared(EvalItem Item, RawResult Dtw, RawResult? OneWord, WordFeatures Features);
    private static bool cachedOnly;

    /// <summary>Runs (or reads from the cache) the Whisper passes for each item and analyses its audio.</summary>
    private static async Task<List<Prepared>> Prepare(IEnumerable<EvalItem> items, bool oneWordPass = true)
    {
        var bag = new ConcurrentBag<Prepared>(); int done = 0;
        var list = items.Where(i => !cachedOnly || engine.Cached(WordMode.Dtw, i.Key) && (!oneWordPass || engine.Cached(WordMode.OneWord, i.Key))).ToList();
        await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = workers }, async (item, _) =>
        {
            var speech = Speech16k(item.Fixture, item.Condition);
            var dtw = await engine.Run(WordMode.Dtw, speech, threads, item.Key);
            var oneWord = oneWordPass ? await engine.Run(WordMode.OneWord, speech, threads, item.Key) : null;
            bag.Add(new(item, dtw, oneWord, WordTimingSignal.Analyze(speech)));
            int count = Interlocked.Increment(ref done);
            if (count % 100 == 0) Console.WriteLine($"   {count}/{list.Count} items");
        });
        return bag.OrderBy(p => p.Item.Fixture.Index).ThenBy(p => Array.IndexOf(Conditions, p.Item.Condition)).ToList();
    }

    private static async Task<List<WordRecord>> Evaluate(IEnumerable<EvalItem> items, bool allMethods = true)
    {
        var records = new List<WordRecord>();
        foreach (var p in await Prepare(items, allMethods))
        {
            var methods = Methods(p.Dtw, p.OneWord, p.Features, allMethods);
            itemWords[p.Item.Key] = methods;
            foreach (var (name, words) in methods) records.AddRange(Score(p.Item, name, words, p.Features, p.Item.Fixture.Words));
        }
        return records;
    }

    /// <summary>
    /// Chooses the anchor-valley settings: every combination on a grid is scored by its share of bad cuts (with the
    /// recommended padding) on the odd-numbered sentences, and the best ones are shown with their score on the
    /// even-numbered sentences, which took no part in the choice.
    /// </summary>
    private static async Task Tune()
    {
        var prepared = await Prepare(Conditions.SelectMany(c => fixtures.Take(limit).Select(f => new EvalItem(f, c))), false);
        var tokens = prepared.Select(p => WordTimingMethods.FromTokens(p.Dtw)).ToList();
        // Where the true boundary lies between two neighbouring anchors (both words recognised, no pause between them).
        var afterLeft = new List<double>(); var beforeRight = new List<double>(); var fraction = new List<double>(); var levelAtTruth = new List<double>(); var levelAtLowest = new List<double>();
        for (int i = 0; i < prepared.Count; i++)
        {
            if (prepared[i].Item.Condition != "clean") continue;
            var truth = prepared[i].Item.Fixture.Words; var words = tokens[i]; var f = prepared[i].Features;
            var pairs = Align(truth.Select(w => w.Norm).ToList(), truth.Select(w => (w.StartMs + w.EndMs) / 2).ToList(), words.Select(w => w.Norm).ToList(), words.Select(w => (w.AStart + w.AEnd) / 2).ToList());
            for (int k = 1; k < pairs.Count; k++)
            {
                var (r0, h0) = pairs[k - 1]; var (r1, h1) = pairs[k];
                if (r0 < 0 || h0 < 0 || r1 < 0 || h1 < 0 || truth[r0].Norm != words[h0].Norm || truth[r1].Norm != words[h1].Norm || truth[r1].StartMs - truth[r0].EndMs > 1) continue;
                double left = words[h0].AnchorLast, right = words[h1].AnchorFirst, boundary = truth[r1].StartMs;
                if (double.IsNaN(left) || double.IsNaN(right) || right <= left) continue;
                afterLeft.Add(boundary - left); beforeRight.Add(right - boundary); fraction.Add((boundary - left) / (right - left));
                double lowest = double.PositiveInfinity; for (int q = f.Frame(left); q <= f.Frame(right); q++) lowest = Math.Min(lowest, f.Db[q]);
                double peak = f.Peak(left - 150, right + 150); levelAtTruth.Add(f.At(boundary) - peak); levelAtLowest.Add(lowest - peak);
            }
        }
        Heading("Where the true boundary lies between the DTW times of two neighbouring words (clean audio, no pause between them)");
        Table(["measure", "boundaries", "p5", "p25", "median", "p75", "p95"], new[] { ("true boundary minus left anchor, ms", afterLeft), ("right anchor minus true boundary, ms", beforeRight), ("fraction of the way from left to right anchor", fraction),
            ("level at the true boundary, dB under the local peak", levelAtTruth), ("lowest level between the anchors, dB under the local peak", levelAtLowest) }.Select(row =>
        {
            row.Item2.Sort();
            return new[] { row.Item1, row.Item2.Count.ToString(Inv), N(Percentile(row.Item2, .05), "0.##"), N(Percentile(row.Item2, .25), "0.##"), N(Percentile(row.Item2, .5), "0.##"), N(Percentile(row.Item2, .75), "0.##"), N(Percentile(row.Item2, .95), "0.##") };
        }));
        results["boundaryBetweenAnchors"] = new { boundaries = afterLeft.Count, afterLeftAnchorMsMedian = Percentile(afterLeft, .5), beforeRightAnchorMsMedian = Percentile(beforeRight, .5), fractionP25 = Percentile(fraction, .25), fractionMedian = Percentile(fraction, .5), fractionP75 = Percentile(fraction, .75),
            levelAtTruthDbMedian = Percentile(levelAtTruth, .5), lowestLevelDbMedian = Percentile(levelAtLowest, .5) };
        var grid = TuneGrid();
        var scores = new ConcurrentBag<(AnchorOptions Options, double[] Bad)>();
        Parallel.ForEach(grid, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 3) }, options =>
        {
            var bad = new int[4]; var hits = new int[4];
            for (int i = 0; i < prepared.Count; i++)
            {
                var p = prepared[i]; int slot = (p.Item.Fixture.Sentence % 2 == 1 ? 0 : 2) + (p.Item.Condition == "clean" ? 0 : 1);
                foreach (var r in Score(p.Item, "", WordTimingMethods.AnchorValley(tokens[i], p.Features, options), p.Features, p.Item.Fixture.Words))
                    if (r.Hit) { hits[slot]++; if (r.Bad(PadBefore, PadAfter)) bad[slot]++; }
            }
            scores.Add((options, bad.Select((b, k) => 100.0 * b / Math.Max(1, hits[k])).ToArray()));
        });
        Heading($"Tuning the anchor-valley settings ({prepared.Count} items): bad cut % with +{PadBefore}/{PadAfter} ms padding");
        var ranked = scores.OrderBy(s => s.Bad[0] + s.Bad[1]).ToList();
        var shown = ranked.Take(12).Concat(ranked.Where(s => s.Options == AnchorOptions.Recommended)).Concat(ranked.TakeLast(3)).Distinct().ToList();
        Table(["pause ms", "position prior dB", "anchor at", "sized by letters", "token prior dB / 100 ms", "tuning half: clean", "tuning half: noisy", "held-out half: clean", "held-out half: noisy", "rank of " + grid.Count],
            shown.Select(s => new[] { N(s.Options.PauseMs), N(s.Options.PhiDb), N(s.Options.AnchorAt, "0.00"), s.Options.ByLength ? "yes" : "no", N(s.Options.TokenDb), N(s.Bad[0]), N(s.Bad[1]), N(s.Bad[2]), N(s.Bad[3]), (ranked.IndexOf(s) + 1).ToString(Inv) + (s.Options == AnchorOptions.Recommended ? " (recommended)" : "") }));
        results["tuning"] = ranked.Select((s, k) => new { rank = k + 1, s.Options.PauseMs, s.Options.PhiDb, s.Options.AnchorAt, s.Options.ByLength, s.Options.TokenDb, tuningClean = Math.Round(s.Bad[0], 2), tuningNoisy = Math.Round(s.Bad[1], 2), heldOutClean = Math.Round(s.Bad[2], 2), heldOutNoisy = Math.Round(s.Bad[3], 2) }).ToList();
    }

    private static List<AnchorOptions> TuneGrid() =>
        (from pause in new[] { 120.0, 200, 300 } from phiDb in new[] { 0.0, 100, 200, 400, 800, 1600 } from at in new[] { 0.55, 0.62, 0.68 } from byLength in new[] { false, true } from tokenDb in new[] { 0.0, 3, 6, 12 }
         where phiDb > 0 || at == 0.62 && byLength select new AnchorOptions(pause, phiDb, at, byLength, tokenDb, AnchorOptions.Recommended.Trim)).Append(AnchorOptions.Recommended).Distinct().ToList();

    private static async Task Accuracy()
    {
        var items = Conditions.SelectMany(c => fixtures.Take(limit).Select(f => new EvalItem(f, c))).ToList();
        if (cachedOnly) items = items.Where(i => engine.Cached(WordMode.Dtw, i.Key) && engine.Cached(WordMode.OneWord, i.Key)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        accuracyRecords = await Evaluate(items);
        Console.WriteLine($"accuracy: {items.Count} items in {clock.Elapsed.TotalSeconds:0} s");
        var byMethod = accuracyRecords.GroupBy(r => r.Method).ToDictionary(g => g.Key, g => g.ToList());
        var names = Methods(new RawResult([]), new RawResult([]), WordTimingSignal.Analyze(new float[1600])).Select(m => m.Name).ToList();
        string[] keyMethods = ["P phrase, proportional", "D phrase + signal DP", "A token", "A + snap 80 + trim", "C one word", "B dtw as end", Best];
        var json = new Dictionary<string, object?>();

        // Ground-truth sanity: where the synthesizer marks a word after a pause, does sound start there?
        var onsets = new List<double>();
        foreach (var fixture in fixtures)
            for (int i = 0; i < fixture.Words.Length; i++)
                if (i == 0 || fixture.Words[i].MarkMs - fixture.Words[i - 1].EndMs >= 100) onsets.Add(fixture.Words[i].StartMs - fixture.Words[i].MarkMs);
        onsets.Sort();
        Heading("Ground truth check");
        markdown.AppendLine($"Words that follow at least 100 ms of silence: {onsets.Count}. Sound begins {N(Percentile(onsets, .5))} ms (median), {N(Percentile(onsets, .9))} ms (p90), {N(onsets[^1])} ms (max) after the synthesizer's word mark.");
        json["groundTruthOnsetAfterMarkMs"] = new { words = onsets.Count, median = Percentile(onsets, .5), p90 = Percentile(onsets, .9), max = onsets[^1] };

        // Does turning DTW on change the token timestamps or the phrases? (It decides whether one pass can serve A, B and today's phrases.)
        int compared = 0, phraseDifferences = 0, tokenDifferences = 0;
        foreach (var fixture in fixtures.Where(f => f.RateName == "normal").Take(Math.Min(limit, 24)))
        {
            var speech = Speech16k(fixture, "clean");
            var a = await engine.Run(WordMode.Phrase, speech, threads, fixture.Id + "__clean"); var b = await engine.Run(WordMode.Tokens, speech, threads, fixture.Id + "__clean"); var c = await engine.Run(WordMode.Dtw, speech, threads, fixture.Id + "__clean");
            compared++;
            if (!a.Segments.Select(s => (s.StartMs, s.EndMs, s.Text)).SequenceEqual(c.Segments.Select(s => (s.StartMs, s.EndMs, s.Text)))) phraseDifferences++;
            if (!b.Segments.SelectMany(s => s.Tokens).Select(t => (t.Id, t.StartCs, t.EndCs)).SequenceEqual(c.Segments.SelectMany(s => s.Tokens).Select(t => (t.Id, t.StartCs, t.EndCs)))) tokenDifferences++;
        }
        markdown.AppendLine().AppendLine($"One pass for everything: on {compared} clean files, the DTW pass gave different phrases than today's pass in {phraseDifferences} and different token timestamps than a token-timestamp pass in {tokenDifferences}.");
        json["onePassCheck"] = new { files = compared, phraseDifferences, tokenDifferences };

        // Where the DTW time falls inside the word it belongs to.
        Heading("Where the DTW time falls");
        var anchorRows = new List<string[]>(); var anchorJson = new List<object>();
        foreach (var condition in Conditions)
        {
            var positions = new List<double>(); int inside = 0, total = 0;
            foreach (var item in items.Where(i => i.Condition == condition))
            {
                var tokens = itemWords[item.Key].First(m => m.Name == "A token").Words;
                var pairs = Align(item.Fixture.Words.Select(w => w.Norm).ToList(), item.Fixture.Words.Select(w => (w.StartMs + w.EndMs) / 2).ToList(), tokens.Select(w => w.Norm).ToList(), tokens.Select(w => (w.AStart + w.AEnd) / 2).ToList());
                foreach (var (r, h) in pairs)
                {
                    if (r < 0 || h < 0 || item.Fixture.Words[r].Norm != tokens[h].Norm || double.IsNaN(tokens[h].AnchorFirst)) continue;
                    var truth = item.Fixture.Words[r]; total++;
                    if (tokens[h].AnchorFirst >= truth.StartMs && tokens[h].AnchorLast <= truth.EndMs) inside++;
                    positions.Add((tokens[h].AnchorFirst - truth.StartMs) / Math.Max(1, truth.EndMs - truth.StartMs));
                }
            }
            positions.Sort();
            anchorRows.Add([condition, total.ToString(Inv), N(100.0 * inside / Math.Max(1, total)), N(Percentile(positions, .1), "0.00"), N(Percentile(positions, .5), "0.00"), N(Percentile(positions, .9), "0.00")]);
            anchorJson.Add(new { condition, words = total, insideOwnWordPct = Math.Round(100.0 * inside / Math.Max(1, total), 1), positionP10 = Percentile(positions, .1), positionMedian = Percentile(positions, .5), positionP90 = Percentile(positions, .9) });
        }
        Table(["condition", "words", "DTW time inside its own word %", "position p10 (0 = start, 1 = end)", "median", "p90"], anchorRows);
        json["dtwAnchor"] = anchorJson;

        // The main tables: every method, per condition.
        var tables = new List<object>();
        void Slice(string title, Func<EvalItem, bool> pick, IEnumerable<string> methods)
        {
            Heading(title);
            var rows = new List<string[]>();
            foreach (var name in methods)
            {
                var records = byMethod[name].Where(r => pick(r.Item)).ToList();
                rows.Add(SummaryRow(name, records));
                tables.Add(new { slice = title, method = name, raw = Summarize(records), padded = Summarize(records, PadBefore, PadAfter) });
            }
            Table(SummaryHeader, rows);
        }
        foreach (var condition in Conditions) Slice($"All methods, {condition} (all voices and speeds)", i => i.Condition == condition, names);
        Slice("All methods, every condition pooled", _ => true, names);
        foreach (var rate in fixtures.Select(f => f.RateName).Distinct()) Slice($"Speed {rate}, clean", i => i.Condition == "clean" && i.Fixture.RateName == rate, keyMethods);
        foreach (var rate in fixtures.Select(f => f.RateName).Distinct()) Slice($"Speed {rate}, the four noisy conditions pooled", i => i.Condition != "clean" && i.Fixture.RateName == rate, keyMethods);
        foreach (var voice in fixtures.Select(f => f.Voice).Distinct()) Slice($"Voice {voice}, clean", i => i.Condition == "clean" && i.Fixture.Voice == voice, keyMethods);
        foreach (var voice in fixtures.Select(f => f.Voice).Distinct()) Slice($"Voice {voice}, the four noisy conditions pooled", i => i.Condition != "clean" && i.Fixture.Voice == voice, keyMethods);
        foreach (var tag in fixtures.Select(f => f.Tag).Distinct()) Slice($"Sentence type \"{tag}\", clean", i => i.Condition == "clean" && i.Fixture.Tag == tag, keyMethods);

        // The strongest split of all: is the word separated from its neighbours by pauses, or run together with them?
        // (From the ground truth: a side is "open" when the word is first/last or at least 40 ms of silence separates it.)
        Heading("By how the word sits in the sentence (reference pauses), recognised words only");
        var contextRows = new List<string[]>(); var contextJson = new List<object>();
        static int OpenSides(WordRecord r) => (double.IsNaN(r.PrevEnd) || r.GtStart - r.PrevEnd >= 40 ? 1 : 0) + (double.IsNaN(r.NextStart) || r.NextStart - r.GtEnd >= 40 ? 1 : 0);
        foreach (var name in new[] { "A token", "D phrase + signal DP", Best })
            foreach (var (label, pick) in new (string, Func<EvalItem, bool>)[] { ("clean", i => i.Condition == "clean"), ("noisy", i => i.Condition != "clean") })
                foreach (var (context, sides) in new[] { ("pause on both sides", 2), ("pause on one side", 1), ("run together on both sides", 0) })
                {
                    var hits = byMethod[name].Where(r => r.Hit && pick(r.Item) && OpenSides(r) == sides).ToList();
                    var raw = Summarize(hits); var padded = Summarize(hits, PadBefore, PadAfter);
                    contextRows.Add([name, label, context, hits.Count.ToString(Inv), $"{N(raw.StartMedian)} / {N(raw.StartP90)}", $"{N(raw.EndMedian)} / {N(raw.EndP90)}", N(raw.BadPct), N(padded.BadPct), N(padded.StrictBadPct)]);
                    contextJson.Add(new { method = name, audio = label, context, words = hits.Count, raw, padded });
                }
        Table(["method", "audio", "word position", "words", "start |err| med / p90 ms", "end |err| med / p90 ms", "bad cut %", "bad cut % padded", "strict bad % padded"], contextRows);
        json["byPauseContext"] = contextJson;
        json["tables"] = tables;

        // Padding: bad-cut share for a grid of padding before/after.
        double[] before = [0, 10, 20, 30, 40, 60], after = [0, 10, 20, 30, 40, 60, 80];
        var paddingJson = new List<object>();
        foreach (var name in new[] { Best, "A token", "C one word" })
            foreach (var (label, pick) in new (string, Func<EvalItem, bool>)[] { ("clean", i => i.Condition == "clean"), ("noisy conditions pooled", i => i.Condition != "clean") })
            {
                Heading($"Padding sweep, {name}, {label}: bad cut % (rows: ms added before the start; columns: ms added after the end)");
                var hits = byMethod[name].Where(r => r.Hit && pick(r.Item)).ToList(); var rows = new List<string[]>();
                foreach (var b in before)
                {
                    var row = new List<string> { b.ToString(Inv) };
                    foreach (var a in after)
                    {
                        double bad = 100.0 * hits.Count(r => r.Bad(b, a)) / Math.Max(1, hits.Count); row.Add(N(bad));
                        paddingJson.Add(new { method = name, slice = label, padBeforeMs = b, padAfterMs = a, badPct = Math.Round(bad, 2),
                            clipPct = Math.Round(100.0 * hits.Count(r => r.Damage(b, a).Clip > 30) / Math.Max(1, hits.Count), 2), neighbourPct = Math.Round(100.0 * hits.Count(r => r.Damage(b, a).Neighbour > 60) / Math.Max(1, hits.Count), 2) });
                    }
                    rows.Add(row.ToArray());
                }
                Table(new[] { "before \\ after" }.Concat(after.Select(a => a.ToString(Inv))).ToArray(), rows);
            }
        json["padding"] = paddingJson;

        // Label-free: how loud is the audio at the cut, relative to the word's loudest frame?
        Heading("Label-free: level at the cut points relative to the word's loudest frame (all recognised words, unpadded)");
        var cutRows = new List<string[]>(); var cutJson = new List<object>();
        foreach (var name in names)
            foreach (var (label, pick) in new (string, Func<EvalItem, bool>)[] { ("clean", i => i.Condition == "clean"), ("noisy", i => i.Condition != "clean") })
            {
                var levels = byMethod[name].Where(r => r.HasHyp && pick(r.Item)).SelectMany(r => new[] { r.StartDb, r.EndDb }).OrderBy(v => v).ToList();
                double loud = 100.0 * levels.Count(v => v > -10) / Math.Max(1, levels.Count);
                cutRows.Add([name, label, N(Percentile(levels, .5)), N(Percentile(levels, .9)), N(loud)]);
                cutJson.Add(new { method = name, slice = label, medianDb = Percentile(levels, .5), p90Db = Percentile(levels, .9), within10DbOfPeakPct = Math.Round(loud, 1) });
            }
        Table(["method", "audio", "median dB", "p90 dB", "cuts within 10 dB of the word's peak %"], cutRows);
        json["cutLevel"] = cutJson;

        // Which signal predicts a bad boundary (and a wrong word)?
        Heading($"Uncertainty signals for {Best}: area under the ROC curve (0.5 = useless, 1 = perfect)");
        var signals = new (string Name, Func<WordRecord, double> Score)[]
        {
            ("low token probability (min in word)", r => -r.MinP), ("low token probability (geometric mean)", r => -r.MeanP), ("low phrase probability", r => -r.SegP),
            ("loud at the cut (max of start/end, dB under word peak)", r => Math.Max(r.StartDb, r.EndDb)), ("word barely above the noise floor", r => -r.PeakOverFloor),
            ("disagrees with token timestamps (ms)", r => r.TokenDisagreeMs), ("sides without a pause (0, 1, 2)", r => 2 - r.PauseSides),
            ("longest stretch between anchors on a side without a pause (ms)", r => r.GapMs), ("level above silence at a boundary without a pause (dB)", r => r.GapLevelDb),
            ("stretch (ms) x level (dB)", r => r.GapMs * (5 + r.GapLevelDb)),
            ("implausible length (ms per letter, far from 70)", r => Math.Abs(Math.Log(Math.Max(5, r.MsPerChar) / 70))),
            ("very short (ms per letter)", r => -r.MsPerChar),
        };
        var aucRows = new List<string[]>(); var aucJson = new List<object>();
        var best = byMethod[Best];
        foreach (var (signal, score) in signals)
        {
            double Area(Func<EvalItem, bool> pick) => Auc(best.Where(r => r.Hit && pick(r.Item)).Select(r => (score(r), r.Bad(PadBefore, PadAfter))));
            double wrong = Auc(best.Where(r => r.HasHyp).Select(r => (score(r), !r.Hit)));
            double clean = Area(i => i.Condition == "clean"), noisy = Area(i => i.Condition != "clean"), all = Area(_ => true);
            aucRows.Add([signal, N(clean, "0.000"), N(noisy, "0.000"), N(all, "0.000"), N(wrong, "0.000")]);
            aucJson.Add(new { signal, badBoundaryClean = clean, badBoundaryNoisy = noisy, badBoundaryAll = all, wrongWord = wrong });
        }
        Table(["signal", "bad boundary, clean", "bad boundary, noisy", "bad boundary, all", "wrong or invented word, all"], aucRows);
        json["uncertaintyAuc"] = aucJson;

        // Calibration: how often is the cut bad, or the word wrong, for each value of the two signals that carry information?
        Heading($"Calibration for {Best}: bad cuts by the longest anchor-to-anchor stretch on a side without a pause (padded +{PadBefore}/{PadAfter} ms)");
        var gapBuckets = new (string Name, Func<WordRecord, bool> In)[] { ("pause on both sides", r => r.PauseSides == 2), ("under 150 ms", r => r.PauseSides < 2 && r.GapMs < 150), ("150 to 250 ms", r => r.PauseSides < 2 && r.GapMs is >= 150 and < 250),
            ("250 to 350 ms", r => r.PauseSides < 2 && r.GapMs is >= 250 and < 350), ("350 to 500 ms", r => r.PauseSides < 2 && r.GapMs is >= 350 and < 500), ("500 ms or more", r => r.PauseSides < 2 && r.GapMs >= 500) };
        var calibration = new List<object>();
        Table(["stretch", "clean: words", "bad cut %", "noisy: words", "bad cut %"], gapBuckets.Select(bucket =>
        {
            var clean = best.Where(r => r.Hit && r.Item.Condition == "clean" && bucket.In(r)).ToList(); var noisy = best.Where(r => r.Hit && r.Item.Condition != "clean" && bucket.In(r)).ToList();
            double cleanBad = 100.0 * clean.Count(r => r.Bad(PadBefore, PadAfter)) / Math.Max(1, clean.Count), noisyBad = 100.0 * noisy.Count(r => r.Bad(PadBefore, PadAfter)) / Math.Max(1, noisy.Count);
            calibration.Add(new { signal = "stretch", bucket = bucket.Name, cleanWords = clean.Count, cleanBadPct = Math.Round(cleanBad, 1), noisyWords = noisy.Count, noisyBadPct = Math.Round(noisyBad, 1) });
            return new[] { bucket.Name, clean.Count.ToString(Inv), N(cleanBad), noisy.Count.ToString(Inv), N(noisyBad) };
        }));
        Heading($"Calibration for {Best}: wrong or invented words by the lowest token probability in the word");
        var probabilityBuckets = new (string Name, double From, double To)[] { ("under 0.3", 0, 0.3), ("0.3 to 0.5", 0.3, 0.5), ("0.5 to 0.7", 0.5, 0.7), ("0.7 to 0.9", 0.7, 0.9), ("0.9 or more", 0.9, 2) };
        Table(["lowest token probability", "clean: words", "wrong %", "noisy: words", "wrong %"], probabilityBuckets.Select(bucket =>
        {
            var clean = best.Where(r => r.HasHyp && r.Item.Condition == "clean" && r.MinP >= bucket.From && r.MinP < bucket.To).ToList(); var noisy = best.Where(r => r.HasHyp && r.Item.Condition != "clean" && r.MinP >= bucket.From && r.MinP < bucket.To).ToList();
            double cleanWrong = 100.0 * clean.Count(r => !r.Hit) / Math.Max(1, clean.Count), noisyWrong = 100.0 * noisy.Count(r => !r.Hit) / Math.Max(1, noisy.Count);
            calibration.Add(new { signal = "minTokenProbability", bucket = bucket.Name, cleanWords = clean.Count, cleanBadPct = Math.Round(cleanWrong, 1), noisyWords = noisy.Count, noisyBadPct = Math.Round(noisyWrong, 1) });
            return new[] { bucket.Name, clean.Count.ToString(Inv), N(cleanWrong), noisy.Count.ToString(Inv), N(noisyWrong) };
        }));
        json["calibration"] = calibration;

        // The concrete rule the report proposes, scored: three states per word.
        Heading("The proposed word status, scored (problem = wrong/invented word, or a bad cut with the recommended padding)");
        var ruleRows = new List<string[]>(); var ruleJson = new List<object>();
        foreach (var (label, pick) in new (string, Func<EvalItem, bool>)[] { ("clean", i => i.Condition == "clean"), ("pink+10", i => i.Condition == "pink+10"), ("pink0", i => i.Condition == "pink0"), ("music0", i => i.Condition == "music0"), ("voice-6", i => i.Condition == "voice-6"), ("all", _ => true) })
            foreach (var status in new[] { "done", "estimated", "uncertain" })
            {
                var all = best.Where(r => r.HasHyp && pick(r.Item)).ToList(); var words = all.Where(r => Status(r.MinP, r.GapMs) == status).ToList();
                int wrong = words.Count(r => !r.Hit), badCut = words.Count(r => r.Hit && r.Bad(PadBefore, PadAfter));
                ruleRows.Add([label, status, words.Count.ToString(Inv), N(100.0 * words.Count / Math.Max(1, all.Count)), N(100.0 * wrong / Math.Max(1, words.Count)), N(100.0 * badCut / Math.Max(1, words.Count)), N(100.0 * (wrong + badCut) / Math.Max(1, words.Count))]);
                ruleJson.Add(new { audio = label, status, words = words.Count, sharePct = Math.Round(100.0 * words.Count / Math.Max(1, all.Count), 1), wrongWordPct = Math.Round(100.0 * wrong / Math.Max(1, words.Count), 1),
                    badCutPct = Math.Round(100.0 * badCut / Math.Max(1, words.Count), 1), problemPct = Math.Round(100.0 * (wrong + badCut) / Math.Max(1, words.Count), 1) });
            }
        Table(["audio", "status", "words", "share of words %", "wrong word %", "bad cut %", "any problem %"], ruleRows);
        json["wordStatus"] = ruleJson;
        results["accuracy"] = json;
    }

    /// <summary>
    /// The word status the report proposes, from two things known without any reference: the lowest token
    /// probability in the word, and the longest anchor-to-anchor stretch in which one of its edges had to be placed
    /// (0 when both edges sit at pauses). "uncertain": Whisper doubted a token, or an edge lies somewhere in a long
    /// unbroken stretch. "done": confident tokens and both edges at a pause or in a short stretch. Else "estimated".
    /// </summary>
    public static string Status(double minTokenProbability, double longestStretchMs)
        => minTokenProbability < UncertainProbability || longestStretchMs >= UncertainStretchMs ? "uncertain"
         : minTokenProbability >= DoneProbability && longestStretchMs < DoneStretchMs ? "done" : "estimated";
    public const double UncertainProbability = 0.5, UncertainStretchMs = 300, DoneProbability = 0.7, DoneStretchMs = 200;

    private static async Task Dump(string[] names, string condition)
    {
        foreach (var name in names)
        {
            var fixture = fixtures.First(f => f.Id == name); var speech = Speech16k(fixture, condition);
            Console.WriteLine($"== {name} ({condition}): {fixture.Text}");
            Console.WriteLine("   truth: " + string.Join(" ", fixture.Words.Select(w => $"{w.Text}[{w.StartMs:0}-{w.EndMs:0}]")));
            var dtw = await engine.Run(WordMode.Dtw, speech, threads); var oneWord = await engine.Run(WordMode.OneWord, speech, threads);
            foreach (var s in dtw.Segments)
            {
                Console.WriteLine($"   [{s.StartMs:0}-{s.EndMs:0}] p={s.Prob:0.00} \"{s.Text}\"");
                foreach (var t in s.Tokens) Console.WriteLine($"      {t.StartCs * 10,6}-{t.EndCs * 10,-6} dtw={t.DtwCs * 10,-6} p={t.P:0.00} \"{t.Text}\"");
            }
            foreach (var (method, words) in Methods(dtw, oneWord, WordTimingSignal.Analyze(speech))) Console.WriteLine($"   {method,-30} {WordTimingMethods.Describe(words)}");
        }
    }
}
