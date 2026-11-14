using System.Globalization;
namespace SoundpadWeb.Testing;

// --speaker-eval <fixtures> [--phrases N] [--seed S]
//
// Measures the 2.41 speaker grouping prototype (SpeakerGrouping.cs) on the word-timing evaluation's synthetic fixture
// sentences (Windows voices David and Zira, rendered to WAV by test tooling; nothing played out loud). Two more
// synthetic "speakers" are made by shifting each voice's pitch and formants together (tape speed: David x1.18 and
// Zira x0.86). Each condition interleaves N phrases of the speakers in a seeded random order and groups them online,
// as a session would, then reports purity (assigned phrases that belong to their group's majority speaker), coverage
// (phrases assigned at all) and how many groups were made. Conditions: clean; pink noise 10 dB under the speech;
// a music bed at the speech level; a second speaker 6 dB under the first (overlap). Reads the fixture WAVs only.
public static class SpeakerGroupingEval
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string[] args)
    {
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        int phrases = int.Parse(Option("--phrases", "80"), Inv), seed = int.Parse(Option("--seed", "7"), Inv);
        var files = Directory.GetFiles(args[1], "*-normal.wav").Concat(Directory.GetFiles(args[1], "*-slow.wav")).OrderBy(f => f, StringComparer.Ordinal).ToList();
        float[] Load(string path) { var (s, r) = WordTimingSignal.ReadMono(path); return MimicTranscriber.Resample(s, r, SpeakerAnalysis.Rate); }
        var david = files.Where(f => Path.GetFileName(f).StartsWith("david", StringComparison.Ordinal)).Select(Load).ToList();
        var zira = files.Where(f => Path.GetFileName(f).StartsWith("zira", StringComparison.Ordinal)).Select(Load).ToList();
        if (david.Count < 10 || zira.Count < 10) { Console.Error.WriteLine("Need the david-*/zira-* fixture WAVs."); return 2; }
        var speakers = new Dictionary<string, List<float[]>>
        {
            ["david"] = david,
            ["zira"] = zira,
            ["david+18%"] = david.Select(s => AudioEffects.Speed(s, 1, 1.18)).ToList(),
            ["zira-14%"] = zira.Select(s => AudioEffects.Speed(s, 1, 0.86)).ToList(),
        };
        // Within-speaker spread of each feature on clean audio: what the distance's scales should be.
        Console.WriteLine("Within-speaker spread of the cepstral means (clean, all phrases):");
        foreach (var (name, list) in speakers)
        {
            var features = list.Select(s => SpeakerAnalysis.Analyze(s)).OfType<SpeakerFeatures>().ToList();
            var sd = Enumerable.Range(0, 12).Select(i => Std(features.Select(f => f.Mean[i]))).ToArray();
            Console.WriteLine($"  {name,-10} n={features.Count,3} pitch {features.Average(f => f.PitchSemitones):0.0} st (sd {Std(features.Select(f => f.PitchSemitones)):0.0}) c1..c12 sd " + string.Join(" ", sd.Select(v => v.ToString("0.00", Inv))));
        }
        var pink = WordTimingSignal.PinkNoise(SpeakerAnalysis.Rate * 20, 11);
        var music = WordTimingSignal.MusicBed(SpeakerAnalysis.Rate * 20, SpeakerAnalysis.Rate);
        // Every condition's phrases are analysed once; each setting then groups the same sequence.
        var settings = new List<(string Name, SpeakerGroupingOptions Options)> { ("shipped", new SpeakerGroupingOptions()) };
        if (args.Contains("--sweep"))
            foreach (var join in new[] { 1.2, 1.5, 1.8, 2.2 })
                foreach (var gap in new[] { 0.5, 1.0 })
                    foreach (var pitch in new[] { 1.0, 1.5, 2.5 })
                        settings.Add(($"join {join} new {join + gap} pitch {pitch}", new SpeakerGroupingOptions { Join = join, New = join + gap, PitchScale = pitch }));
        var sets = new[] { new[] { "david", "zira" }, new[] { "david", "zira", "david+18%", "zira-14%" } };
        var conditions = new[] { "clean", "pink+10", "music0", "overlap-6" };
        var sequences = new Dictionary<(int, string), List<(string Who, SpeakerFeatures? Features)>>();
        for (int s = 0; s < sets.Length; s++)
            foreach (var condition in conditions)
            {
                var set = sets[s]; var random = new Random(seed); var list = new List<(string, SpeakerFeatures?)>();
                for (int n = 0; n < phrases; n++)
                {
                    var who = set[random.Next(set.Length)];
                    var all = speakers[who]; var phrase = all[random.Next(all.Count)];
                    float[] audio = condition switch
                    {
                        "pink+10" => WordTimingSignal.Mix(phrase, WordTimingSignal.Loop(pink, phrase.Length, random.Next(pink.Length)), 10, SpeakerAnalysis.Rate),
                        "music0" => WordTimingSignal.Mix(phrase, WordTimingSignal.Loop(music, phrase.Length, random.Next(music.Length)), 0, SpeakerAnalysis.Rate),
                        "overlap-6" => Overlap(phrase, speakers[set.Where(x => x != who).ElementAt(random.Next(set.Length - 1))], random),
                        _ => phrase,
                    };
                    list.Add((who, SpeakerAnalysis.Analyze(audio)));
                }
                sequences[(s, condition)] = list;
            }
        foreach (var (name, options) in settings)
        {
            Console.WriteLine();
            Console.WriteLine($"Setting: {name}");
            Console.WriteLine("| Speakers | Condition | Phrases | Purity % | Coverage % | Groups |");
            Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: |");
            for (int s = 0; s < sets.Length; s++)
                foreach (var condition in conditions)
                {
                    var grouper = new SpeakerGrouper(options);
                    var results = sequences[(s, condition)].Select(p => (p.Who, grouper.Assign(p.Features))).ToList();
                    var (purity, coverage, groups) = SpeakerGrouper.Score(results);
                    Console.WriteLine($"| {sets[s].Length} | {condition} | {phrases} | {purity * 100:0.0} | {coverage * 100:0.0} | {groups} |");
                }
        }
        return 0;
    }

    private static float[] Overlap(float[] primary, List<float[]> others, Random random)
    {
        var other = others[random.Next(others.Count)];
        return WordTimingSignal.Mix(primary, WordTimingSignal.Loop(other, primary.Length, random.Next(other.Length)), 6, SpeakerAnalysis.Rate);
    }

    private static double Std(IEnumerable<double> values)
    {
        var list = values.ToList(); if (list.Count < 2) return 0;
        double mean = list.Average();
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
    }
}
