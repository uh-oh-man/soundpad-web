namespace SoundpadWeb;

// Word timing for Mimic transcripts (2.39). The method is "anchor-valley" from the word-timing evaluation
// (harness WordTiming*.cs): Whisper's DTW time is a point INSIDE each word, so between the anchors of two
// neighbouring words there is exactly one boundary, taken from the audio (a pause if there is one, otherwise the
// quietest plausible frame). No timestamp Whisper returns is used as a cut by itself.

/// <summary>One recognised token. Times in centiseconds relative to the audio handed in; DtwCs is -1 when there is none.</summary>
public sealed record TranscribedToken(string Text, long StartCs, long EndCs, long DtwCs, float Probability);

/// <summary>A word with its cut points in milliseconds from the start of the recognised audio.</summary>
/// <param name="StretchMs">Longest stretch between DTW points on a side without a pause; 0 when both edges are at a pause.</param>
/// <param name="Phrase">Index of the phrase (Whisper segment) the word came from, in this call.</param>
public sealed record AlignedWord(string Text, double StartMs, double EndMs, float Probability, int StretchMs, bool PauseBefore, bool PauseAfter, int Phrase);

/// <summary>Speech-band energy per 5 ms frame of a 16 kHz signal, in dB, lightly smoothed.</summary>
public sealed class SpeechEnergy
{
    public const double HopMs = 5;
    private const int Rate = 16000, Hop = 80, Window = 320, FftSize = 512; // 5 ms hop, 20 ms Hann window, zero-padded
    /// <summary>How far above the background sound must be to count as speech (tuned in the evaluation).</summary>
    public const double NoiseMarginDb = 1.5;
    public required double[] Db { get; init; }
    /// <summary>5th and 99th percentile of <see cref="Db"/>: the background and the loud speech level.</summary>
    public double FloorDb { get; init; }
    public double PeakDb { get; init; }
    public double DurationMs => (Db.Length - 1) * HopMs;
    public int Frame(double ms) => Math.Clamp((int)Math.Round(ms / HopMs), 0, Db.Length - 1);
    public double At(double ms) => Db[Frame(ms)];
    public double Peak(double fromMs, double toMs)
    {
        double peak = double.NegativeInfinity;
        for (int f = Frame(fromMs), last = Frame(toMs); f <= last; f++) peak = Math.Max(peak, Db[f]);
        return peak;
    }
    /// <summary>Energy under this is "no speech here" near a word whose loudest frame is <paramref name="peakDb"/>.</summary>
    public double SilenceThreshold(double peakDb, bool wide = false) => Math.Max(FloorDb + (wide ? WideMarginDb : NoiseMarginDb), peakDb - 35);
    /// <summary>The margin for the second, looser pause search: wider when loud speech stands well above the background (up to 7 dB).</summary>
    public double WideMarginDb => Math.Clamp(NoiseMarginDb + 0.5 * (PeakDb - FloorDb - 22), NoiseMarginDb, 7);

    /// <summary>
    /// True when the last <paramref name="ms"/> are a pause relative to this audio: under the background plus 6 dB
    /// (or 35 dB under the loud speech), so a steady game or music bed does not hide every pause (2.39).
    /// </summary>
    public bool QuietAtEnd(double ms) => QuietBetween(DurationMs - ms, DurationMs);
    public bool QuietBetween(double fromMs, double toMs)
    {
        // Never closer than 10 dB to the loud speech: a steady sound with no quieter moment has no pause in it.
        double threshold = Math.Min(Math.Max(FloorDb + 6, PeakDb - 35), PeakDb - 10);
        for (int f = Frame(fromMs), last = Frame(toMs); f <= last; f++) if (Db[f] >= threshold) return false;
        return true;
    }
    /// <summary>The middle of the quietest <paramref name="lengthMs"/> stretch between two times: where to cut when no pause comes.</summary>
    public double QuietestBetween(double fromMs, double toMs, double lengthMs)
    {
        int width = Math.Max(1, (int)(lengthMs / HopMs)), first = Frame(fromMs), last = Frame(toMs) - width;
        if (last < first) return toMs;
        double sum = 0; for (int f = first; f < first + width; f++) sum += Db[f];
        double best = sum; int bestAt = first;
        for (int f = first + 1; f <= last; f++)
        {
            sum += Db[f + width - 1] - Db[f - 1];
            if (sum < best) { best = sum; bestAt = f; }
        }
        return (bestAt + width / 2.0) * HopMs;
    }

