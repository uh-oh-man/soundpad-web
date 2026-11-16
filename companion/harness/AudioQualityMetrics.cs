namespace SoundpadWeb.Testing;

// Metrics and the stand-in voice library of the audio-quality evaluation (AudioQualityEval.cs). Pure functions.
public static class AudioQualityMetrics
{
    /// <summary>Linear-interpolated percentile (0-100); NaN for no values.</summary>
    public static double Percentile(IEnumerable<double> values, double p)
    {
        var list = values.Where(double.IsFinite).OrderBy(v => v).ToList();
        if (list.Count == 0) return double.NaN;
        double at = p / 100 * (list.Count - 1); int i = (int)Math.Floor(at);
        return i + 1 < list.Count ? list[i] + (list[i + 1] - list[i]) * (at - i) : list[i];
    }

    /// <summary>Minimum-edit alignment of two word sequences: pairs (reference index, hypothesis index), -1 for a gap.</summary>
    public static List<(int Ref, int Hyp)> AlignWords(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        int n = reference.Count, m = hypothesis.Count;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1));
        var pairs = new List<(int, int)>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && d[i, j] == d[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1)) { pairs.Add((i - 1, j - 1)); i--; j--; }
            else if (i > 0 && d[i, j] == d[i - 1, j] + 1) { pairs.Add((i - 1, -1)); i--; }
            else { pairs.Add((-1, j - 1)); j--; }
        }
        pairs.Reverse();
        return pairs;
    }

    /// <summary>Word error rate: edit distance over the number of reference words.</summary>
    public static double Wer(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis) =>
        AlignWords(reference, hypothesis).Count(p => p.Ref < 0 || p.Hyp < 0 || reference[p.Ref] != hypothesis[p.Hyp]) / (double)Math.Max(1, reference.Count);

    /// <summary>The share of the reference words found in the hypothesis (as a multiset, in any order).</summary>
    public static double Recall(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        var left = hypothesis.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        int found = 0;
        foreach (var w in reference) if (left.TryGetValue(w, out int n) && n > 0) { found++; left[w] = n - 1; }
        return reference.Count == 0 ? double.NaN : (double)found / reference.Count;
    }

    /// <summary>
    /// How abrupt the audio is at a frame: the largest sample step within 1 ms, over the 99th percentile of the steps
    /// 5-30 ms either side. NaN when both sides are silent.
    /// </summary>
    public static double ClickRatio(float[] mono, int rate, long frame)
    {
        int near = Math.Max(1, rate / 1000), from = rate * 5 / 1000, to = rate * 30 / 1000;
        double peak = 0;
        for (long n = Math.Max(1, frame - near); n <= Math.Min(mono.Length - 1, frame + near); n++) peak = Math.Max(peak, Math.Abs(mono[n] - mono[n - 1]));
        var around = new List<double>();
        for (long n = frame - to; n <= frame + to; n++)
        {
            if (n < 1 || n >= mono.Length || Math.Abs(n - frame) < from) continue;
            around.Add(Math.Abs(mono[n] - mono[n - 1]));
        }
        double typical = Percentile(around, 99);
        if (!(typical > 1e-5)) return peak > 1e-3 ? 100 : double.NaN; // a step out of (or into) digital silence
        return peak / typical;
    }

    /// <summary>Per 10 ms frame (30 ms window) of 16 kHz audio: voiced (normalised autocorrelation of 0.6 or more at 70-400 Hz) and loud enough.</summary>
    public static bool[] Voiced(float[] speech16k, double withinDb = 30)
    {
        const int Hop = 160, Window = 480, MinLag = 40, MaxLag = 228;
        int frames = Math.Max(0, (speech16k.Length - Window - MaxLag) / Hop + 1);
        var energy = new double[frames]; var voiced = new bool[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0; int at = f * Hop;
            for (int i = 0; i < Window; i++) sum += speech16k[at + i] * speech16k[at + i];
            energy[f] = 10 * Math.Log10(sum / Window + 1e-12);
        }
        double loudest = frames == 0 ? 0 : energy.Max();
        for (int f = 0; f < frames; f++)
        {
            if (energy[f] < loudest - withinDb) continue;
            int at = f * Hop; double r0 = 0, best = 0;
            for (int i = 0; i < Window; i++) r0 += speech16k[at + i] * speech16k[at + i];
            for (int lag = MinLag; lag <= MaxLag; lag++)
            {
                double r = 0, rl = 0;
                for (int i = 0; i < Window; i++) { r += speech16k[at + i] * speech16k[at + i + lag]; rl += speech16k[at + i + lag] * speech16k[at + i + lag]; }
                best = Math.Max(best, r / Math.Sqrt(r0 * rl + 1e-12));
            }
            voiced[f] = best >= 0.6;
        }
        return voiced;
    }

    public static double RmsDb(float[] mono, long from, long to)
    {
        double sum = 0; long count = 0;
        for (long i = Math.Max(0, from); i < Math.Min(mono.Length, to); i++) { sum += mono[i] * mono[i]; count++; }
        return count == 0 ? double.NaN : 10 * Math.Log10(sum / count + 1e-12);
    }

    /// <summary>The 10th percentile of the 20 ms RMS levels in a range (its noise floor), dBFS.</summary>
    public static double FloorDb(float[] mono, int rate, long from, long to)
    {
        int window = rate / 50; var levels = new List<double>();
        for (long at = Math.Max(0, from); at + window <= Math.Min(mono.Length, to); at += window) levels.Add(RmsDb(mono, at, at + window));
        return Percentile(levels, 10);
    }

    public sealed record JoinReport(List<double> Clicks, List<double> LoudnessSteps, List<double> FloorSteps, List<double> TimelineGapErrors, List<double> AudibleGaps, double? BleepDb, double SpeechDb, bool Limited);

    /// <summary>Clicks at every edge of a rendered sentence, and the loudness, noise floor and gaps between its pieces.</summary>
    public static JoinReport Joins(SentenceRender.Result result, IReadOnlyList<SentencePiece> pieces, SentenceSettings settings)
    {
        var audio = result.Audio; int rate = audio.Rate, channels = audio.Channels;
        var mono = new float[audio.Frames];
        for (long f = 0; f < audio.Frames; f++) { float sum = 0; for (int c = 0; c < channels; c++) sum += audio.Samples[f * channels + c]; mono[f] = sum / channels; }
        // Where each item sounds in the output: its own length, cut where the next item starts (SliceAssembly's rule).
        var spans = new List<(int Index, long Start, long End, SliceItem Item)>();
        for (int i = 0; i < result.Items.Count; i++)
        {
            var item = result.Items[i]; long start = SliceRenderer.FrameOf(item.OffsetMs, rate);
            long length = item switch
            {
                SlicePiece p => (long)Math.Round((p.EndFrame - p.StartFrame) * (double)rate / p.Source.SampleRate),
                SlicePause p => SliceRenderer.FrameOf(p.DurationMs, rate),
                SliceBleep b => SliceRenderer.FrameOf(b.DurationMs, rate),
                _ => 0,
            };
            spans.Add((i, start, start + length, item));
        }
        spans = [.. spans.OrderBy(s => s.Start).ThenBy(s => s.Index)];
        for (int k = 0; k + 1 < spans.Count; k++) if (spans[k + 1].Start < spans[k].End) spans[k] = spans[k] with { End = spans[k + 1].Start };
        // Edges of sounding items (not pauses), inside the output.
        var edges = new SortedSet<long>();
        foreach (var s in spans.Where(s => s.Item is not SlicePause))
        {
            if (s.Start > 0) edges.Add(s.Start);
            if (s.End < mono.Length) edges.Add(s.End);
        }
        var clicks = new List<double>(); long? last = null;
        foreach (var e in edges)
        {
            if (last is long previous && e - previous < rate / 1000) continue;
            last = e;
            double ratio = ClickRatio(mono, rate, e);
            if (double.IsFinite(ratio)) clicks.Add(ratio);
        }
        // Per piece of the sentence (a splice's parts together).
        var owners = new List<(SentencePiece Piece, long Start, long End, bool Speech)>();
        foreach (var group in spans.GroupBy(s => result.Pieces[s.Index]))
        {
            var piece = pieces.FirstOrDefault(p => p.Id == group.Key);
            if (piece == null) continue;
            owners.Add((piece, group.Min(s => s.Start), group.Max(s => s.End), piece.Kind is "word" or "splice"));
        }
        owners = [.. owners.OrderBy(o => o.Start)];
        var source = new PcmSliceSource(mono, rate, 1);
        var loud = new Dictionary<SentencePiece, double?>(); var floor = new Dictionary<SentencePiece, double>();
        foreach (var o in owners.Where(o => o.Piece.Kind is "word" or "splice" or "named"))
        {
            loud[o.Piece] = o.End > o.Start ? SliceLoudness.EstimateDb(source, o.Start, o.End) : null;
            floor[o.Piece] = FloorDb(mono, rate, o.Start, o.End);
        }
        var loudnessSteps = new List<double>(); var floorSteps = new List<double>();
        var recorded = owners.Where(o => loud.ContainsKey(o.Piece)).ToList();
        for (int k = 0; k + 1 < recorded.Count; k++)
        {
            if (loud[recorded[k].Piece] is double a && loud[recorded[k + 1].Piece] is double b) loudnessSteps.Add(Math.Abs(a - b));
            double fa = floor[recorded[k].Piece], fb = floor[recorded[k + 1].Piece];
            if (double.IsFinite(fa) && double.IsFinite(fb)) floorSteps.Add(Math.Abs(fa - fb));
        }
        // The layout's gaps against the setting (pieces nobody placed by hand).
        var gapErrors = new List<double>();
        var laid = pieces.Where(p => p.TokenStart is int).OrderBy(p => p.TokenStart).ThenBy(p => p.OffsetMs).ToList();
        for (int k = 0; k + 1 < laid.Count; k++)
        {
            var a = laid[k]; var b = laid[k + 1];
            if (b.Manual || a.Manual) continue;
            double expected = a.Kind is "pause" or "missing" ? 0 : settings.Timing == "original" ? Math.Clamp(a.GapAfterMs, 0, 800) : SentenceComposer.FluentGapMs;
            gapErrors.Add(b.OffsetMs - (a.OffsetMs + SentenceComposer.Duration(a)) - expected);
        }
        // Audible gaps between neighbouring recorded words: last voiced frame of one to the first voiced frame of the next.
        var speech16k = MimicTranscriber.Resample(mono, rate, MimicTranscriber.SpeechRate);
        var voiced = Voiced(speech16k);
        double FrameMs(int f) => f * 10 + 15;
        var audibleGaps = new List<double>();
        var words = owners.Where(o => o.Speech).ToList();
        for (int k = 0; k + 1 < words.Count; k++)
        {
            var a = words[k]; var b = words[k + 1];
            if (owners.Any(o => !o.Speech && o.Start >= a.End - 1 && o.End <= b.Start + 1 && o.End > o.Start)) continue; // a bleep or pause between them
            int aFrom = (int)(a.Start * 100 / rate), aTo = (int)(a.End * 100 / rate), bFrom = (int)(b.Start * 100 / rate), bTo = (int)(b.End * 100 / rate);
            int lastVoiced = -1, firstVoiced = -1;
            for (int f = Math.Min(aTo, voiced.Length - 1); f >= Math.Max(0, aFrom); f--) if (voiced[f]) { lastVoiced = f; break; }
            for (int f = Math.Max(0, bFrom); f <= Math.Min(bTo, voiced.Length - 1); f++) if (voiced[f]) { firstVoiced = f; break; }
            if (lastVoiced >= 0 && firstVoiced >= 0) audibleGaps.Add(Math.Max(0, FrameMs(firstVoiced) - FrameMs(lastVoiced) - 10));
        }
        double? bleep = null;
        var bleepSpan = spans.FirstOrDefault(s => s.Item is SliceBleep);
        if (bleepSpan.Item != null) bleep = RmsDb(mono, bleepSpan.Start, bleepSpan.End);
        double speechDb = Percentile(owners.Where(o => o.Speech && loud.TryGetValue(o.Piece, out var v) && v != null).Select(o => loud[o.Piece]!.Value), 50);
        bool limited = mono.Any(x => Math.Abs(x) > 0.99f);
        return new(clicks, loudnessSteps, floorSteps, gapErrors, audibleGaps, bleep, speechDb, limited);
    }
}

