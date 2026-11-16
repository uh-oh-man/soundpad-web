namespace SoundpadWeb.Testing;

// Sections 4 and 5 of the audio-quality evaluation (AudioQualityEval.cs): speaker grouping on the clips' phrases,
// and synthetic tones, which make any click at a join obvious (the click detector's own check).
public static partial class AudioQualityEval
{
    // ---- 4. Speaker grouping -------------------------------------------------------------------------------------

    private static void Grouping(List<Clip> clips, Dictionary<string, List<AlignedWord>> history, float[] laugh, int laughRate)
    {
        Heading("4. Speaker grouping (SpeakerGrouping.cs, shipped settings)");
        Line("Both clips are Neil Armstrong, recorded over different radio links. Phrases are cut at the hand word edges (the four phrases of \"small step\" and the two of \"eagle\"), and also as the history would cut them (Whisper's phrases with the pipeline's word times). Other \"speakers\" are synthetic: the same phrases played 18 % faster (pitch and formants up, like a higher voice) and 14 % slower (down). Noise: pink noise mixed at 20 and 10 dB under the speech.");
        int rate = SpeakerAnalysis.Rate;
        float[] Cut(Clip c, double from, double to) => c.Speech16k[(int)(from * rate / 1000)..Math.Min(c.Speech16k.Length, (int)(to * rate / 1000))];
        var small = clips.First(c => c.Id == "small-step"); var eagle = clips.First(c => c.Id == "eagle");
        var hand = new List<(string Name, float[] Audio)>
        {
            ("small-step: that's one small step", Cut(small, 95, 1560)), ("small-step: for man", Cut(small, 1665, 2190)),
            ("small-step: one giant leap", Cut(small, 3815, 5420)), ("small-step: for mankind", Cut(small, 5550, 6540)),
            ("eagle: tranquility base here", Cut(eagle, 510, 1740)), ("eagle: the eagle has landed", Cut(eagle, 2045, 3060)),
        };
        var whisper = new List<(string Name, float[] Audio)>();
        foreach (var clip in clips)
            foreach (var phrase in history[clip.Id].GroupBy(w => w.Phrase))
                whisper.Add(($"{clip.Id} (history phrase): {string.Join(' ', phrase.Select(w => w.Text))}", Cut(clip, phrase.Min(w => w.StartMs), phrase.Max(w => w.EndMs))));
        var grouper = new SpeakerGrouper();
        var rows = new List<string[]>();
        var features = hand.Select(p => (p.Name, F: SpeakerAnalysis.Analyze(p.Audio))).ToList();
        foreach (var (name, audio) in hand.Concat(whisper))
        {
            var f = SpeakerAnalysis.Analyze(audio);
            rows.Add([name, F(audio.Length / (double)rate, "0.00"), f == null ? $"under {SpeakerAnalysis.MinVoicedSeconds:0.00}: none" : F(f.VoicedSeconds, "0.00"), f == null ? "" : F(f.PitchSemitones - 12 * Math.Log2(100), "0.0"), f == null ? "" : F(f.PitchRange, "0.0")]);
        }
        Table(["Phrase", "Length s", "Voiced s (pitch found)", "Pitch (semitones over 100 Hz)", "Pitch range st"], rows);
        Line();
        // Distances between the hand phrases (in the grouper's units: Join under 1.8, New over 2.3).
        var valid = features.Where(x => x.F != null).ToList();
        Table(["Distance", .. valid.Select((_, i) => "P" + (i + 1))], valid.Select((a, i) => new[] { $"P{i + 1} {a.Name}" }.Concat(valid.Select(b => F(grouper.Distance(a.F!, b.F!.Mean, b.F.PitchSemitones), "0.00"))).ToArray()));
        Line();
        // Sessions: the order a session would hear them, then noisy and mixed sessions.
        var pink = WordTimingSignal.PinkNoise(rate * 10, 5);
        float[] Noisy(float[] audio, double snr, int seed) => WordTimingSignal.Mix(audio, WordTimingSignal.Loop(pink, audio.Length, seed * 7919 % pink.Length), snr, rate);
        string Run(IReadOnlyList<(string Truth, float[] Audio)> sequence, out (double Purity, double Coverage, int Groups) score)
        {
            var g = new SpeakerGrouper(); var results = new List<(string, int?)>();
            foreach (var (truth, audio) in sequence) results.Add((truth, g.Assign(SpeakerAnalysis.Analyze(audio))));
            score = SpeakerGrouper.Score(results);
            return string.Join(" ", results.Select(r => r.Item2?.ToString(Inv) ?? "-"));
        }
        var sessions = new List<string[]>(); var json = new List<object>();
        void Session(string name, IReadOnlyList<(string Truth, float[] Audio)> sequence)
        {
            var order = Run(sequence, out var s);
            var pairs = sequence.Zip(order.Split(' ')).ToList();
            int armstrongGroups = pairs.Where(p => p.First.Truth == "armstrong" && p.Second != "-").Select(p => p.Second).Distinct().Count();
            var bySpeaker = string.Join("; ", pairs.GroupBy(p => p.First.Truth).Select(g => $"{g.Key}: {g.Count(p => p.Second != "-")}/{g.Count()} in {string.Join(",", g.Where(p => p.Second != "-").Select(p => p.Second).Distinct().DefaultIfEmpty("none"))}"));
            sessions.Add([name, sequence.Count.ToString(Inv), F(100 * s.Purity, "0"), F(100 * s.Coverage, "0"), s.Groups.ToString(Inv), armstrongGroups.ToString(Inv), bySpeaker, order]);
            json.Add(new { name, phrases = sequence.Count, purity = s.Purity, coverage = s.Coverage, groups = s.Groups, armstrongGroups, order });
        }
        var armstrong = hand.Select(p => ("armstrong", p.Audio)).ToList();
        Session("Armstrong, hand phrases in order", armstrong);
        Session("Armstrong, history phrases in order", [.. whisper.Select(p => ("armstrong", p.Audio))]);
        var noisy = armstrong.Concat(armstrong.Select((p, i) => (p.Item1, Noisy(p.Audio, 20, i)))).Concat(armstrong.Select((p, i) => (p.Item1, Noisy(p.Audio, 10, i + 10)))).ToList();
        Session("Armstrong, clean then +20 dB then +10 dB noise", noisy);
        var random = new Random(11);
        var shuffled = noisy.OrderBy(_ => random.Next()).ToList();
        Session("Armstrong, the same 18 shuffled", shuffled);
        var others = new List<(string, float[])>();
        foreach (var (truth, audio) in noisy)
        {
            others.Add((truth, audio));
            others.Add(("higher (x1.18)", AudioEffects.Speed(audio, 1, 1.18)));
            others.Add(("lower (x0.86)", AudioEffects.Speed(audio, 1, 0.86)));
        }
        random = new Random(13);
        Session("Armstrong + 2 synthetic voices, 54 shuffled", [.. others.OrderBy(_ => random.Next())]);
        // The longer history phrases (a whole clip each) are the only ones voiced enough to start a group: the same tests on them.
        var whole = whisper.Select(p => ("armstrong", p.Audio)).ToList();
        var wholeNoisy = whole.Concat(whole.Select((p, i) => (p.Item1, Noisy(p.Audio, 20, i + 20)))).Concat(whole.Select((p, i) => (p.Item1, Noisy(p.Audio, 10, i + 30)))).ToList();
        Session("Armstrong, history phrases clean, +20 dB, +10 dB", wholeNoisy);
        var wholeOthers = new List<(string, float[])>();
        foreach (var (truth, audio) in wholeNoisy)
        {
            wholeOthers.Add((truth, audio));
            wholeOthers.Add(("higher (x1.18)", AudioEffects.Speed(audio, 1, 1.18)));
            wholeOthers.Add(("lower (x0.86)", AudioEffects.Speed(audio, 1, 0.86)));
        }
        foreach (int seed in new[] { 17, 19, 23 })
        {
            random = new Random(seed);
            Session($"History phrases + 2 synthetic voices, 18 shuffled (seed {seed})", [.. wholeOthers.OrderBy(_ => random.Next())]);
        }
        var laugh16 = MimicTranscriber.Resample(laugh, laughRate, rate);
        Session("Armstrong in order, then the audience laugh", [.. armstrong, ("laugh", laugh16[(int)(LaughStartMs * rate / 1000)..(int)(LaughEndMs * rate / 1000)])]);
        Session("History phrases, then the audience laugh", [.. whole, ("laugh", laugh16[(int)(LaughStartMs * rate / 1000)..(int)(LaughEndMs * rate / 1000)])]);
        Table(["Session", "Phrases", "Purity %", "Coverage %", "Groups", "Groups holding Armstrong", "Assigned by speaker (groups)", "Assignments in order (- = Unassigned)"], sessions);
        Json["grouping"] = json;
    }