    /// <summary>Speech-band (150 Hz to 6 kHz) energy per 5 ms frame. Frame f is centred on sample f * 80.</summary>
    public static SpeechEnergy Analyze(ReadOnlySpan<float> samples16k)
    {
        int frames = Math.Max(1, samples16k.Length / Hop + 1);
        var raw = new double[frames]; var re = new double[FftSize]; var im = new double[FftSize];
        var hann = Hann.Value;
        int low = 150 * FftSize / Rate, high = 6000 * FftSize / Rate;
        for (int f = 0; f < frames; f++)
        {
            Array.Clear(re); Array.Clear(im);
            int start = f * Hop - Window / 2;
            for (int i = 0; i < Window; i++) { int s = start + i; if (s >= 0 && s < samples16k.Length) re[i] = samples16k[s] * hann[i]; }
            Fft(re, im);
            double energy = 0;
            for (int k = low; k <= high; k++) energy += re[k] * re[k] + im[k] * im[k];
            raw[f] = 10 * Math.Log10(energy / Window + 1e-12);
        }
        var smooth = new double[frames];
        for (int f = 0; f < frames; f++) smooth[f] = (raw[Math.Max(0, f - 1)] + raw[f] + raw[Math.Min(frames - 1, f + 1)]) / 3;
        var sorted = (double[])smooth.Clone(); Array.Sort(sorted);
        return new SpeechEnergy { Db = smooth, FloorDb = sorted[(int)(sorted.Length * 0.05)], PeakDb = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] };
    }
    private static readonly Lazy<double[]> Hann = new(() => Enumerable.Range(0, Window).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1))).ToArray());

    /// <summary>In-place radix-2 FFT.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            double angle = -2 * Math.PI / size, wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += size)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < size / 2; k++)
                {
                    int a = i + k, b = a + size / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}

/// <summary>Whisper output → words with cut points (anchor-valley with the wide pause search, as the evaluation recommends).</summary>
public static class WordAligner
{
    // Recommended settings (report section 5/6): pause = 120 ms quiet; cost = dB above quiet + 400 d² + 6 per 100 ms
    // from the token boundary (capped at 300 ms); the DTW time sits 62 % into its token; wide search 250 ms, 60 ms kept.
    private const double PauseMs = 120, PhiDb = 400, AnchorAt = 0.62, TokenDb = 6, WidePauseMs = 250, WideGuardMs = 60;
    /// <summary>Padding added at cut time (never stored): 20 ms before and 10 ms after.</summary>
    public const double PadBeforeMs = 20, PadAfterMs = 10;

    private sealed class Word
    {
        public string Text = "";
        public double AStart, AEnd, AnchorFirst = double.NaN, AnchorLast = double.NaN, FirstWeight = 5, LastWeight = 5, MinP = 1;
        public double StartMs, EndMs, GapBeforeMs, GapAfterMs;
        public bool PauseBefore, PauseAfter;
        public int Phrase;
    }

    /// <summary>Rough spoken length of a written token: letters, with digits counting as a whole number word each.</summary>
    private static double Weight(string word) => 1.5 + word.Count(char.IsLetter) + 3.5 * word.Count(char.IsDigit);

    /// <summary>
    /// Words for the phrases Whisper returned for one call. Phrases without tokens (an engine that gives none) are
    /// spread over their span by length and count as uncertain (stretch 800 ms).
    /// </summary>
    public static List<AlignedWord> Align(IReadOnlyList<TranscribedSpan> spans, SpeechEnergy energy)
    {
        var words = new List<Word>(); var spread = new List<AlignedWord>();
        for (int s = 0; s < spans.Count; s++)
        {
            var span = spans[s];
            if (span.Tokens is not { Length: > 0 } tokens) { spread.AddRange(Spread(span, s)); continue; }
            Word? current = null;
            foreach (var token in tokens)
            {
                if (token.Text.StartsWith("[_", StringComparison.Ordinal)) continue; // [_BEG_], [_TT_150], ...
                double anchor = token.DtwCs >= 0 ? token.DtwCs * 10 : double.NaN;
                if (!token.Text.Any(char.IsLetterOrDigit))
                {
                    if (current != null) current.Text += token.Text; // punctuation joins the word's text, never its time
                    continue;
                }
                if (current == null || token.Text.StartsWith(' '))
                {
                    if (current != null) words.Add(current);
                    current = new Word { Phrase = s, AStart = token.StartCs * 10, AnchorFirst = anchor, FirstWeight = Weight(token.Text) };
                }
                current.LastWeight = Weight(token.Text);
                current.Text += token.Text; current.AEnd = token.EndCs * 10; current.AnchorLast = anchor;
                current.MinP = Math.Min(current.MinP, token.Probability);
            }
            if (current != null) words.Add(current);
        }
        foreach (var w in words) w.Text = w.Text.Trim();
        words.RemoveAll(w => w.Text.Length == 0);
        AnchorValley(words, energy);
        var result = words.Select(w => new AlignedWord(w.Text, w.StartMs, w.EndMs, (float)w.MinP, (int)Math.Round(Math.Max(w.PauseBefore ? 0 : w.GapBeforeMs, w.PauseAfter ? 0 : w.GapAfterMs)), w.PauseBefore, w.PauseAfter, w.Phrase)).ToList();
        result.AddRange(spread);
        return result.OrderBy(w => w.StartMs).ToList();
    }

