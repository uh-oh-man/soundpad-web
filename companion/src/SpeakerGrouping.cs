namespace SoundpadWeb;

// Speaker grouping (2.41, a quality-gated prototype behind the host's "Group speakers (experimental)" switch, off by
// default). Within one recording-history session, phrases are grouped by how they sound: simple features computed in
// plain C# from the audio (mel-cepstrum means and spreads, and pitch, over voiced frames), clustered online with
// conservative thresholds. A group is "Speaker 1, 2, ..." until a person names it; anything not clearly close to one
// group, or clearly new, stays Unassigned. Nothing carries over to another session; a person may link a named group to
// a library voice explicitly. Measured on synthetic fixtures only (see the Stage F1 report): not proven on real voices.

/// <summary>What a phrase sounds like: 12 cepstral means, 12 cepstral spreads, median pitch and its spread (semitones).</summary>
public sealed record SpeakerFeatures(double[] Mean, double[] Spread, double PitchSemitones, double PitchRange, double VoicedSeconds);

public static class SpeakerAnalysis
{
    public const int Rate = 16000;
    private const int Frame = 400, Hop = 160, Fft = 512, Filters = 26, Coefficients = 12;
    private const int MinLag = Rate / 400, MaxLag = Rate / 70, PitchWindow = 640;
    /// <summary>A phrase needs this much voiced speech to say anything about its speaker.</summary>
    public const double MinVoicedSeconds = 0.35;

    private static readonly Lazy<double[]> Hamming = new(() => Enumerable.Range(0, Frame).Select(i => 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Frame - 1))).ToArray());
    private static readonly Lazy<double[][]> MelBank = new(BuildMel);
    private static double Mel(double hz) => 2595 * Math.Log10(1 + hz / 700);
    private static double Hz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);
    private static double[][] BuildMel()
    {
        double low = Mel(80), high = Mel(7600);
        var points = Enumerable.Range(0, Filters + 2).Select(i => Hz(low + (high - low) * i / (Filters + 1)) * Fft / Rate).ToArray();
        var bank = new double[Filters][];
        for (int m = 0; m < Filters; m++)
        {
            bank[m] = new double[Fft / 2 + 1];
            for (int k = 0; k <= Fft / 2; k++)
            {
                double left = points[m], centre = points[m + 1], right = points[m + 2];
                bank[m][k] = k < left || k > right ? 0 : k <= centre ? (k - left) / Math.Max(1e-9, centre - left) : (right - k) / Math.Max(1e-9, right - centre);
            }
        }
        return bank;
    }

    /// <summary>Features of a phrase (16 kHz mono), or null when it has too little voiced speech.</summary>
    public static SpeakerFeatures? Analyze(ReadOnlySpan<float> samples16k)
    {
        int frames = samples16k.Length < Frame ? 0 : (samples16k.Length - Frame) / Hop + 1;
        if (frames < 10) return null;
        var energies = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = 0; i < Frame; i++) { double s = samples16k[f * Hop + i]; sum += s * s; }
            energies[f] = 10 * Math.Log10(sum / Frame + 1e-12);
        }
        double loudest = energies.Max();
        var hamming = Hamming.Value; var bank = MelBank.Value;
        var re = new double[Fft]; var im = new double[Fft]; var logMel = new double[Filters];
        var cepstra = new List<double[]>(); var pitches = new List<double>();
        for (int f = 0; f < frames; f++)
        {
            if (energies[f] < loudest - 20 || energies[f] < -60) continue;
            double? f0 = Pitch(samples16k, f * Hop + Frame / 2);
            if (f0 == null) continue;
            Array.Clear(re); Array.Clear(im);
            int at = f * Hop;
            for (int i = 0; i < Frame; i++) re[i] = (samples16k[at + i] - (at + i > 0 ? 0.97 * samples16k[at + i - 1] : 0)) * hamming[i];
            Transform(re, im);
            for (int m = 0; m < Filters; m++)
            {
                double e = 0; var w = bank[m];
                for (int k = 0; k <= Fft / 2; k++) if (w[k] > 0) e += w[k] * (re[k] * re[k] + im[k] * im[k]);
                logMel[m] = Math.Log(e + 1e-10);
            }
            var c = new double[Coefficients];
            for (int n = 1; n <= Coefficients; n++)
            {
                double sum = 0;
                for (int m = 0; m < Filters; m++) sum += logMel[m] * Math.Cos(Math.PI * n * (m + 0.5) / Filters);
                c[n - 1] = sum * Math.Sqrt(2.0 / Filters);
            }
            cepstra.Add(c); pitches.Add(12 * Math.Log2(f0.Value));
        }
        double voiced = cepstra.Count * (double)Hop / Rate;
        if (voiced < MinVoicedSeconds) return null;
        var mean = new double[Coefficients]; var spread = new double[Coefficients];
        foreach (var c in cepstra) for (int i = 0; i < Coefficients; i++) mean[i] += c[i] / cepstra.Count;
        foreach (var c in cepstra) for (int i = 0; i < Coefficients; i++) spread[i] += (c[i] - mean[i]) * (c[i] - mean[i]) / cepstra.Count;
        for (int i = 0; i < Coefficients; i++) spread[i] = Math.Sqrt(spread[i]);
        pitches.Sort();
        double median = pitches[pitches.Count / 2], range = pitches[(int)(pitches.Count * 0.9)] - pitches[(int)(pitches.Count * 0.1)];
        return new(mean, spread, median, range, voiced);
    }

    /// <summary>Pitch at a sample (Hz) from the normalised autocorrelation, or null when the frame is not clearly voiced.</summary>
    private static double? Pitch(ReadOnlySpan<float> s, int centre)
    {
        int start = centre - PitchWindow / 2;
        if (start < 0 || start + PitchWindow + MaxLag > s.Length) { start = Math.Max(0, Math.Min(start, s.Length - PitchWindow - MaxLag)); if (start < 0) return null; }
        double r0 = 0;
        for (int i = 0; i < PitchWindow; i++) r0 += s[start + i] * s[start + i];
        if (r0 < 1e-7) return null;
        double best = 0; int bestLag = 0;
        for (int lag = MinLag; lag <= MaxLag; lag++)
        {
            double r = 0, rl = 0;
            for (int i = 0; i < PitchWindow; i++) { r += s[start + i] * s[start + i + lag]; rl += s[start + i + lag] * s[start + i + lag]; }
            double n = r / Math.Sqrt(r0 * rl + 1e-12);
            if (n > best) { best = n; bestLag = lag; }
        }
        if (best < 0.7 || bestLag == 0) return null;
        // A strong peak at half the lag is the true period (octave errors).
        int half = bestLag / 2;
        if (half >= MinLag)
        {
            double r = 0, rl = 0;
            for (int i = 0; i < PitchWindow; i++) { r += s[start + i] * s[start + i + half]; rl += s[start + i + half] * s[start + i + half]; }
            if (r / Math.Sqrt(r0 * rl + 1e-12) > 0.85 * best) bestLag = half;
        }
        return (double)Rate / bestLag;
    }

    private static void Transform(double[] re, double[] im)
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

