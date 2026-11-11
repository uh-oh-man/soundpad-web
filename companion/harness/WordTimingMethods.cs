using System.Text;
namespace SoundpadWeb.Testing;

// Word-timing evaluation: the candidate ways of turning Whisper output into per-word cut points.
//
//   A  token      whisper.cpp token timestamps (WithTokenTimestamps), sub-word tokens grouped into words
//   B  dtw        the DTW time of each token (UseDtwTimeStamps). Measured here: it is a point INSIDE the word,
//                 not its start or end, so it is reported raw (as start, as end) and used as an anchor (AV)
//   C  oneword    "-ml 1 -sow": one word per segment
//   P  phrase     today's phrase spans, words spread by length (prop) or placed by a signal DP (dp)
//   +snap/+trim   any of the above with each boundary moved to the quietest frame nearby / shrunk to the sound
//   AV anchor-valley: one boundary between each pair of neighbouring DTW anchors, at the pause or energy valley
//   AV + wide pauses: the same, plus a looser second look for pauses on clean recordings (the recommended form)

/// <summary>A word with cut points in milliseconds from the start of the audio that was transcribed.</summary>
public sealed class TimedWord
{
    public string Text = "", Norm = "";
    public double StartMs, EndMs;
    /// <summary>Lowest and geometric-mean token probability in the word, and its phrase's probability.</summary>
    public double MinP = 1, MeanP = 1, SegP = 1;
    /// <summary>Token-timestamp span (method A) and DTW anchors of the first/last token and of the token after the word (NaN when absent).</summary>
    public double AStart, AEnd, AnchorFirst = double.NaN, AnchorLast = double.NaN, NextAnchor = double.NaN;
    public double SegStart, SegEnd;
    /// <summary>Rough spoken length (letters) of the word's first and last token: where between two anchors the boundary should fall.</summary>
    public double FirstWeight = 5, LastWeight = 5;
    public int Segment;
    /// <summary>True when a real pause (not just a valley) was found at that side of the word.</summary>
    public bool PauseBefore, PauseAfter;
    /// <summary>
    /// For a side without a pause: how long the stretch between the two anchors was (the boundary is somewhere in
    /// it), and how far above silence the chosen frame is. 0 on a side that ends at a pause.
    /// </summary>
    public double GapBeforeMs, GapAfterMs, LevelBeforeDb, LevelAfterDb;
    public TimedWord Clone() => (TimedWord)MemberwiseClone();
}

/// <summary>
/// Settings of the anchor-valley method. A quiet stretch of at least <paramref name="PauseMs"/> between two anchors
/// is a pause (shorter ones are stop consonants and only count as valleys). Without a pause the boundary is the
/// frame with the lowest cost, in dB: its level above silence, plus <paramref name="PhiDb"/> times the squared
/// distance (1 = the whole stretch) from where the boundary is expected, plus <paramref name="TokenDb"/> per 100 ms
/// of distance from the token-timestamp boundary (capped at 300 ms). The expected place: the DTW time sits about
/// <paramref name="AnchorAt"/> of the way through its token (measured: 0.62), so the boundary is expected after the
/// remaining share of the left token and before that share of the right one, each sized by its letters
/// (<paramref name="ByLength"/>; off = both tokens taken as equally long). <paramref name="Trim"/> additionally
/// shrinks each word to the sound inside it; measured, that helped nothing on clean audio and cut quiet
/// consonants off in noise, so it is off. <paramref name="WidePauseMs"/> (0 = off) adds a second, looser look for
/// pauses on recordings clean enough to afford it: a stretch at least that long under the margin widened by
/// signal-to-noise (<see cref="WordFeatures.MarginBySignalToNoiseDb"/>) also counts as a pause, and each word next
/// to it keeps <paramref name="WideGuardMs"/> of it, because a quiet consonant can hide under a wide margin.
/// </summary>
public sealed record AnchorOptions(double PauseMs, double PhiDb, double AnchorAt, bool ByLength, double TokenDb, bool Trim, double WidePauseMs = 0, double WideGuardMs = 40)
{
    /// <summary>The settings the report recommends (chosen by the tune stage on half the sentences, checked on the other half).</summary>
    public static readonly AnchorOptions Recommended = new(120, 400, 0.62, true, 6, false);
}

