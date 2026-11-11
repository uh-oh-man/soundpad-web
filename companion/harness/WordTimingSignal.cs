namespace SoundpadWeb.Testing;

// Word-timing evaluation: audio helpers. WAV reading, the deterministic noise beds that make the
// "noisy" and "overlap" variants of each fixture, and the short-time energy analysis the boundary
// refinements use. Nothing here plays audio or opens a device.

/// <summary>Speech-band energy per 5 ms frame of a 16 kHz signal, in dB, lightly smoothed.</summary>
public sealed class WordFeatures
{
    public const double HopMs = 5;
    public required double[] Db { get; init; }
    /// <summary>5th and 99th percentile of <see cref="Db"/>: the noise floor and the loud speech level.</summary>
    public double FloorDb { get; init; }
    public double PeakDb { get; init; }
    public int Frame(double ms) => Math.Clamp((int)Math.Round(ms / HopMs), 0, Db.Length - 1);
    public double At(double ms) => Db[Frame(ms)];
    public double Peak(double fromMs, double toMs)
    {
        double peak = double.NegativeInfinity;
        for (int f = Frame(fromMs), last = Frame(toMs); f <= last; f++) peak = Math.Max(peak, Db[f]);
        return peak;
    }
    /// <summary>
    /// Energy below this is treated as "no speech here" around a word whose loudest frame is <paramref name="peakDb"/>:
    /// 35 dB under that peak, or just above the background when the background is louder than that.
    /// </summary>
    public double SilenceThreshold(double peakDb, bool bySignalToNoise = false) => Math.Max(FloorDb + (bySignalToNoise ? MarginBySignalToNoiseDb : NoiseMarginDb), peakDb - 35);
    /// <summary>How far above the background level sound must be to count as speech. Tuned: 6 dB cut quiet consonants off in noise (see the report).</summary>
    public static double NoiseMarginDb = 1.5;
    /// <summary>
    /// The same margin, widened when the recording is clean: +0.5 dB for every dB by which loud speech stands more
    /// than 22 dB above the background, up to 7 dB. Added after the real-speech sample showed that a real room's
    /// background wanders several dB, so pauses there never stay under a 1.5 dB margin (see the report). Used only
    /// for the second, looser look for pauses (AnchorOptions.WidePauseMs).
    /// </summary>
    public double MarginBySignalToNoiseDb => Math.Clamp(NoiseMarginDb + 0.5 * (PeakDb - FloorDb - 22), NoiseMarginDb, 7);
}

public static class WordTimingSignal
{
    public const int Rate = 16000;
    private const int Hop = 80, Window = 320, FftSize = 512; // 5 ms hop, 20 ms Hann window, zero-padded