/// <summary>The evaluation's voice library: the words of each recording (pipeline or hand times) and the named pieces, as BridgeSentences.LibraryCorpus presents them.</summary>
public sealed class AudioQualityCorpus(IReadOnlyList<AudioQualityCorpus.Recording> recordings, IReadOnlyList<CorpusNamed> named) : ISentenceCorpus
{
    public sealed record Word(string Text, long StartMs, long EndMs, bool Uncertain)
    {
        public string Token => WordIndex.Token(Text);
    }
    public sealed record Recording(string Id, string Voice, IReadOnlyList<Word> Words);
    public IReadOnlyList<Recording> Recordings => recordings;

    public IReadOnlyList<CorpusTake> Find(string[] tokens, TakeFilter filter, int max)
    {
        var result = new List<CorpusTake>();
        if (tokens.Length == 0) return result;
        foreach (var r in recordings.Where(r => filter.Allows(r.Voice, false)))
        {
            var words = r.Words;
            for (int position = 0; position + tokens.Length <= words.Count && result.Count < max; position++)
            {
                bool match = true;
                for (int i = 0; i < tokens.Length && match; i++) match = words[position + i].Token == tokens[i];
                if (!match) continue;
                int lastIndex = position + tokens.Length - 1;
                long start = words[position].StartMs, end = words[lastIndex].EndMs;
                long gap = lastIndex + 1 < words.Count ? Math.Max(0, words[lastIndex + 1].StartMs - end) : 250;
                int uncertain = 0; for (int i = position; i <= lastIndex; i++) if (words[i].Uncertain) uncertain++;
                double quality = 1 - 0.5 * uncertain / tokens.Length;
                result.Add(new(r.Id, r.Voice, position, tokens.Length, start, end, gap, string.Join(' ', words.Skip(position).Take(tokens.Length).Select(w => w.Text)), quality, false, 0));
            }
        }
        return result;
    }

    public IReadOnlyList<CorpusNamed> Named(string name, TakeFilter filter) => [.. named.Where(n => n.Name == name && filter.Allows(n.VoiceId, n.Altered))];

    public IReadOnlyDictionary<string, int> Vocabulary(TakeFilter filter)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in recordings.Where(r => filter.Allows(r.Voice, false)))
            foreach (var w in r.Words) if (w.Token.Length > 0) counts[w.Token] = counts.GetValueOrDefault(w.Token) + 1;
        return counts;
    }
}