    private static IEnumerable<AlignedWord> Spread(TranscribedSpan span, int phrase)
    {
        var pieces = span.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Any(char.IsLetterOrDigit)).ToArray();
        double total = pieces.Sum(Weight), done = 0, from = span.Start.TotalMilliseconds, length = span.End.TotalMilliseconds - from;
        foreach (var piece in pieces)
        {
            double start = from + length * done / total; done += Weight(piece);
            yield return new AlignedWord(piece, start, from + length * done / total, span.Probability, 800, false, false, phrase);
        }
    }

    private static void AnchorValley(List<Word> words, SpeechEnergy f)
    {
        int n = words.Count;
        if (n == 0) return;
        double audioMs = f.DurationMs;
        var firstAnchor = new double[n]; var lastAnchor = new double[n];
        for (int i = 0; i < n; i++)
        {
            double fallback = (words[i].AStart + words[i].AEnd) / 2;
            // Anchors are kept inside the audio and in order, whatever Whisper reported (with a shortened audio
            // context its DTW times can land past the end of the audio).
            firstAnchor[i] = Math.Clamp(double.IsNaN(words[i].AnchorFirst) ? fallback : words[i].AnchorFirst, 0, audioMs);
            lastAnchor[i] = Math.Max(firstAnchor[i], Math.Clamp(double.IsNaN(words[i].AnchorLast) ? fallback : words[i].AnchorLast, 0, audioMs));
            if (i > 0) { firstAnchor[i] = Math.Max(firstAnchor[i], lastAnchor[i - 1]); lastAnchor[i] = Math.Max(lastAnchor[i], firstAnchor[i]); }
        }
        // Start of the first word: the end of the nearest pause within 1 s before its anchor.
        {
            double right = firstAnchor[0], left = Math.Max(0, right - 1000);
            var pause = PauseBetween(f, left, right, f.Peak(right - 100, right + 150));
            if (pause.Found) { words[0].StartMs = pause.End; words[0].PauseBefore = true; }
            else { words[0].StartMs = SnapPoint(f, Math.Clamp(words[0].AStart, Math.Max(0, right - 800), right), 60, 0, right); words[0].GapBeforeMs = 800; }
        }
        for (int i = 1; i < n; i++)
        {
            double left = lastAnchor[i - 1], right = firstAnchor[i];
            if (right - left < 2 * SpeechEnergy.HopMs) { words[i - 1].EndMs = words[i].StartMs = (left + right) / 2; words[i - 1].GapAfterMs = words[i].GapBeforeMs = Math.Max(right - left, 1); continue; }
            double threshold = f.SilenceThreshold(f.Peak(left - 150, right + 150));
            var pause = PauseBetween(f, left, right, f.Peak(left - 150, right + 150));
            if (pause.Found)
            {
                words[i - 1].EndMs = pause.Start; words[i].StartMs = pause.End; words[i - 1].PauseAfter = words[i].PauseBefore = true;
                continue;
            }
            double remaining = (1 - AnchorAt) * words[i - 1].LastWeight, lead = AnchorAt * words[i].FirstWeight, centre = left + (right - left) * remaining / (remaining + lead);
            double tokenBoundary = (words[i - 1].AEnd + words[i].AStart) / 2, bestScore = double.PositiveInfinity, boundary = centre;
            for (int k = f.Frame(left) + 1; k < f.Frame(right); k++)
            {
                double t = k * SpeechEnergy.HopMs, d = (t - centre) / (right - left);
                double score = Math.Max(0, f.Db[k] - threshold) + PhiDb * d * d + TokenDb * Math.Min(3, Math.Abs(t - tokenBoundary) / 100);
                if (score < bestScore) { bestScore = score; boundary = t; }
            }
            words[i - 1].EndMs = words[i].StartMs = boundary;
            words[i - 1].GapAfterMs = words[i].GapBeforeMs = right - left;
        }
        // End of the last word: where the next pause begins, within 1.2 s after its anchor.
        {
            double left = lastAnchor[n - 1], right = Math.Min(audioMs, left + 1200);
            var pause = PauseBetween(f, left, right, f.Peak(left - 150, left + 100));
            if (pause.Found) { words[n - 1].EndMs = pause.Start; words[n - 1].PauseAfter = true; }
            else { words[n - 1].EndMs = SnapPoint(f, Math.Clamp(words[n - 1].AEnd, left, Math.Min(audioMs, left + 800)), 60, left, audioMs); words[n - 1].GapAfterMs = 800; }
        }
        foreach (var w in words) { w.StartMs = Math.Clamp(w.StartMs, 0, audioMs); w.EndMs = Math.Clamp(Math.Max(w.EndMs, w.StartMs), 0, audioMs); }
    }

    /// <summary>The quietest frame within ±window of a time, with a mild pull toward where it started.</summary>
    private static double SnapPoint(SpeechEnergy f, double ms, double windowMs, double lowMs, double highMs, double slopeDb = 6)
    {
        int centre = f.Frame(ms), reach = Math.Max(1, (int)(windowMs / SpeechEnergy.HopMs));
        int low = Math.Max(Math.Max(0, centre - reach), f.Frame(lowMs)), high = Math.Min(Math.Min(f.Db.Length - 1, centre + reach), f.Frame(highMs));
        if (low > high) return ms;
        int best = Math.Clamp(centre, low, high); double bestScore = double.PositiveInfinity;
        for (int k = low; k <= high; k++)
        {
            double score = f.Db[k] + slopeDb * Math.Abs(k - centre) / reach;
            if (score < bestScore) { bestScore = score; best = k; }
        }
        return best * SpeechEnergy.HopMs;
    }

    /// <summary>The silence between two times, if any: from where the first pause begins to where the last one ends.</summary>
    private static (bool Found, double Start, double End) PauseBetween(SpeechEnergy f, double fromMs, double toMs, double peakDb)
    {
        var pauses = Pauses(f, fromMs, toMs, f.SilenceThreshold(peakDb), PauseMs);
        // The wide search, only for recordings clean enough to afford a wider margin (loud speech > 22 dB over the background).
        if (f.WideMarginDb > SpeechEnergy.NoiseMarginDb)
            foreach (var wide in Pauses(f, fromMs, toMs, f.SilenceThreshold(peakDb, true), WidePauseMs))
                if (wide.Length > 2 * WideGuardMs) pauses.Add((wide.Start + WideGuardMs, wide.End - WideGuardMs, wide.Length - 2 * WideGuardMs));
        return pauses.Count == 0 ? (false, 0, 0) : (true, pauses.Min(p => p.Start), pauses.Max(p => p.End));
    }

    /// <summary>Runs of frames below the threshold lasting at least <paramref name="minimumMs"/>, between two times.</summary>
    private static List<(double Start, double End, double Length)> Pauses(SpeechEnergy f, double fromMs, double toMs, double threshold, double minimumMs)
    {
        var runs = new List<(double, double, double)>(); int first = f.Frame(fromMs), last = f.Frame(toMs), run = -1;
        for (int k = first; k <= last + 1; k++)
        {
            bool quiet = k <= last && f.Db[k] < threshold;
            if (quiet && run < 0) run = k;
            else if (!quiet && run >= 0)
            {
                // A frame's 20 ms window reaches 10 ms past its centre, so a quiet frame is already ~10 ms clear of the sound.
                double start = run * SpeechEnergy.HopMs, end = (k - 1) * SpeechEnergy.HopMs;
                if (end - start >= minimumMs) runs.Add((start, end, end - start));
                run = -1;
            }
        }
        return runs;
    }

    /// <summary>
    /// The honest status of a word (report section 5): "uncertain" when the lowest token probability is under 0.5,
    /// the no-pause stretch is 300 ms or more, the word touches a cut that was not at a pause, or the chunk's loud
    /// speech is under 12 dB over its background; otherwise "done". "pending" is never stored: it is audio not yet recognised.
    /// </summary>
    public static string Status(float probability, int stretchMs, bool touchesForcedCut, double signalToNoiseDb) =>
        probability < 0.5f || stretchMs >= 300 || touchesForcedCut || signalToNoiseDb < 12 ? WordStatus.Uncertain : WordStatus.Done;
}

public static class WordStatus
{
    public const string Done = "done", Uncertain = "uncertain", Pending = "pending";
}
