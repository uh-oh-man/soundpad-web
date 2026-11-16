using System.Globalization;
using System.Text;
using System.Text.Json;
namespace SoundpadWeb.Testing;

// --audio-quality-eval <demo folder> --model <ggml file> [--out <folder>] [--stage dump|all]
//
// WP5 of the overhaul ("prototype grouping/extraction and pacing quality against real representative audio before
// relying on them"). Measures, offline and with the companion's own code:
//   1. word timing of the history pipeline (Whisper + WordAligner) against hand-checked word edges;
//   2. sentence joins (SentenceComposer + SentenceRender): clicks at joins, loudness and noise-floor steps between
//      pieces, gap lengths against the timing setting, bleep level, for Smooth / Natural / Chaotic / Original pacing,
//      approved splices, interruptions and a nonverbal piece, with the pipeline's word times and with the hand times;
//   3. intelligibility: each rendered sentence transcribed again with Whisper, word error rate against the text;
//   4. speaker grouping (SpeakerGrouping.cs) on the clips' phrases, with synthetic "other speakers";
//   plus a synthetic-tone check of the click detector and of the joins (tones cut at awkward phases).
// The only speech is the public-domain Apollo 11 clips in web/public/demo (NASA; see CREDITS.md); the nonverbal piece
// is the CC0 audience laugh from the same folder; everything else is synthetic. It opens no audio device, no
// microphone and no Soundpad pipe, reads the model and the demo files only, and writes only into --out (tables.md,
// results.json and the rendered sentences as WAVs, for listening). Metrics: AudioQualityMetrics.cs.
public static partial class AudioQualityEval
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record TruthWord(string Text, double StartMs, double EndMs);

    /// <summary>A clip of the evaluation, decoded with the companion's own decoder (AudioFile, as library recordings are).</summary>
    public sealed record Clip(string Id, string File, string Voice, string Text, string Audible, TruthWord[] Truth, TruthWord[] Demo, float[] Mono, int Rate)
    {
        public float[] Speech16k => speech ??= MimicTranscriber.Resample(Mono, Rate, MimicTranscriber.SpeechRate);
        private float[]? speech;
    }

    // Hand-checked word edges (ms of the decoded file), read off 1 ms-per-pixel spectrograms and a 10 ms table of
    // voiced-band energy, high-band energy and autocorrelation voicing (scratch tooling, not part of the repo).
    // Edges next to silence are good to about +-15 ms; edges between words spoken without a pause, and edges inside
    // the radio static, to about +-30 ms. "Houston" is not audible in the eagle clip: its first 510 ms are static
    // with no voicing at all; the voiced "tran" of Tranquility follows a "t" burst at 515 ms.
    private static readonly TruthWord[] SmallStep =
    [
        new("That's", 95, 300), new("one", 300, 500), new("small", 500, 1175), new("step", 1175, 1560), new("for", 1665, 1810), new("man", 1810, 2190),
        new("one", 3815, 3995), new("giant", 4855, 5200), new("leap", 5235, 5420), new("for", 5550, 5710), new("mankind", 5710, 6540),
    ];
    private static readonly TruthWord[] Eagle =
    [
        new("Tranquility", 510, 1140), new("Base", 1170, 1400), new("here", 1465, 1740), new("The", 2045, 2205), new("Eagle", 2205, 2470), new("has", 2470, 2650), new("landed", 2700, 3060),
    ];
    // A second, independent annotation: the example's word times in web/src/demo/library.ts up to 2.44 ("approximate,
    // about +-0.15 s"). The evaluation also reads the current ones from that file (ExampleWords) and scores them.
    private static readonly TruthWord[] SmallStepDemo =
    [
        new("That's", 100, 290), new("one", 320, 540), new("small", 860, 1170), new("step", 1180, 1510), new("for", 1700, 1810), new("man", 1850, 2180),
        new("one", 3810, 4000), new("giant", 4870, 5120), new("leap", 5240, 5400), new("for", 5570, 5670), new("mankind", 5750, 6550),
    ];
    private static readonly TruthWord[] EagleDemo =
    [
        new("Tranquility", 740, 1380), new("Base", 1440, 1620), new("here", 1620, 1740), new("The", 2040, 2140), new("Eagle", 2140, 2440), new("has", 2440, 2640), new("landed", 2700, 3000),
    ];

    private static readonly (string Id, string File, string Voice, string Text, string Audible, TruthWord[] Truth, TruthWord[] Demo)[] ClipList =
    [
        ("small-step", "apollo11-small-step.mp3", "armstrong-a", "That's one small step for man, one giant leap for mankind.", "That's one small step for man, one giant leap for mankind.", SmallStep, SmallStepDemo),
        ("eagle", "apollo11-eagle-landed.mp3", "armstrong-b", "Houston, Tranquility Base here. The Eagle has landed.", "Tranquility Base here. The Eagle has landed.", Eagle, EagleDemo),
    ];

    private static readonly StringBuilder Md = new();
    private static readonly Dictionary<string, object> Json = [];
    private static void Line(string text = "") { Console.WriteLine(text); Md.AppendLine(text); }
    private static void Heading(string text) { Line(); Line("## " + text); Line(); }
    private static void Table(string[] header, IEnumerable<string[]> rows)
    {
        Line("| " + string.Join(" | ", header) + " |");
        Line("|" + string.Concat(header.Select((h, i) => i == 0 ? " --- |" : " ---: |")));
        foreach (var row in rows) Line("| " + string.Join(" | ", row) + " |");
    }
    private static string F(double value, string format = "0") => double.IsNaN(value) ? "n/a" : value.ToString(format, Inv);

    public static async Task<int> Run(string[] args)
    {
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var demo = Path.GetFullPath(args[1]);
        var model = Option("--model", "");
        var outDir = Path.GetFullPath(Option("--out", Path.Combine(Path.GetTempPath(), "soundpadweb-audio-eval")));
        var stage = Option("--stage", "all");
        if (!File.Exists(model)) { Console.Error.WriteLine("Speech model not found (pass --model <ggml file>): " + model); return 2; }
        Directory.CreateDirectory(outDir);
        var clips = ClipList.Select(c => { var (mono, rate) = AudioFile.ReadMono(Path.Combine(demo, c.File)); return new Clip(c.Id, c.File, c.Voice, c.Text, c.Audible, c.Truth, c.Demo, mono, rate); }).ToList();
        var (laugh, laughRate) = AudioFile.ReadMono(Path.Combine(demo, "audience-laugh.mp3"));
        using var engine = new WhisperEngine(model, null, null);
        var mode = SubtitleModes.Parse(Option("--mode", "gaming")) ?? SubtitleMode.Gaming;
        engine.UseMode(mode); // threads only: the words are the same in every mode
        await engine.Transcribe(new float[MimicTranscriber.SpeechRate], default); // loads the model
        if (stage == "dump")
        {
            foreach (var clip in clips)
            {
                File.WriteAllBytes(Path.Combine(outDir, clip.Id + "-16k.wav"), MimicRing.Wav(clip.Speech16k, MimicTranscriber.SpeechRate));
                var words = await HistoryWords(engine, clip.Speech16k);
                Console.WriteLine($"{clip.Id}: " + string.Join(" | ", words.Select(w => $"{w.Text} {w.StartMs:0}-{w.EndMs:0}")));
            }
            return 0;
        }
        Line("# Audio-quality evaluation (WP5)");
        Line();
        Line($"Speech: {string.Join(", ", clips.Select(c => $"{c.File} ({c.Mono.Length * 1000.0 / c.Rate:0} ms, {c.Rate} Hz)"))}. Nonverbal: audience-laugh.mp3. Model: {Path.GetFileName(model)} (a copy, read only). Whisper: the companion's settings, {SubtitleModes.Name(mode)} mode.");

        var library = await WordTiming(engine, clips, ExampleWords(Path.Combine(demo, "..", "..", "src", "demo", "library.ts")));
        var renders = Sentences(clips, library, laugh, laughRate, outDir);
        await Intelligibility(engine, clips, library, renders);
        Tones(outDir);
        Grouping(clips, library.History, laugh, laughRate);

        File.WriteAllText(Path.Combine(outDir, "tables.md"), Md.ToString());
        File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(Json, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine("Wrote " + Path.Combine(outDir, "tables.md") + ", results.json and renders\\*.wav");
        return 0;
    }

    /// <summary>The example's word times per file, read from web/src/demo/library.ts (empty when it is not there).</summary>
    public static Dictionary<string, List<(string Text, double Start, double End)>> ExampleWords(string path)
    {
        var result = new Dictionary<string, List<(string, double, double)>>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        string? file = null;
        foreach (var line in File.ReadLines(path))
        {
            var named = System.Text.RegularExpressions.Regex.Match(line, @"file: '([^']+)'");
            if (named.Success) { file = named.Groups[1].Value; continue; }
            var word = System.Text.RegularExpressions.Regex.Match(line, @"\{ w: (['""])(.+?)\1, startMs: (\d+), endMs: (\d+) \}");
            if (word.Success && file != null)
            {
                if (!result.TryGetValue(file, out var list)) result[file] = list = [];
                list.Add((word.Groups[2].Value, double.Parse(word.Groups[3].Value, Inv), double.Parse(word.Groups[4].Value, Inv)));
            }
        }
        return result;
    }

    /// <summary>
    /// The history's word pipeline for one chunk (MimicTranscriber.Recognise without the hold-back at a forced cut):
    /// Whisper with DTW and token timestamps, then WordAligner's anchor-valley on the chunk's own speech energy.
    /// </summary>
    public static async Task<List<AlignedWord>> HistoryWords(WhisperEngine engine, float[] speech16k)
    {
        var energy = SpeechEnergy.Analyze(speech16k);
        var spans = await engine.Transcribe(speech16k, default);
        return WordAligner.Align(spans, energy);
    }

    /// <summary>A library recording's words (MimicTranscriber.WordsOfFile: background Whisper, then WordAligner and the stored status).</summary>
    public static async Task<List<LibraryWord>> LibraryWords(WhisperEngine engine, float[] speech16k)
    {
        var spans = await engine.TranscribeBackground(speech16k, default);
        var energy = SpeechEnergy.Analyze(speech16k);
        double snr = energy.PeakDb - energy.FloorDb;
        return [.. WordAligner.Align(spans, energy).Select(w => new LibraryWord((long)Math.Round(w.StartMs), Math.Max((long)Math.Round(w.StartMs) + 1, (long)Math.Round(w.EndMs)),
            w.Text, w.Probability, WordAligner.Status(w.Probability, w.StretchMs, false, snr) == WordStatus.Uncertain)).Where(w => w.Text.Length > 0)];
    }

    // ---- 1. Word timing ------------------------------------------------------------------------------------------

    public sealed record LibraryState(Dictionary<string, List<LibraryWord>> Library, Dictionary<string, List<AlignedWord>> History);

    private static async Task<LibraryState> WordTiming(WhisperEngine engine, List<Clip> clips, Dictionary<string, List<(string Text, double Start, double End)>> example)
    {
        Heading("1. Word timing against hand-checked edges");
        Line("Errors are |recognised - hand| in ms for each hand word matched to a recognised word (edit-distance alignment of the word sequences; a misrecognised word still counts for timing). Bad cut: the word as the library would cut it (20 ms before, 10 ms after) loses more than 50 ms of the hand word or takes more than 50 ms of a neighbouring hand word. Conditions: the clip as it is (bare); with 1 s of digital silence on both sides (as a VoIP-gated history chunk); with 1 s of quiet pink noise at -50 dBFS on both sides (room tone).");
        var library = new Dictionary<string, List<LibraryWord>>(); var history = new Dictionary<string, List<AlignedWord>>();
        var rows = new List<string[]>(); var json = new List<object>(); var errors = new Dictionary<string, List<double>>(); var perWordRows = new List<string[]>();
        void Score(string clipId, string condition, TruthWord[] truth, IReadOnlyList<(string Text, double Start, double End)> hyp, bool record = true)
        {
            var pairs = AudioQualityMetrics.AlignWords(truth.Select(t => WordIndex.Token(t.Text)).ToList(), hyp.Select(h => WordIndex.Token(h.Text)).ToList());
            var starts = new List<double>(); var ends = new List<double>(); int bad = 0, hits = 0;
            var perWord = new List<object>();
            foreach (var (t, h) in pairs)
            {
                if (t < 0 || h < 0) continue;
                var tw = truth[t]; var hw = hyp[h];
                double se = Math.Abs(hw.Start - tw.StartMs), ee = Math.Abs(hw.End - tw.EndMs);
                starts.Add(se); ends.Add(ee);
                if (WordIndex.Token(tw.Text) == WordIndex.Token(hw.Text)) hits++;
                double cutStart = hw.Start - SentenceComposer.PadBeforeMs, cutEnd = hw.End + SentenceComposer.PadAfterMs;
                double lost = Math.Max(0, cutStart - tw.StartMs) + Math.Max(0, tw.EndMs - cutEnd);
                double taken = 0;
                for (int k = 0; k < truth.Length; k++) if (k != t) taken = Math.Max(taken, Math.Max(0, Math.Min(cutEnd, truth[k].EndMs) - Math.Max(cutStart, truth[k].StartMs)));
                bool isBad = lost > 50 || taken > 50; if (isBad) bad++;
                perWord.Add(new { truth = tw.Text, heard = hw.Text, truthStart = tw.StartMs, truthEnd = tw.EndMs, start = Math.Round(hw.Start), end = Math.Round(hw.End), lost = Math.Round(lost), taken = Math.Round(taken), bad = isBad });
            }
            var all = starts.Concat(ends).ToList();
            if (record) { var key = condition; if (!errors.TryGetValue(key, out var list)) errors[key] = list = []; list.AddRange(all); }
            rows.Add([clipId, condition, $"{hits}/{truth.Length}", F(AudioQualityMetrics.Percentile(starts, 50)), F(AudioQualityMetrics.Percentile(starts, 95)), F(AudioQualityMetrics.Percentile(ends, 50)), F(AudioQualityMetrics.Percentile(ends, 95)), F(AudioQualityMetrics.Percentile(all, 50)), F(AudioQualityMetrics.Percentile(all, 95)), $"{bad}/{starts.Count}"]);
            json.Add(new { clip = clipId, condition, words = perWord });
            if (condition == "history, bare") perWordRows.AddRange(pairs.Where(p => p.Ref >= 0).Select(p => new[] { clipId, truth[p.Ref].Text, $"{truth[p.Ref].StartMs:0}-{truth[p.Ref].EndMs:0}", p.Hyp < 0 ? "(none)" : hyp[p.Hyp].Text, p.Hyp < 0 ? "" : $"{hyp[p.Hyp].Start:0}-{hyp[p.Hyp].End:0}", p.Hyp < 0 ? "" : F(hyp[p.Hyp].Start - truth[p.Ref].StartMs, "+0;-0;0"), p.Hyp < 0 ? "" : F(hyp[p.Hyp].End - truth[p.Ref].EndMs, "+0;-0;0") }));
        }
        foreach (var clip in clips)
        {
            var speech = clip.Speech16k; int second = MimicTranscriber.SpeechRate;
            // Bare: also the words a library recording of this file gets.
            var bare = await HistoryWords(engine, speech);
            history[clip.Id] = bare;
            Score(clip.Id, "history, bare", clip.Truth, [.. bare.Select(w => (w.Text, w.StartMs, w.EndMs))]);
            var silence = new float[second];
            var padded = silence.Concat(speech).Concat(silence).ToArray();
            var words = await HistoryWords(engine, padded);
            Score(clip.Id, "history, 1 s digital silence", clip.Truth, [.. words.Select(w => (w.Text, w.StartMs - 1000, w.EndMs - 1000))]);
            var pink = WordTimingSignal.PinkNoise(second, 3); double rms = Math.Sqrt(pink.Average(x => (double)x * x)); var room = pink.Select(x => (float)(x / rms * Math.Pow(10, -50 / 20.0))).ToArray();
            words = await HistoryWords(engine, room.Concat(speech).Concat(room).ToArray());
            Score(clip.Id, "history, 1 s room tone", clip.Truth, [.. words.Select(w => (w.Text, w.StartMs - 1000, w.EndMs - 1000))]);
            words = await HistoryWords(engine, [.. speech, .. new float[second / 2]]);
            Score(clip.Id, "candidate: 0.5 s silence after only", clip.Truth, [.. words.Select(w => (w.Text, w.StartMs, w.EndMs))]);
            var lib = await LibraryWords(engine, speech);
            library[clip.Id] = lib;
            Score(clip.Id, "library recording (WordsOfFile)", clip.Truth, [.. lib.Select(w => (w.Text, (double)w.StartMs, (double)w.EndMs))]);
            Score(clip.Id, "example word times up to 2.44 (2nd hand annotation)", clip.Truth, [.. clip.Demo.Select(w => (w.Text, w.StartMs, w.EndMs))], record: false);
            if (example.TryGetValue(clip.File, out var current)) Score(clip.Id, "example word times now (web/src/demo/library.ts)", clip.Truth, current, record: false);
            Line($"- {clip.Id}: heard \"{string.Join(' ', bare.Select(w => w.Text))}\" (history, bare); library words: {string.Join(", ", lib.Select(w => $"{w.Text} {w.StartMs}-{w.EndMs}{(w.Uncertain ? " (uncertain)" : "")}"))}");
        }
        Line();
        Table(["Clip", "Condition", "Words right", "Start median", "Start p95", "End median", "End p95", "Edge median", "Edge p95", "Bad cuts"], rows);
        Line();
        Table(["Condition (both clips)", "Edges", "Median ms", "p95 ms", "Mean ms"], errors.Select(e => new[] { e.Key, e.Value.Count.ToString(Inv), F(AudioQualityMetrics.Percentile(e.Value, 50)), F(AudioQualityMetrics.Percentile(e.Value, 95)), F(e.Value.Average()) }));
        Line();
        Table(["Clip", "Word", "Hand ms", "Heard", "Pipeline ms", "Start error", "End error"], perWordRows);
        Json["wordTiming"] = json;
        return new(library, history);
    }

    // ---- 2. Sentences --------------------------------------------------------------------------------------------

    public sealed record Render(string Corpus, string Setting, string SentenceId, string Text, string[] Spoken, SentenceRender.Result Result, List<SentencePiece> Pieces, SentenceSettings Settings, string File, AudioQualityMetrics.JoinReport Joins);

    private static readonly (string Id, string Text)[] SentenceList =
    [
        ("S1", "the eagle has landed"),
        ("S2", "one small step for mankind"),
        ("S3", "the eagle has landed for mankind"),
        ("S4", "one giant eagle"),
        ("S5", "small step for the eagle"),
        ("S6", "here is one giant leap"),          // "is" was never recorded: a bleep
        ("S7", "the giant has landed here"),
        ("S8", "the eagle leaped"),                // "leaped" was never recorded: an approved splice
        ("S9", "one small step [laugh]"),          // a hand-named nonverbal piece
        ("S10", "for man [laugh]"),                // the laugh interrupts "man" 150 ms before it ends
        ("S11", "base here one small step"),
        ("W1", "small-step"),                      // every word of a recording as its own piece, in its order: the cuts re-joined
        ("W2", "eagle"),
    ];
    private static readonly (string Name, Func<SentenceSettings> Make)[] Settings =
    [
        ("Smooth", () => new SentenceSettings { Chaos = 0, Seed = 7 }),
        ("Natural", () => new SentenceSettings { Chaos = 45, Seed = 7 }),
        ("Chaotic", () => new SentenceSettings { Chaos = 100, Seed = 7 }),
        ("Original", () => new SentenceSettings { Chaos = 45, Seed = 7, Timing = "original" }),
        ("Natural, balance off", () => new SentenceSettings { Chaos = 45, Seed = 7, Balance = false }),
        ("Natural, cleanup", () => new SentenceSettings { Chaos = 45, Seed = 7, Cleanup = true }),
    ];
    public const double LaughStartMs = 300, LaughEndMs = 2300, InterruptMs = 150;

    private static List<Render> Sentences(List<Clip> clips, LibraryState state, float[] laugh, int laughRate, string outDir)
    {
        Heading("2. Sentence joins");
        var renderDir = Path.Combine(outDir, "renders"); Directory.CreateDirectory(renderDir);
        var sources = new Dictionary<string, ISliceSource>(StringComparer.Ordinal);
        foreach (var clip in clips) sources[clip.Id] = new PcmSliceSource(clip.Mono, clip.Rate, 1);
        sources["laugh"] = new PcmSliceSource(laugh, laughRate, 1);
        var named = new List<CorpusNamed> { new("laugh1", "laugh", "laugh", "audience", (long)LaughStartMs, (long)LaughEndMs, false) };
        var corpora = new (string Name, AudioQualityCorpus Corpus)[]
        {
            ("pipeline", new AudioQualityCorpus([.. clips.Select(c => new AudioQualityCorpus.Recording(c.Id, c.Voice, [.. state.Library[c.Id].Select(w => new AudioQualityCorpus.Word(w.Text, w.StartMs, w.EndMs, w.Uncertain))]))], named)),
            ("hand", new AudioQualityCorpus([.. clips.Select(c => new AudioQualityCorpus.Recording(c.Id, c.Voice, [.. c.Truth.Select(w => new AudioQualityCorpus.Word(w.Text, (long)w.StartMs, (long)w.EndMs, false))]))], named)),
        };
        var renders = new List<Render>(); var notes = new List<string>();
        foreach (var (corpusName, corpus) in corpora)
            foreach (var (settingName, make) in Settings)
                foreach (var (sid, text) in SentenceList)
                {
                    var settings = make();
                    var project = new SentenceProject { Id = sid, Text = "", Settings = settings };
                    string[]? meant = null; string shown = text;
                    if (sid.StartsWith('W'))
                    {
                        var clip = clips.First(c => c.Id == text);
                        var words = corpus.Recordings.First(r => r.Id == clip.Id).Words;
                        project.Text = string.Join(' ', words.Select(w => w.Text));
                        project.Pieces = [.. words.Select((w, i) => SentenceComposer.TakePiece(new CorpusTake(clip.Id, clip.Voice, i, 1, w.StartMs, w.EndMs, i + 1 < words.Count ? Math.Max(0, words[i + 1].StartMs - w.EndMs) : 250, w.Text, 1, false, 0), i, i + 1, w.Text))];
                        SentenceComposer.Layout(project);
                        meant = [.. SentenceComposer.Tokens(clip.Audible).Select(t => t.Key)];
                        shown = clip.Audible + " (word by word)";
                    }
                    else SentenceComposer.Rematch(project, text, corpus);
                    var tokens = SentenceComposer.Tokens(project.Text);
                    foreach (var missing in project.Pieces.Where(p => p.Kind == "missing").ToList())
                    {
                        var word = tokens[missing.TokenStart!.Value].Key;
                        var splices = SentenceComposer.Splices(word, corpus, TakeFilter.Of(settings));
                        if (sid == "S8" && splices.Count > 0)
                        {
                            SentenceComposer.UseSplice(project, missing, splices[0]);
                            if (settingName == "Natural") notes.Add($"{corpusName}: \"{word}\" approved splice {splices[0].Description}");
                        }
                        else SentenceComposer.MakeMissing(project, missing, "bleep");
                    }
                    if (sid == "S10")
                    {
                        var man = project.Pieces.FirstOrDefault(p => p.Kind == "word" && p.Text.Contains("man", StringComparison.OrdinalIgnoreCase));
                        var cut = project.Pieces.FirstOrDefault(p => p.Kind == "named");
                        if (man != null && cut != null) { cut.Manual = true; cut.OffsetMs = man.OffsetMs + SentenceComposer.Duration(man) - InterruptMs; SentenceComposer.Layout(project); }
                    }
                    SentenceRender.Result result;
                    try { result = SentenceRender.Render(project.Pieces, settings, id => sources[id]); }
                    catch (Exception ex) when (ex is InvalidDataException or SentenceRender.MissingSourceException) { notes.Add($"{corpusName} {settingName} {sid}: not rendered ({ex.Message})"); continue; }
                    var spoken = meant ?? project.Pieces.Where(p => p.Kind is "word" or "splice").OrderBy(p => p.OffsetMs).SelectMany(p => p.Kind == "splice" ? [WordIndex.Token(p.Text)] : SentenceComposer.Tokens(p.Text).Select(t => t.Key)).ToArray();
                    var file = Path.Combine(renderDir, $"{corpusName}-{settingName.Replace(", ", "-").Replace(' ', '-').ToLowerInvariant()}-{sid}.wav");
                    File.WriteAllBytes(file, result.Audio.ToWav());
                    var joins = AudioQualityMetrics.Joins(result, project.Pieces, settings);
                    renders.Add(new(corpusName, settingName, sid, shown, spoken, result, project.Pieces, settings, file, joins));
                }
        foreach (var note in notes.Distinct()) Line("- " + note);
        Line();
        Line("Click: the largest sample step within 1 ms of a join, over the 99th percentile of steps 5-30 ms on either side of it (under 1 = smoother than the audio around it; hard cuts of tones with no fade measure 4 to 11, see section 5). Loudness: voiced RMS of each recorded piece in the output (SliceLoudness, dBFS), step = difference between neighbouring pieces. Noise floor: 10th percentile of the 20 ms RMS of each piece. Timeline gap: where the next piece starts minus where this one ends (the setting); audible gap: last voiced 10 ms frame of one piece to the first voiced frame of the next.");
        Line();
        var rows = new List<string[]>(); var json = new List<object>();
        foreach (var group in renders.GroupBy(r => (r.Corpus, r.Setting)))
        {
            var joins = group.SelectMany(r => r.Joins.Clicks).ToList();
            var steps = group.SelectMany(r => r.Joins.LoudnessSteps).ToList();
            var floors = group.SelectMany(r => r.Joins.FloorSteps).ToList();
            var gaps = group.SelectMany(r => r.Joins.TimelineGapErrors).ToList();
            var audible = group.SelectMany(r => r.Joins.AudibleGaps).ToList();
            rows.Add([group.Key.Corpus, group.Key.Setting, joins.Count.ToString(Inv), F(AudioQualityMetrics.Percentile(joins, 50), "0.0"), F(joins.DefaultIfEmpty(double.NaN).Max(), "0.0"), joins.Count(x => x > 3).ToString(Inv),
                F(AudioQualityMetrics.Percentile(steps, 50), "0.0"), F(steps.DefaultIfEmpty(double.NaN).Max(), "0.0"), F(AudioQualityMetrics.Percentile(floors, 50), "0.0"), F(floors.DefaultIfEmpty(double.NaN).Max(), "0.0"),
                F(gaps.Select(Math.Abs).DefaultIfEmpty(double.NaN).Max(), "0.0"), F(AudioQualityMetrics.Percentile(audible, 50)), F(AudioQualityMetrics.Percentile(audible, 95)), group.Count(r => r.Joins.Limited).ToString(Inv)]);
        }
        // The same audible-gap measure on the untouched clips: neighbouring words spoken without a pause (under 300 ms apart).
        var natural = new List<double>();
        foreach (var clip in clips)
        {
            var voiced = AudioQualityMetrics.Voiced(clip.Speech16k);
            for (int k = 0; k + 1 < clip.Truth.Length; k++)
            {
                var a = clip.Truth[k]; var b = clip.Truth[k + 1];
                if (b.StartMs - a.EndMs >= 300) continue;
                int lastVoiced = -1, firstVoiced = -1;
                for (int f = Math.Min((int)(a.EndMs / 10), voiced.Length - 1); f >= (int)(a.StartMs / 10); f--) if (voiced[f]) { lastVoiced = f; break; }
                for (int f = (int)(b.StartMs / 10); f <= Math.Min((int)(b.EndMs / 10), voiced.Length - 1); f++) if (voiced[f]) { firstVoiced = f; break; }
                if (lastVoiced >= 0 && firstVoiced >= 0) natural.Add(Math.Max(0, (firstVoiced - lastVoiced) * 10.0 - 10));
            }
        }
        Line($"Reference: the same audible-gap measure between neighbouring words of the untouched clips (spoken without a pause): median {F(AudioQualityMetrics.Percentile(natural, 50))} ms, p95 {F(AudioQualityMetrics.Percentile(natural, 95))} ms over {natural.Count} pairs.");
        Line();
        Table(["Words", "Setting", "Joins", "Click median", "Click max", "Clicks > 3", "Loudness step median dB", "Loudness step max dB", "Floor step median dB", "Floor step max dB", "Timeline gap error max ms", "Audible gap median ms", "Audible gap p95 ms", "Renders limited"], rows);
        Line();
        // Bleeps against the speech around them, and the special sentences.
        var bleeps = renders.Where(r => r.Joins.BleepDb != null).Select(r => (r.Corpus, r.Setting, Bleep: r.Joins.BleepDb!.Value, Speech: r.Joins.SpeechDb)).ToList();
        Table(["Words", "Setting", "Bleep RMS dBFS", "Speech voiced RMS dBFS (median of pieces)", "Bleep minus speech dB"], bleeps.Select(b => new[] { b.Corpus, b.Setting, F(b.Bleep, "0.0"), F(b.Speech, "0.0"), F(b.Bleep - b.Speech, "0.0") }));
        Line();
        var special = renders.Where(r => r.Setting == "Natural" && r.SentenceId is "S8" or "S9" or "S10").ToList();
        Table(["Words", "Sentence", "Pieces", "Joins: click ratios", "Loudness steps dB", "Audible gaps ms"], special.Select(r => new[] { r.Corpus, $"{r.SentenceId} \"{r.Text}\"", string.Join(" / ", r.Pieces.OrderBy(p => p.OffsetMs).Select(p => p.Kind == "splice" ? $"splice({string.Join("+", p.Parts!.Select(x => x.Label))})" : p.Kind == "named" ? $"[{p.Text.Trim('[', ']')}]{(p.Manual ? $" at {p.OffsetMs:0}" : "")}" : p.Text)),
            string.Join(", ", r.Joins.Clicks.Select(x => F(x, "0.0"))), string.Join(", ", r.Joins.LoudnessSteps.Select(x => F(x, "0.0"))), string.Join(", ", r.Joins.AudibleGaps.Select(x => F(x))) }));
        foreach (var r in renders)
            json.Add(new { r.Corpus, r.Setting, r.SentenceId, r.Text, file = Path.GetFileName(r.File), durationMs = Math.Round(r.Result.Audio.DurationMs), pieces = r.Pieces.OrderBy(p => p.OffsetMs).Select(p => new { p.Kind, p.Text, offset = Math.Round(p.OffsetMs), duration = Math.Round(SentenceComposer.Duration(p)), source = p.Source == null ? null : $"{p.Source.RecordingId} {p.Source.StartMs}-{p.Source.EndMs}" }),
                clicks = r.Joins.Clicks.Select(x => Math.Round(x, 2)), loudnessSteps = r.Joins.LoudnessSteps.Select(x => Math.Round(x, 2)), floorSteps = r.Joins.FloorSteps.Select(x => Math.Round(x, 2)), audibleGaps = r.Joins.AudibleGaps.Select(x => Math.Round(x)), bleepDb = r.Joins.BleepDb, speechDb = r.Joins.SpeechDb, limited = r.Joins.Limited });
        Json["sentences"] = json;
        return renders;
    }

    // ---- 3. Intelligibility --------------------------------------------------------------------------------------

    private static async Task Intelligibility(WhisperEngine engine, List<Clip> clips, LibraryState state, List<Render> renders)
    {
        Heading("3. Intelligibility (Whisper word error rate)");
        Line("Each rendered sentence is transcribed again by the same model; WER = (substitutions + deletions + insertions) / words meant to be heard (the bleeped word and [laugh] excluded; a splice counts as its word; W1/W2 are the clips' own words re-joined, scored against the audible words). 300 ms of silence is added on both sides before transcribing. The originals are the baseline: the model on the untouched clips.");
        Line();
        var baseline = new List<string[]>();
        foreach (var clip in clips)
        {
            var heard = await Heard(engine, clip.Speech16k);
            var reference = SentenceComposer.Tokens(clip.Audible).Select(t => t.Key).ToArray();
            var full = SentenceComposer.Tokens(clip.Text).Select(t => t.Key).ToArray();
            baseline.Add([clip.File, string.Join(' ', heard), F(100 * AudioQualityMetrics.Wer(reference, heard), "0"), F(100 * AudioQualityMetrics.Wer(full, heard), "0")]);
        }
        Table(["Original clip", "Heard", "WER % (audible words)", "WER % (credits transcript)"], baseline);
        Line();
        var rows = new List<string[]>(); var json = new List<object>(); var bySetting = new Dictionary<(string, string), (double Errors, int Words)>(); var recall = new Dictionary<(string, string), double>();
        foreach (var r in renders)
        {
            var audio = r.Result.Audio; var mono = new float[audio.Frames];
            for (long f = 0; f < audio.Frames; f++) { float sum = 0; for (int c = 0; c < audio.Channels; c++) sum += audio.Samples[f * audio.Channels + c]; mono[f] = sum / audio.Channels; }
            var heard = await Heard(engine, MimicTranscriber.Resample(mono, audio.Rate, MimicTranscriber.SpeechRate));
            double wer = AudioQualityMetrics.Wer(r.Spoken, heard);
            var key = (r.Corpus, r.Setting); var sum0 = bySetting.GetValueOrDefault(key);
            bySetting[key] = (sum0.Errors + wer * r.Spoken.Length, sum0.Words + r.Spoken.Length);
            var found0 = recall.GetValueOrDefault(key); recall[key] = found0 + AudioQualityMetrics.Recall(r.Spoken, heard) * r.Spoken.Length;
            json.Add(new { r.Corpus, r.Setting, r.SentenceId, meant = string.Join(' ', r.Spoken), heard = string.Join(' ', heard), wer = Math.Round(wer, 3) });
            if (r.Setting == "Natural") rows.Add([r.Corpus, r.SentenceId, string.Join(' ', r.Spoken), string.Join(' ', heard), F(100 * wer)]);
        }
        Table(["Words", "Setting", "Words meant", "WER %", "Words heard %"], bySetting.Select(p => new[] { p.Key.Item1, p.Key.Item2, p.Value.Words.ToString(Inv), F(100 * p.Value.Errors / Math.Max(1, p.Value.Words), "0"), F(100 * recall[p.Key] / Math.Max(1, p.Value.Words), "0") }));
        Line();
        Table(["Words", "Sentence", "Meant", "Heard (Natural)", "WER %"], rows);
        Json["intelligibility"] = json;
    }

    /// <summary>What the model hears, with 300 ms of silence added on both sides (as a played sentence would be captured).</summary>
    private static async Task<string[]> Heard(WhisperEngine engine, float[] speech16k)
    {
        var pad = new float[MimicTranscriber.SpeechRate * 3 / 10];
        var spans = await engine.Transcribe([.. pad, .. speech16k, .. pad], default);
        return [.. SentenceComposer.Tokens(string.Join(' ', spans.Select(s => s.Text))).Where(t => !t.Nonverbal).Select(t => t.Key)];
    }
}
