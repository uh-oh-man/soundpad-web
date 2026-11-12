namespace SoundpadWeb.Testing;

/// <summary>
/// What the real-Soundpad exact-playback probe (2.37) measures in a capture of Soundpad's own output, kept apart
/// from the probe so it can be tested with made-up captures. A fixture is built from back-to-back tone segments of
/// different frequencies; a slice of it must sound only its own segment(s): any other frequency before the wanted
/// one is a leaked beginning, any after it an overrun past the end.
/// </summary>
public static class ExactPlaybackAnalysis
{
    /// <summary>About -48 dBFS: quiet enough to go unnoticed in a game or call, far above an idle capture.</summary>
    public const float QuietLevel = 0.004f;

    /// <summary>Tone segments (Hz, ms) back to back as mono samples, without fades, so each edge is exact.</summary>
    public static float[] Tones(int rate, float level, params (double Hz, int Ms)[] parts)
    {
        var samples = new List<float>();
        foreach (var (hz, ms) in parts)
        {
            long frames = SliceRenderer.FrameOf(ms, rate);
            for (long i = 0; i < frames; i++) samples.Add(hz <= 0 ? 0 : level * MathF.Sin((float)(2 * Math.PI * hz * i / rate)));
        }
        return [.. samples];
    }

    /// <summary>Share of one frequency in a window (Goertzel) against the window's power, and its RMS.</summary>
    private static (double Share, double Rms) Tone(float[] data, int start, int length, int rate, double hz)
    {
        double w = 2 * Math.PI * hz / rate, coeff = 2 * Math.Cos(w), s0, s1 = 0, s2 = 0, energy = 0;
        for (int i = 0; i < length; i++) { double x = data[start + i]; energy += x * x; s0 = x + coeff * s1 - s2; s2 = s1; s1 = s0; }
        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return (energy <= 0 ? 0 : Math.Min(1, 2 * power / (length * energy)), Math.Sqrt(energy / length));
    }

    /// <summary>Where a frequency dominates: first and last sample, and in how many 1 ms steps of a 5 ms window. Null when never.</summary>
    public static (int First, int Last, int Ms)? Span(float[] data, int rate, double hz, double floor)
    {
        int window = rate / 200, hop = rate / 1000, first = -1, last = -1, count = 0;
        for (int at = 0; at + window <= data.Length; at += hop)
        {
            var (share, rms) = Tone(data, at, window, rate, hz);
            if (rms > floor && share > 0.6) { if (first < 0) first = at; last = at + window; count++; }
        }
        return first < 0 ? null : (first, last, count);
    }

    /// <summary>The idle level a tone must stand above: four times the RMS of a capture while nothing plays (never below 1e-5).</summary>
    public static double Floor(float[] idle) => Math.Max(1e-5, Math.Sqrt(idle.Select(x => (double)x * x).DefaultIfEmpty(0).Average()) * 4);

    public sealed record Verdict(bool Exact, double LeakMs, double OverrunMs, double? WantedMs, double? OnsetMs, string Detail);

    /// <summary>
    /// Judges one play. <paramref name="capture"/> starts when the play command was sent. Exact: the wanted
    /// frequency is heard for its length (within <paramref name="toleranceMs"/>) and nothing of the frequencies
    /// before or after it in the fixture is heard at all (two 1 ms steps allowed for the window's own smear).
    /// </summary>
    public static Verdict Judge(float[] capture, int rate, double floor, double wantedHz, double wantedMs, double[] beforeHz, double[] afterHz, double toleranceMs = 12)
    {
        var wanted = Span(capture, rate, wantedHz, floor);
        double leak = beforeHz.Sum(hz => Span(capture, rate, hz, floor)?.Ms ?? 0), overrun = afterHz.Sum(hz => Span(capture, rate, hz, floor)?.Ms ?? 0);
        if (wanted == null) return new(false, leak, overrun, null, null, "the wanted part was never heard (no output captured?)");
        double heard = (wanted.Value.Last - wanted.Value.First) * 1000.0 / rate, onset = wanted.Value.First * 1000.0 / rate;
        bool exact = leak <= 2 && overrun <= 2 && Math.Abs(heard - wantedMs) <= toleranceMs;
        return new(exact, leak, overrun, Math.Round(heard, 1), Math.Round(onset, 1),
            $"wanted part heard {heard:0.0} ms (asked {wantedMs:0} ms), began {onset:0.0} ms after the command; leaked beginning {leak} ms, overrun past the end {overrun} ms");
    }
}