/// <summary>Thresholds of the online grouping (distances in units of typical within-speaker variation).</summary>
public sealed record SpeakerGroupingOptions
{
    /// <summary>Closer than this to a group (and clearly closer than to any other): joins it.</summary>
    public double Join { get; init; } = 1.8;
    /// <summary>Farther than this from every group (and long enough): starts a new group. Between the two: Unassigned.</summary>
    public double New { get; init; } = 2.3;
    /// <summary>The nearest group must be this much closer than the second.</summary>
    public double Margin { get; init; } = 0.35;
    /// <summary>Voiced speech a phrase needs to start a group.</summary>
    public double NewGroupSeconds { get; init; } = 0.8;
    public int MaxGroups { get; init; } = 8;
    /// <summary>Typical within-speaker spread of each cepstral mean (measured on the fixtures).</summary>
    public double[] Scale { get; init; } = [1.0, 0.85, 0.85, 0.75, 0.5, 0.5, 0.36, 0.36, 0.31, 0.28, 0.25, 0.23];
    /// <summary>Semitones of pitch that count as one unit of distance. (Settings chosen by the --speaker-eval sweep, 2.41.)</summary>
    public double PitchScale { get; init; } = 1.0;
}

/// <summary>Online, conservative grouping of the phrases of one session. Not thread-safe; the caller serialises.</summary>
public sealed class SpeakerGrouper(SpeakerGroupingOptions? options = null)
{
    public SpeakerGroupingOptions Options { get; } = options ?? new();
    private sealed class Group { public int Id; public double[] Mean = []; public double Pitch; public int Count; }
    private readonly List<Group> groups = [];
    public int GroupCount => groups.Count;

    /// <summary>Distance between a phrase and a group: cepstral means (scaled) and pitch, root mean square.</summary>
    public double Distance(SpeakerFeatures x, double[] mean, double pitch)
    {
        double sum = 0;
        for (int i = 0; i < x.Mean.Length; i++) { double d = (x.Mean[i] - mean[i]) / Options.Scale[i]; sum += d * d; }
        double p = (x.PitchSemitones - pitch) / Options.PitchScale;
        // Pitch counts as much as all the cepstral dimensions together.
        return Math.Sqrt((sum / x.Mean.Length + p * p) / 2);
    }

    /// <summary>The group a phrase belongs to (1-based), or null for Unassigned.</summary>
    public int? Assign(SpeakerFeatures? x)
    {
        if (x == null) return null;
        Group? best = null; double first = double.PositiveInfinity, second = double.PositiveInfinity;
        foreach (var g in groups)
        {
            double d = Distance(x, g.Mean, g.Pitch);
            if (d < first) { second = first; first = d; best = g; }
            else if (d < second) second = d;
        }
        if (best != null && first < Options.Join && second - first >= Options.Margin)
        {
            // A running mean that keeps adapting slowly (at most 1/30 per phrase).
            double w = 1.0 / Math.Min(best.Count + 1, 30);
            for (int i = 0; i < best.Mean.Length; i++) best.Mean[i] += w * (x.Mean[i] - best.Mean[i]);
            best.Pitch += w * (x.PitchSemitones - best.Pitch); best.Count++;
            return best.Id;
        }
        if ((best == null || first > Options.New) && x.VoicedSeconds >= Options.NewGroupSeconds && groups.Count < Options.MaxGroups)
        {
            var g = new Group { Id = groups.Count + 1, Mean = (double[])x.Mean.Clone(), Pitch = x.PitchSemitones, Count = 1 };
            groups.Add(g);
            return g.Id;
        }
        return null;
    }

    /// <summary>Purity (assigned phrases in their group's majority speaker) and coverage (phrases assigned at all).</summary>
    public static (double Purity, double Coverage, int Groups) Score(IReadOnlyList<(string Truth, int? Group)> results)
    {
        var assigned = results.Where(r => r.Group != null).ToList();
        if (results.Count == 0) return (0, 0, 0);
        int majority = assigned.GroupBy(r => r.Group).Sum(g => g.GroupBy(r => r.Truth).Max(t => t.Count()));
        return (assigned.Count == 0 ? 0 : (double)majority / assigned.Count, (double)assigned.Count / results.Count, assigned.Select(r => r.Group).Distinct().Count());
    }
}