public static class WordTimingMethods
{
    /// <summary>The companion's own filter for phrases it shows (MimicTranscript.cs).</summary>
    public static bool Shown(RawSegment s) => s.Prob >= .45f && s.Text.Trim() is { Length: > 0 } text && !text.StartsWith('[') && !text.StartsWith('(');

    // ---- text ----------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Spoken = new()
    {
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
        ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12", ["thirteen"] = "13", ["fourteen"] = "14", ["fifteen"] = "15", ["sixteen"] = "16", ["seventeen"] = "17",
        ["eighteen"] = "18", ["nineteen"] = "19", ["twenty"] = "20", ["thirty"] = "30", ["forty"] = "40", ["fifty"] = "50", ["sixty"] = "60", ["seventy"] = "70",
        ["eighty"] = "80", ["ninety"] = "90", ["hundred"] = "100", ["mr"] = "mister", ["ok"] = "okay", ["favourite"] = "favorite",
    };
    private static bool Tens(string n) => n.Length == 2 && n[1] == '0' && n[0] is >= '2' and <= '9';
    private static bool Unit(string n) => n.Length == 1 && n[0] is >= '1' and <= '9';

    /// <summary>Lower case, letters and digits only, number words as digits ("twenty-five" and "25" compare equal).</summary>
    public static string Normalize(string text)
    {
        var parts = new List<string>();
        foreach (var piece in text.ToLowerInvariant().Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = new string(piece.Where(char.IsLetterOrDigit).ToArray());
            if (clean.Length > 0) parts.Add(Spoken.GetValueOrDefault(clean, clean));
        }
        if (parts.Count == 2 && Tens(parts[0]) && Unit(parts[1])) return parts[0][0] + parts[1];
        return string.Concat(parts);
    }

    /// <summary>Joins "twenty" + "five" (or "one" + "hundred") written as two words into one number word.</summary>
    public static List<TimedWord> MergeNumbers(List<TimedWord> words)
    {
        var merged = new List<TimedWord>();
        foreach (var w in words)
        {
            var last = merged.Count > 0 ? merged[^1] : null;
            if (last != null && (Tens(last.Norm) && Unit(w.Norm) || last.Norm == "1" && w.Norm == "100"))
            {
                last.Norm = last.Norm == "1" ? "100" : last.Norm[0] + w.Norm; last.Text += " " + w.Text.Trim();
                last.EndMs = w.EndMs; last.AEnd = w.AEnd; last.AnchorLast = w.AnchorLast; last.LastWeight = w.LastWeight; last.NextAnchor = w.NextAnchor; last.PauseAfter = w.PauseAfter; last.GapAfterMs = w.GapAfterMs; last.LevelAfterDb = w.LevelAfterDb;
                last.MinP = Math.Min(last.MinP, w.MinP); last.MeanP = Math.Sqrt(last.MeanP * w.MeanP);
            }
            else merged.Add(w);
        }
        return merged;
    }

    // ---- from Whisper output ---------------------------------------------------------------------------------

