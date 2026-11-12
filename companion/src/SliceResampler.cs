namespace SoundpadWeb;

/// <summary>
/// Sample-rate conversion for slices and assembled sentences: a Kaiser-windowed sinc with 32 zero crossings a
/// side (about 80 dB stopband), cut off at the lower of the two Nyquist rates so nothing aliases. The companion's
/// other resampler (<c>MimicTranscriber.Resample</c>) is linear, which is fine for feeding Whisper but dulls and
/// aliases audio that is played back.
///
/// It is zero-phase: output frame i sits exactly at source time i * from / to, so a slice keeps its start and
/// offsets on a timeline stay exact. Audio outside the given samples counts as silence.
/// </summary>
public static class SliceResampler
{
    private const int ZeroCrossings = 32;
    private const double Beta = 8.0;
    // Above this many table entries the kernel is computed per output frame instead (unusual rate pairs only).
    private const long MaxTable = 1 << 20;

    /// <summary>Frames a conversion produces: round(frames * to / from), and never zero for audio that exists.</summary>
    public static long OutputFrames(long frames, int from, int to) =>
        frames <= 0 ? 0 : Math.Max(1, (long)Math.Round((double)frames * to / from, MidpointRounding.AwayFromZero));

    /// <summary>Converts interleaved samples from one rate to another. The same array comes back when the rates match.</summary>
    public static float[] Resample(float[] interleaved, int channels, int from, int to)
    {
        if (channels is < 1 or > 16 || from is < 8000 or > 384000 || to is < 8000 or > 384000 || interleaved.Length % channels != 0)
            throw new InvalidDataException("Unsupported audio format.");
        if (from == to) return interleaved;
        long frames = interleaved.Length / channels, outFrames = OutputFrames(frames, from, to);
        if (outFrames * channels > Array.MaxLength) throw new InvalidDataException("That audio is too long to prepare.");
        var output = new float[outFrames * channels];

        int divisor = Gcd(from, to), up = to / divisor, down = from / divisor; // output i sits at source frame i * down / up
        double cutoff = Math.Min(1.0, (double)to / from), halfWidth = ZeroCrossings / cutoff;
        int reach = (int)Math.Ceiling(halfWidth), taps = reach * 2;             // source frames whole - reach + 1 .. whole + reach
        bool tabled = (long)up * taps <= MaxTable;
        var table = tabled ? new float[up * taps] : Array.Empty<float>();
        var built = tabled ? new bool[up] : Array.Empty<bool>();
        var kernel = new float[taps];
        var sums = new double[channels];

        for (long i = 0; i < outFrames; i++)
        {
            long numerator = i * down, whole = numerator / up; int phase = (int)(numerator % up);
            Span<float> weights = tabled ? table.AsSpan(phase * taps, taps) : kernel;
            if (!tabled || !built[phase])
            {
                Kernel(weights, (double)phase / up, cutoff, halfWidth, reach);
                if (tabled) built[phase] = true;
            }
            Array.Clear(sums);
            long first = whole - reach + 1;
            int from0 = (int)Math.Max(0, -first), to0 = (int)Math.Min(taps, frames - first);
            for (int t = from0; t < to0; t++)
            {
                float weight = weights[t]; long at = (first + t) * channels;
                for (int c = 0; c < channels; c++) sums[c] += interleaved[at + c] * weight;
            }
            for (int c = 0; c < channels; c++) output[i * channels + c] = (float)sums[c];
        }
        return output;
    }

    /// <summary>The filter weights for one fractional position, scaled to add up to 1 so level is preserved.</summary>
    private static void Kernel(Span<float> weights, double fraction, double cutoff, double halfWidth, int reach)
    {
        double sum = 0;
        for (int t = 0; t < weights.Length; t++)
        {
            double distance = t - reach + 1 - fraction, x = distance / halfWidth, value = 0;
            if (Math.Abs(x) < 1)
            {
                double phase = Math.PI * cutoff * distance;
                value = (Math.Abs(phase) < 1e-12 ? 1 : Math.Sin(phase) / phase) * Bessel(Beta * Math.Sqrt(1 - x * x));
            }
            weights[t] = (float)value; sum += value;
        }
        if (sum != 0) for (int t = 0; t < weights.Length; t++) weights[t] = (float)(weights[t] / sum);
    }

    /// <summary>Modified Bessel function of the first kind, order zero (the Kaiser window's shape).</summary>
    private static double Bessel(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 64; k++)
        {
            term *= half / k * (half / k); sum += term;
            if (term < sum * 1e-14) break;
        }
        return sum;
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