    // ---- 5. Synthetic tones: clicks and loudness at joins ----------------------------------------------------------

    private static void Tones(string outDir)
    {
        Heading("5. Synthetic tones: the click detector and the joins");
        Line("Two synthetic recordings at 44.1 kHz, each six 300 ms tone \"words\" (220-770 Hz) with 120 ms of silence between them; one at 0.5 peak, the other 20 dB quieter. Their word edges are placed 40 ms inside each tone, so every cut lands mid-waveform at an arbitrary phase: the worst case for clicks. Control: the same joins assembled with no fades and no crossfade.");
        const int rate = 44100;
        var names = new[] { "ta", "tb", "tc", "td", "te", "tf" }; var freqs = new[] { 220.0, 330, 440, 550, 660, 770 };
        (float[] Audio, List<AudioQualityCorpus.Word> Words) Make(double amplitude, string prefix, bool reverse)
        {
            var audio = new List<float>(); var words = new List<AudioQualityCorpus.Word>();
            for (int w = 0; w < 6; w++)
            {
                audio.AddRange(new float[rate * 120 / 1000]);
                double f = freqs[reverse ? 5 - w : w]; long start = audio.Count * 1000L / rate;
                for (int i = 0; i < rate * 300 / 1000; i++) audio.Add((float)(amplitude * Math.Sin(2 * Math.PI * f * i / rate + 0.7 * w)));
                words.Add(new(prefix + names[w][1..], start + 40, start + 260, false));
            }
            audio.AddRange(new float[rate * 120 / 1000]);
            return (audio.ToArray(), words);
        }
        var (loudAudio, loudWords) = Make(0.5, "a", false); var (quietAudio, quietWords) = Make(0.05, "b", true);
        var sources = new Dictionary<string, ISliceSource> { ["loud"] = new PcmSliceSource(loudAudio, rate, 1), ["quiet"] = new PcmSliceSource(quietAudio, rate, 1) };
        var corpus = new AudioQualityCorpus([new("loud", "v1", loudWords), new("quiet", "v2", quietWords)], []);
        var texts = new[] { "aa ab ac ad", "aa bb ac bd ae", "ba ab bc ad", "af ae ad ac ab aa", "bb ab", "aa bz ab" };
        var rows = new List<string[]>();
        foreach (var (name, settings) in new[] { ("Natural", new SentenceSettings { Seed = 3 }), ("Natural, balance off", new SentenceSettings { Seed = 3, Balance = false }), ("Original", new SentenceSettings { Seed = 3, Timing = "original" }), ("Natural, cleanup", new SentenceSettings { Seed = 3, Cleanup = true }) })
        {
            var clicks = new List<double>(); var steps = new List<double>();
            foreach (var text in texts)
            {
                var project = new SentenceProject { Id = "t", Text = "", Settings = settings };
                SentenceComposer.Rematch(project, text, corpus);
                foreach (var m in project.Pieces.Where(p => p.Kind == "missing").ToList()) SentenceComposer.MakeMissing(project, m, "bleep");
                var result = SentenceRender.Render(project.Pieces, settings, id => sources[id]);
                var report = AudioQualityMetrics.Joins(result, project.Pieces, settings);
                clicks.AddRange(report.Clicks); steps.AddRange(report.LoudnessSteps);
            }
            // An interruption: the second word placed by hand 100 ms before the first one ends (a cut with the 4 ms crossfade).
            {
                var project = new SentenceProject { Id = "t", Text = "", Settings = settings };
                SentenceComposer.Rematch(project, "aa bb", corpus);
                var second = project.Pieces.OrderBy(p => p.OffsetMs).Last(); var first = project.Pieces.OrderBy(p => p.OffsetMs).First();
                second.Manual = true; second.OffsetMs = first.OffsetMs + SentenceComposer.Duration(first) - 100; SentenceComposer.Layout(project);
                var report = AudioQualityMetrics.Joins(SentenceRender.Render(project.Pieces, settings, id => sources[id]), project.Pieces, settings);
                clicks.AddRange(report.Clicks); steps.AddRange(report.LoudnessSteps);
            }
            rows.Add([name, clicks.Count.ToString(Inv), F(AudioQualityMetrics.Percentile(clicks, 50), "0.0"), F(clicks.DefaultIfEmpty(double.NaN).Max(), "0.0"), clicks.Count(x => x > 3).ToString(Inv), F(AudioQualityMetrics.Percentile(steps, 50), "0.0"), F(steps.DefaultIfEmpty(double.NaN).Max(), "0.0")]);
        }
        // Control: the same kind of joins with no fade and no crossfade, words touching and overlapping.
        {
            var items = new List<SliceItem>(); double at = 0;
            foreach (var (w, i) in loudWords.Select((w, i) => (w, i)))
            {
                var src = i % 2 == 0 ? sources["loud"] : sources["quiet"]; var word = i % 2 == 0 ? w : quietWords[i];
                long s = SliceRenderer.FrameOf(word.StartMs, rate), e = SliceRenderer.FrameOf(word.EndMs, rate);
                items.Add(new SlicePiece(src, s, e, at) { Balance = true });
                at += (e - s) * 1000.0 / rate - (i == 3 ? 50 : 0);
            }
            var output = SliceAssembly.RenderSamples(items, new SliceAssemblyOptions { CrossfadeMs = 0, Balance = true });
            var mono = output.Audio.Samples;
            var clicks = output.Placements.Skip(1).Select(p => AudioQualityMetrics.ClickRatio(mono, rate, p.StartFrame)).Where(double.IsFinite).ToList();
            rows.Add(["Control: no fades, no crossfade", clicks.Count.ToString(Inv), F(AudioQualityMetrics.Percentile(clicks, 50), "0.0"), F(clicks.DefaultIfEmpty(double.NaN).Max(), "0.0"), clicks.Count(x => x > 3).ToString(Inv), "", ""]);
        }
        Table(["Tone sentences", "Edges", "Click median", "Click max", "Clicks > 3", "Loudness step median dB", "Loudness step max dB"], rows);
    }
}