    /// <summary>
    /// Groups sub-word tokens into words (a token that starts with a space starts a word; punctuation belongs to the
    /// word before it but never stretches its time). Fills the token span (A) and the DTW anchors; Start/End = A.
    /// </summary>
    public static List<TimedWord> FromTokens(RawResult raw)
    {
        var words = new List<TimedWord>();
        for (int s = 0; s < raw.Segments.Length; s++)
        {
            var segment = raw.Segments[s];
            if (!Shown(segment)) continue;
            TimedWord? current = null; double after = double.NaN, logSum = 0; int count = 0;
            void Close(double nextAnchor)
            {
                if (current == null) return;
                current.NextAnchor = double.IsNaN(after) ? nextAnchor : after;
                current.MeanP = Math.Exp(logSum / Math.Max(1, count)); current.Norm = Normalize(current.Text);
                current.StartMs = current.AStart; current.EndMs = current.AEnd;
                if (current.Norm.Length > 0) words.Add(current);
                current = null; after = double.NaN; logSum = 0; count = 0;
            }
            foreach (var token in segment.Tokens)
            {
                if (token.Text.StartsWith("[_", StringComparison.Ordinal)) continue; // [_BEG_], [_TT_150], ...
                double anchor = token.DtwCs >= 0 ? token.DtwCs * 10 : double.NaN;
                if (!token.Text.Any(char.IsLetterOrDigit))
                {
                    if (current != null) { current.Text += token.Text; if (double.IsNaN(after)) after = anchor; }
                    continue;
                }
                if (current == null || token.Text.StartsWith(' '))
                {
                    Close(anchor);
                    current = new TimedWord { Segment = s, SegStart = segment.StartMs, SegEnd = segment.EndMs, SegP = segment.Prob, AStart = token.StartCs * 10, AnchorFirst = anchor, FirstWeight = Weight(token.Text) };
                }
                current.LastWeight = Weight(token.Text);
                current.Text += token.Text; current.AEnd = token.EndCs * 10; current.AnchorLast = anchor; after = double.NaN;
                current.MinP = Math.Min(current.MinP, token.P); logSum += Math.Log(Math.Max(1e-6, token.P)); count++;
            }
            Close(double.NaN);
        }
        foreach (var w in words) w.Text = w.Text.Trim();
        return MergeNumbers(words);
    }