    /// <summary>Reads a WAV as mono float samples at its own rate.</summary>
    public static (float[] Samples, int Rate) ReadMono(string path)
    {
        using var file = new NAudio.Wave.WaveFileReader(path);
        var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(file);
        int channels = provider.WaveFormat.Channels;
        var mono = new List<float>(); var block = new float[channels * 4096]; int read;
        while ((read = provider.Read(block)) > 0)
            for (int i = 0; i + channels <= read; i += channels)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += block[i + c];
                mono.Add(sum / channels);
            }
        return (mono.ToArray(), provider.WaveFormat.SampleRate);
    }

    /// <summary>Pink (1/f) noise from a seeded generator (Paul Kellet's filter), so every run mixes the same noise.</summary>
    public static float[] PinkNoise(int length, int seed)
    {
        var random = new Random(seed); var output = new float[length];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < length; i++)
        {
            double white = random.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + white * 0.0555179; b1 = 0.99332 * b1 + white * 0.0750759; b2 = 0.96900 * b2 + white * 0.1538520;
            b3 = 0.86650 * b3 + white * 0.3104856; b4 = 0.55000 * b4 + white * 0.5329522; b5 = -0.7616 * b5 - white * 0.0168980;
            output[i] = (float)((b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362) * 0.11);
            b6 = white * 0.115926;
        }
        return output;
    }

    /// <summary>
    /// A synthetic "music" bed: a four-chord loop of sine partials with a 5 Hz tremolo and a decaying pulse
    /// twice a second, so its level moves like music rather than sitting still like noise.
    /// </summary>
    public static float[] MusicBed(int length, int rate)
    {
        double[] roots = [220.0, 174.61, 261.63, 196.0]; double[] ratios = [1, 1.25, 1.5, 2, 3, 4, 5];
        var output = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / rate, root = roots[(int)(t / 0.75) % roots.Length], sum = 0;
            for (int k = 0; k < ratios.Length; k++) sum += Math.Sin(2 * Math.PI * root * ratios[k] * t) / (k + 1);
            double tremolo = 1 - 0.4 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 5 * t));
            double pulse = 0.55 + 0.45 * Math.Exp(-(t % 0.5) * 6);
            output[i] = (float)(sum * tremolo * pulse * 0.2);
        }
        return output;
    }

    /// <summary>RMS over the 20 ms frames within 30 dB of the loudest one: the level of the sound itself, pauses excluded.</summary>
    public static double ActiveRms(float[] samples, int rate)
    {
        int frame = rate / 50; var energies = new List<double>();
        for (int i = 0; i + frame <= samples.Length; i += frame)
        {
            double sum = 0;
            for (int j = 0; j < frame; j++) sum += samples[i + j] * samples[i + j];
            energies.Add(sum / frame);
        }
        if (energies.Count == 0) return 0;
        double limit = energies.Max() * 1e-3;
        var active = energies.Where(e => e >= limit).ToList();
        return Math.Sqrt(active.Average());
    }

    /// <summary>Repeats (or cuts) a sound to the wanted length, starting <paramref name="offset"/> samples in.</summary>
    public static float[] Loop(float[] source, int length, int offset)
    {
        var output = new float[length];
        for (int i = 0; i < length; i++) output[i] = source[(i + offset) % source.Length];
        return output;
    }

    /// <summary>speech + noise, with the noise scaled so speech is <paramref name="snrDb"/> dB above it (active levels). Scaled down if it would clip.</summary>
    public static float[] Mix(float[] speech, float[] noise, double snrDb, int rate)
    {
        double gain = ActiveRms(speech, rate) / Math.Max(1e-9, ActiveRms(noise, rate)) / Math.Pow(10, snrDb / 20);
        var output = new float[speech.Length]; float peak = 0;
        for (int i = 0; i < output.Length; i++) { output[i] = (float)(speech[i] + gain * noise[i]); peak = Math.Max(peak, Math.Abs(output[i])); }
        if (peak > 0.98f) for (int i = 0; i < output.Length; i++) output[i] *= 0.98f / peak;
        return output;
    }

    /// <summary>Speech-band (150 Hz to 6 kHz) energy per 5 ms frame. Frame f is centred on sample f * 80.</summary>
    public static WordFeatures Analyze(float[] samples16k)
    {
        int frames = Math.Max(1, samples16k.Length / Hop + 1);
        var raw = new double[frames]; var re = new double[FftSize]; var im = new double[FftSize];
        var hann = new double[Window];
        for (int i = 0; i < Window; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1));
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
        var sorted = smooth.OrderBy(v => v).ToArray();
        return new WordFeatures { Db = smooth, FloorDb = sorted[(int)(sorted.Length * 0.05)], PeakDb = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] };
    }

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

    /// <summary>Whisper returns nothing for less than one second of audio, so short slices are padded with silence.</summary>
    public static float[] SliceWithSilence(float[] samples16k, double fromMs, double toMs, double silenceMs = 300, double fadeMs = 5)
    {
        int from = Math.Clamp((int)(fromMs * Rate / 1000), 0, samples16k.Length), to = Math.Clamp((int)(toMs * Rate / 1000), from, samples16k.Length);
        int pad = (int)(silenceMs * Rate / 1000), fade = Math.Min((int)(fadeMs * Rate / 1000), (to - from) / 2);
        var output = new float[Math.Max(pad + (to - from) + pad, (int)(1.25 * Rate))];
        Array.Copy(samples16k, from, output, pad, to - from);
        for (int i = 0; i < fade; i++) { float g = (float)(i + 1) / (fade + 1); output[pad + i] *= g; output[pad + (to - from) - 1 - i] *= g; }
        return output;
    }
}
