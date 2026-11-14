using System.Diagnostics;
using System.Globalization;
namespace SoundpadWeb.Testing;

// --word-pipeline-eval <fixtures> [--limit N] [--conditions clean,pink+10] [--mode gaming|balanced|fast] [--model file]
//
// Runs the companion's OWN word-timing pipeline (2.39: WhisperEngine with DTW + token timestamps and the sized
// window, then WordAligner's anchor-valley) over the word-timing evaluation's fixture sentences and scores it with
// the evaluation's ground truth and bad-cut rule (WordTimingEval.Score/Summarize), padded 20 ms before / 10 ms after.
// It reads the model and the fixture WAVs only: no audio device, no Soundpad, nothing written.
public static class WordPipelineEval
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<int> Run(string[] args)
    {
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var model = Option("--model", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb", "models", "ggml-base.en.bin"));
        if (!File.Exists(model)) { Console.Error.WriteLine("Speech model not found: " + model); return 2; }
        int limit = int.Parse(Option("--limit", "96"), Inv);
        var conditions = Option("--conditions", "clean,pink+10").Split(',');
        var mode = SubtitleModes.Parse(Option("--mode", "gaming")) ?? SubtitleMode.Gaming;
        var all = WordTimingEval.LoadFixtures(args[1]);
        // A fixed pseudo-random sample, so voices, speeds and sentence types are all represented (fixtures are sorted
        // voice-sentence-speed, so taking every third would take one speed only).
        static int Hash(string text) { unchecked { int h = 17; foreach (char c in text) h = h * 31 + c; return h & 0x7fffffff; } }
        var fixtures = all.OrderBy(f => Hash(f.Id)).Take(limit).ToList();
        Console.WriteLine("speeds: " + string.Join(", ", fixtures.GroupBy(f => f.RateName).Select(g => $"{g.Key} {g.Count()}")) + "; voices: " + string.Join(", ", fixtures.GroupBy(f => f.Voice).Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine($"{fixtures.Count} of {all.Count} fixtures, {fixtures.Sum(f => f.Words.Length)} reference words, mode {SubtitleModes.Name(mode)}, model {Path.GetFileName(model)} (read only)");
        using var engine = new WhisperEngine(model, null, null);
        engine.UseMode(mode);
        // Warm-up: loads the model.
        await engine.Transcribe(new float[MimicTranscriber.SpeechRate * 2], default);
        foreach (var condition in conditions)
        {
            var records = new List<WordRecord>();
            var statuses = new List<(string Status, bool Problem)>();
            double audioMs = 0; var wall = Stopwatch.StartNew(); var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            foreach (var fixture in fixtures)
            {
                var speech = WordTimingEval.Speech16k(fixture, condition);
                audioMs += speech.Length * 1000.0 / MimicTranscriber.SpeechRate;
                var energy = SpeechEnergy.Analyze(speech);
                var spans = await engine.Transcribe(speech, default);
                var aligned = WordAligner.Align(spans, energy);
                double snr = energy.PeakDb - energy.FloorDb;
                var timed = aligned.Select(w => new TimedWord { Text = w.Text, Norm = WordTimingMethods.Normalize(w.Text), StartMs = w.StartMs, EndMs = w.EndMs, AStart = w.StartMs, AEnd = w.EndMs, MinP = w.Probability, MeanP = w.Probability, PauseBefore = w.PauseBefore, PauseAfter = w.PauseAfter, GapBeforeMs = w.StretchMs, GapAfterMs = w.StretchMs })
                    .Where(w => w.Norm.Length > 0).ToList();
                var scored = WordTimingEval.Score(new EvalItem(fixture, condition), "production", timed, WordTimingSignal.Analyze(speech), fixture.Words);
                records.AddRange(scored);
                // The stored status against what actually went wrong (a wrong word or a bad padded cut).
                foreach (var record in scored.Where(r => r.HasHyp))
                {
                    var word = aligned.FirstOrDefault(a => Math.Abs(a.StartMs - record.HypStart) < 0.01 && Math.Abs(a.EndMs - record.HypEnd) < 0.01);
                    if (word == null) continue;
                    bool problem = !record.Hit || record.Bad(WordTimingEval.PadBefore, WordTimingEval.PadAfter);
                    statuses.Add((WordAligner.Status(word.Probability, word.StretchMs, false, snr), problem));
                }
            }
            double cpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds, minutes = audioMs / 60000;
            var s = WordTimingEval.Summarize(records, WordTimingEval.PadBefore, WordTimingEval.PadAfter);
            Console.WriteLine($"[{condition}] words {s.Words}, recall {s.Recall}%, precision {s.Precision}%");
            Console.WriteLine($"[{condition}] start error median/p90 {s.StartMedian}/{s.StartP90} ms, end error median/p90 {s.EndMedian}/{s.EndP90} ms, bad cuts padded 20/10: {s.BadPct}% (clip {s.ClipPct}%, neighbour {s.NeighbourPct}%), usable {s.UsablePct}% of spoken");
            foreach (var speed in records.GroupBy(r => r.Item.Fixture.RateName).OrderBy(g => g.Key))
                Console.WriteLine($"[{condition}] {speed.Key} speech: bad cuts {WordTimingEval.Summarize(speed, WordTimingEval.PadBefore, WordTimingEval.PadAfter).BadPct}%");
            foreach (var group in statuses.GroupBy(x => x.Status).OrderBy(g => g.Key))
                Console.WriteLine($"[{condition}] status {group.Key}: {100.0 * group.Count() / Math.Max(1, statuses.Count):0.0}% of words, problem rate {100.0 * group.Count(x => x.Problem) / group.Count():0.0}%");
            Console.WriteLine($"[{condition}] cost: {wall.Elapsed.TotalSeconds / minutes:0.0} s wall and {cpuSeconds / minutes:0.0} s CPU per audio minute ({minutes:0.00} audio minutes)");
        }
        return 0;
    }
}