    /// <summary>Method C: each segment of a "-ml 1 -sow" pass is one word (split evenly by letters if it ever holds two).</summary>
    public static List<TimedWord> FromOneWord(RawResult raw)
    {
        var words = new List<TimedWord>();
        for (int s = 0; s < raw.Segments.Length; s++)
        {
            var segment = raw.Segments[s]; var text = segment.Text.Trim();
            if (text.Length == 0 || text.StartsWith('[') || text.StartsWith('(')) continue;
            var pieces = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Any(char.IsLetterOrDigit)).ToArray();
            var spoken = segment.Tokens.Where(t => !t.Text.StartsWith("[_", StringComparison.Ordinal) && t.Text.Any(char.IsLetterOrDigit)).ToArray();
            double total = pieces.Sum(Weight), done = 0;
            foreach (var piece in pieces)
            {
                double from = segment.StartMs + (segment.EndMs - segment.StartMs) * done / total; done += Weight(piece);
                double to = segment.StartMs + (segment.EndMs - segment.StartMs) * done / total;
                words.Add(new TimedWord { Text = piece, Norm = Normalize(piece), StartMs = from, EndMs = to, AStart = from, AEnd = to, Segment = s, SegStart = segment.StartMs, SegEnd = segment.EndMs,
                    SegP = segment.Prob, MinP = spoken.Length > 0 ? spoken.Min(t => t.P) : segment.MinProb, MeanP = segment.Prob });
            }
        }
        return MergeNumbers(words.Where(w => w.Norm.Length > 0).ToList());
    }

    /// <summary>Rough spoken length of a written word: letters, with digits counting as a whole number word each.</summary>
    private static double Weight(string word) => 1.5 + word.Count(char.IsLetter) + 3.5 * word.Count(char.IsDigit);

    /// <summary>Phrase spans only (today's output): words spread across the phrase in proportion to their length.</summary>
    public static List<TimedWord> PhraseProportional(RawResult raw)
    {
        var words = new List<TimedWord>();
        for (int s = 0; s < raw.Segments.Length; s++)
        {
            var segment = raw.Segments[s];
            if (!Shown(segment)) continue;
            var pieces = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Any(char.IsLetterOrDigit)).ToArray();
            double total = pieces.Sum(Weight), done = 0;
            foreach (var piece in pieces)
            {
                double from = segment.StartMs + (segment.EndMs - segment.StartMs) * done / total; done += Weight(piece);
                double to = segment.StartMs + (segment.EndMs - segment.StartMs) * done / total;
                words.Add(new TimedWord { Text = piece, Norm = Normalize(piece), StartMs = from, EndMs = to, AStart = from, AEnd = to, Segment = s, SegStart = segment.StartMs, SegEnd = segment.EndMs, SegP = segment.Prob, MinP = segment.MinProb, MeanP = segment.Prob });
            }
        }
        return MergeNumbers(words.Where(w => w.Norm.Length > 0).ToList());
    }

    // ---- signal refinements ----------------------------------------------------------------------------------

    /// <summary>
    /// Method D without any token timing: inside each phrase span, choose the word boundaries by dynamic
    /// programming so that word lengths stay near their expected share of the voiced time while each boundary
    /// sits as deep in an energy valley as possible; then shrink every word to the sound it contains.
    /// </summary>
    public static List<TimedWord> PhraseSignal(RawResult raw, WordFeatures f)
    {
        var all = PhraseProportional(raw); var result = new List<TimedWord>();
        foreach (var group in all.GroupBy(w => w.Segment))
        {
            var words = group.Select(w => w.Clone()).ToList();
            int first = f.Frame(words[0].SegStart), last = f.Frame(Math.Min(words[0].SegEnd, (f.Db.Length - 1) * WordFeatures.HopMs));
            double peak = f.Peak(words[0].SegStart, words[0].SegEnd), threshold = f.SilenceThreshold(peak);
            while (first < last && f.Db[first] < threshold) first++;
            while (last > first && f.Db[last] < threshold) last--;
            int n = words.Count, m = (last - first) / 2; // candidate boundaries every 10 ms
            if (m < n) { result.AddRange(words); continue; }
            var voiced = new double[m + 1];
            for (int k = 1; k <= m; k++) voiced[k] = voiced[k - 1] + (f.Db[first + 2 * k - 1] >= threshold ? 1 : 0) + (f.Db[first + 2 * k] >= threshold ? 1 : 0);
            double total = Math.Max(1, voiced[m]), weightSum = words.Sum(w => Weight(w.Text));
            var best = new double[n + 1, m + 1]; var from = new int[n + 1, m + 1];
            for (int i = 0; i <= n; i++) for (int k = 0; k <= m; k++) best[i, k] = double.PositiveInfinity;
            best[0, 0] = 0;
            for (int i = 1; i <= n; i++)
            {
                double expected = Weight(words[i - 1].Text) / weightSum;
                for (int k = i; k <= m; k++)
                {
                    if (i == n && k != m) continue;
                    double level = Math.Clamp((f.Db[first + 2 * k] - threshold) / Math.Max(1, peak - threshold), 0, 1), valley = i == n ? 0 : 3 * level * level;
                    for (int j = i - 1; j < k; j++)
                    {
                        if (double.IsInfinity(best[i - 1, j])) continue;
                        double share = (voiced[k] - voiced[j]) / total, error = (share - expected) / expected, cost = best[i - 1, j] + error * error + valley;
                        if (cost < best[i, k]) { best[i, k] = cost; from[i, k] = j; }
                    }
                }
            }
            for (int i = n, k = m; i >= 1; i--)
            {
                int j = from[i, k];
                words[i - 1].StartMs = (first + 2 * j) * WordFeatures.HopMs; words[i - 1].EndMs = (first + 2 * k) * WordFeatures.HopMs;
                k = j;
            }
            foreach (var w in words) Trim(w, f);
            result.AddRange(words);
        }
        return result;
    }

    /// <summary>The quietest frame within +/-window of a time, with a mild pull (slope dB at the window edge) toward where it started.</summary>
    public static double SnapPoint(WordFeatures f, double ms, double windowMs, double lowMs, double highMs, double slopeDb = 6)
    {
        int centre = f.Frame(ms), reach = Math.Max(1, (int)(windowMs / WordFeatures.HopMs));
        int low = Math.Max(Math.Max(0, centre - reach), f.Frame(lowMs)), high = Math.Min(Math.Min(f.Db.Length - 1, centre + reach), f.Frame(highMs));
        if (low > high) return ms;
        int best = Math.Clamp(centre, low, high); double bestScore = double.PositiveInfinity;
        for (int k = low; k <= high; k++)
        {
            double score = f.Db[k] + slopeDb * Math.Abs(k - centre) / reach;
            if (score < bestScore) { bestScore = score; best = k; }
        }
        return best * WordFeatures.HopMs;
    }

    /// <summary>Shrinks a word to the sound inside it (one frame of margin), when its edges sit in silence.</summary>
    public static void Trim(TimedWord w, WordFeatures f)
    {
        int s = f.Frame(w.StartMs), e = f.Frame(w.EndMs);
        if (e <= s) return;
        double threshold = f.SilenceThreshold(f.Peak(w.StartMs, w.EndMs));
        int s2 = s, e2 = e;
        while (s2 < e && f.Db[s2] < threshold) s2++;
        while (e2 > s2 && f.Db[e2] < threshold) e2--;
        if (s2 >= e2) return;
        if (s2 > s) w.StartMs = Math.Max(w.StartMs, (s2 - 1) * WordFeatures.HopMs);
        if (e2 < e) w.EndMs = Math.Min(w.EndMs, (e2 + 1) * WordFeatures.HopMs);
    }

    /// <summary>Moves every boundary of a seeded word list to the quietest frame within +/-window; a boundary two words share stays shared.</summary>
    public static List<TimedWord> Snap(List<TimedWord> seed, WordFeatures f, double windowMs, bool trim)
    {
        var words = seed.Select(w => w.Clone()).ToList();
        static double Middle(TimedWord w) => (w.StartMs + w.EndMs) / 2;
        for (int i = 0; i < words.Count; i++)
        {
            bool sharedBefore = i > 0 && Math.Abs(seed[i].StartMs - seed[i - 1].EndMs) <= 30, sharedAfter = i + 1 < words.Count && Math.Abs(seed[i + 1].StartMs - seed[i].EndMs) <= 30;
            words[i].StartMs = sharedBefore ? words[i - 1].EndMs
                : SnapPoint(f, seed[i].StartMs, windowMs, i > 0 ? Middle(seed[i - 1]) : seed[i].StartMs - windowMs, Middle(seed[i]));
            double end = sharedAfter ? (seed[i].EndMs + seed[i + 1].StartMs) / 2 : seed[i].EndMs;
            words[i].EndMs = Math.Max(words[i].StartMs, SnapPoint(f, end, windowMs, Middle(seed[i]), i + 1 < words.Count ? Middle(seed[i + 1]) : end + windowMs));
        }
        if (trim) foreach (var w in words) Trim(w, f);
        return words;
    }

    /// <summary>
    /// Anchor-valley (AV). Whisper's DTW time lands inside each word, so between the anchors of two neighbouring
    /// words there is exactly one boundary. If that stretch holds a pause, the earlier word ends where the pause
    /// begins and the later one starts where it ends; otherwise both share the frame with the lowest cost (see
    /// <see cref="AnchorOptions"/>). Words without an anchor fall back to their token span.
    /// </summary>
    public static List<TimedWord> AnchorValley(List<TimedWord> tokenWords, WordFeatures f, AnchorOptions o)
    {
        var words = tokenWords.Select(w => w.Clone()).ToList();
        int n = words.Count;
        if (n == 0) return words;
        double audioMs = (f.Db.Length - 1) * WordFeatures.HopMs;
        var firstAnchor = new double[n]; var lastAnchor = new double[n];
        for (int i = 0; i < n; i++)
        {
            double fallback = (words[i].AStart + words[i].AEnd) / 2;
            // Anchors are kept inside the audio and in order, whatever Whisper reported (with a shortened audio
            // context its DTW times can land far past the end of the audio).
            firstAnchor[i] = Math.Clamp(double.IsNaN(words[i].AnchorFirst) ? fallback : words[i].AnchorFirst, 0, audioMs);
            lastAnchor[i] = Math.Max(firstAnchor[i], Math.Clamp(double.IsNaN(words[i].AnchorLast) ? fallback : words[i].AnchorLast, 0, audioMs));
            if (i > 0) { firstAnchor[i] = Math.Max(firstAnchor[i], lastAnchor[i - 1]); lastAnchor[i] = Math.Max(lastAnchor[i], firstAnchor[i]); }
        }
        // Start of the first word: the end of the nearest pause before its anchor.
        {
            double right = firstAnchor[0], left = Math.Max(0, right - 1000);
            var pause = PauseBetween(f, left, right, f.Peak(right - 100, right + 150), o);
            if (pause.Found) { words[0].StartMs = pause.End; words[0].PauseBefore = true; }
            else { words[0].StartMs = SnapPoint(f, Math.Clamp(words[0].AStart, Math.Max(0, right - 800), right), 60, 0, right); words[0].GapBeforeMs = 800; }
        }
        for (int i = 1; i < n; i++)
        {
            double left = lastAnchor[i - 1], right = firstAnchor[i];
            if (right - left < 2 * WordFeatures.HopMs) { words[i - 1].EndMs = words[i].StartMs = (left + right) / 2; continue; }
            double threshold = f.SilenceThreshold(f.Peak(left - 150, right + 150));
            var pause = PauseBetween(f, left, right, f.Peak(left - 150, right + 150), o);
            if (pause.Found)
            {
                words[i - 1].EndMs = pause.Start; words[i].StartMs = pause.End; words[i - 1].PauseAfter = words[i].PauseBefore = true;
                continue;
            }
            double remaining = (1 - o.AnchorAt) * (o.ByLength ? words[i - 1].LastWeight : 1), lead = o.AnchorAt * (o.ByLength ? words[i].FirstWeight : 1), centre = left + (right - left) * remaining / (remaining + lead);
            double tokenBoundary = (words[i - 1].AEnd + words[i].AStart) / 2, bestScore = double.PositiveInfinity, boundary = centre;
            for (int k = f.Frame(left) + 1; k < f.Frame(right); k++)
            {
                double t = k * WordFeatures.HopMs, d = (t - centre) / (right - left);
                double score = Math.Max(0, f.Db[k] - threshold) + o.PhiDb * d * d + o.TokenDb * Math.Min(3, Math.Abs(t - tokenBoundary) / 100);
                if (score < bestScore) { bestScore = score; boundary = t; }
            }
            words[i - 1].EndMs = words[i].StartMs = boundary;
            words[i - 1].GapAfterMs = words[i].GapBeforeMs = right - left;
            words[i - 1].LevelAfterDb = words[i].LevelBeforeDb = Math.Max(0, f.At(boundary) - threshold);
        }
        // End of the last word: where the next pause begins.
        {
            double left = lastAnchor[n - 1], right = Math.Min(audioMs, left + 1200);
            var pause = PauseBetween(f, left, right, f.Peak(left - 150, left + 100), o);
            if (pause.Found) { words[n - 1].EndMs = pause.Start; words[n - 1].PauseAfter = true; }
            else { words[n - 1].EndMs = SnapPoint(f, Math.Clamp(words[n - 1].AEnd, left, Math.Min(audioMs, left + 800)), 60, left, audioMs); words[n - 1].GapAfterMs = 800; }
        }
        foreach (var w in words) { w.EndMs = Math.Max(w.EndMs, w.StartMs); if (o.Trim) Trim(w, f); }
        return words;
    }

    /// <summary>
    /// The silence between two times, if any: from where the first pause begins to where the last one ends. Pauses
    /// are found with the fixed margin and, when <see cref="AnchorOptions.WidePauseMs"/> is on and the recording is
    /// clean enough for a wider margin, also as longer stretches under that wider margin, less the guard on each side.
    /// </summary>
    private static (bool Found, double Start, double End) PauseBetween(WordFeatures f, double fromMs, double toMs, double peakDb, AnchorOptions o)
    {
        var pauses = Pauses(f, fromMs, toMs, f.SilenceThreshold(peakDb), o.PauseMs);
        if (o.WidePauseMs > 0 && f.MarginBySignalToNoiseDb > WordFeatures.NoiseMarginDb)
            foreach (var wide in Pauses(f, fromMs, toMs, f.SilenceThreshold(peakDb, true), o.WidePauseMs))
                if (wide.Length > 2 * o.WideGuardMs) pauses.Add((wide.Start + o.WideGuardMs, wide.End - o.WideGuardMs, wide.Length - 2 * o.WideGuardMs));
        return pauses.Count == 0 ? (false, 0, 0) : (true, pauses.Min(p => p.Start), pauses.Max(p => p.End));
    }

    /// <summary>Runs of frames below the threshold lasting at least <paramref name="minimumMs"/>, between two times.</summary>
    private static List<(double Start, double End, double Length)> Pauses(WordFeatures f, double fromMs, double toMs, double threshold, double minimumMs)
    {
        var runs = new List<(double, double, double)>(); int first = f.Frame(fromMs), last = f.Frame(toMs), run = -1;
        for (int k = first; k <= last + 1; k++)
        {
            bool quiet = k <= last && f.Db[k] < threshold;
            if (quiet && run < 0) run = k;
            else if (!quiet && run >= 0)
            {
                // A frame's 20 ms window reaches 10 ms past its centre, so a quiet frame is already ~10 ms clear of the sound.
                double start = run * WordFeatures.HopMs, end = (k - 1) * WordFeatures.HopMs;
                if (end - start >= minimumMs) runs.Add((start, end, end - start));
                run = -1;
            }
        }
        return runs;
    }

    /// <summary>DTW time taken literally as each word's start (end = the next token's DTW time), as the reference implementation does.</summary>
    public static List<TimedWord> DtwAsStart(List<TimedWord> tokenWords)
    {
        var words = tokenWords.Select(w => w.Clone()).ToList();
        for (int i = 0; i < words.Count; i++)
        {
            if (double.IsNaN(words[i].AnchorFirst)) continue;
            words[i].StartMs = words[i].AnchorFirst;
            words[i].EndMs = Math.Max(words[i].StartMs, !double.IsNaN(words[i].NextAnchor) ? words[i].NextAnchor : i + 1 < words.Count && !double.IsNaN(words[i + 1].AnchorFirst) ? words[i + 1].AnchorFirst : words[i].SegEnd);
        }
        return words;
    }

    /// <summary>DTW time taken literally as each word's end (start = the previous token's DTW time).</summary>
    public static List<TimedWord> DtwAsEnd(List<TimedWord> tokenWords)
    {
        var words = tokenWords.Select(w => w.Clone()).ToList();
        for (int i = 0; i < words.Count; i++)
        {
            if (double.IsNaN(words[i].AnchorLast)) continue;
            words[i].EndMs = words[i].AnchorLast;
            words[i].StartMs = Math.Min(words[i].EndMs, i > 0 && words[i - 1].Segment == words[i].Segment && !double.IsNaN(words[i - 1].AnchorLast) ? words[i - 1].AnchorLast : words[i].SegStart);
        }
        return words;
    }

    public static string Describe(IEnumerable<TimedWord> words)
    {
        var text = new StringBuilder();
        foreach (var w in words) text.Append($"{w.Text}[{w.StartMs:0}-{w.EndMs:0}] ");
        return text.ToString().TrimEnd();
    }
}
