using NAudio.Wave;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Shared by the Slice tests: sources whose every frame is recognisable, and simple measurements.</summary>
internal static class SliceFixture
{
    // A stereo pair that is different for every frame up to several minutes, and survives 16-bit exactly.
    public static short Low(long frame) => (short)(frame % 60000 - 30000);
    public static short High(long frame) => (short)(frame / 60000 - 5);

    /// <summary>Stereo source: frame f holds (Low(f), High(f)).</summary>
    public static PcmSliceSource Ramp(int rate, long frames)
    {
        var samples = new float[frames * 2];
        for (long f = 0; f < frames; f++) { samples[f * 2] = Low(f) / 32768f; samples[f * 2 + 1] = High(f) / 32768f; }
        return new(samples, rate, 2);
    }

    /// <summary>Mono source: frame f holds Low(f) (unique below 60,000 frames).</summary>
    public static PcmSliceSource Mono(int rate, long frames) => new(Enumerable.Range(0, (int)frames).Select(f => Low(f) / 32768f).ToArray(), rate, 1);

    public static PcmSliceSource Constant(int rate, long frames, float value) => new(Enumerable.Repeat(value, (int)frames).ToArray(), rate, 1);

    public static float[] Tone(int rate, double hertz, double amplitude, int frames) =>
        Enumerable.Range(0, frames).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hertz * i / rate))).ToArray();

    /// <summary>Reads a WAV with NAudio, not with the code under test.</summary>
    public static short[] Samples(byte[] wav, out int rate, out int channels)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        Assert.Equal(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding); Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        rate = reader.WaveFormat.SampleRate; channels = reader.WaveFormat.Channels;
        var bytes = new byte[reader.Length]; int done = 0, read;
        while (done < bytes.Length && (read = reader.Read(bytes, done, bytes.Length - done)) > 0) done += read;
        Assert.Equal(bytes.Length, done);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    /// <summary>Frequency from upward zero crossings (the method the keep-pitch test uses).</summary>
    public static double Hertz(ReadOnlySpan<float> mono, int rate)
    {
        int crossings = 0;
        for (int i = 1; i < mono.Length; i++) if (mono[i - 1] < 0 && mono[i] >= 0) crossings++;
        return crossings / (mono.Length / (double)rate);
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var sample in samples) sum += (double)sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }

    public static double Db(double linear) => 20 * Math.Log10(linear);
    public static float[] Floats(short[] samples) => samples.Select(s => s / 32768f).ToArray();
}

public sealed class SliceRendererTests
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 44100, 48000, 96000];
    private static readonly (double From, double To)[] Spans = [(0, 10), (12.3, 45.6), (100, 100.5), (333.333, 987.654), (0.4, 1.6), (500, 1000), (999.2, 1000)];

    [Theory]
    [InlineData(0, 48000, 0)]
    [InlineData(1000, 44100, 44100)]
    [InlineData(12.3, 44100, 542)]     // 542.43
    [InlineData(0.0104, 48000, 0)]     // 0.4992
    [InlineData(0.0105, 48000, 1)]     // 0.504
    [InlineData(333.333, 22050, 7350)] // 7349.99
    [InlineData(2.5, 8000, 20)]
    [InlineData(1.0625, 8000, 9)]      // exactly 8.5: halves round away from zero
    public void FrameOfIsTheNearestFrame(double ms, int rate, long expected)
    {
        Assert.Equal(expected, SliceRenderer.FrameOf(ms, rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => SliceRenderer.FrameOf(double.NaN, rate));
    }

    [Fact]
    public void FrameOfMatchesDecimalArithmeticAcrossRates()
    {
        foreach (int rate in Rates)
            foreach (var (from, to) in Spans)
                foreach (double ms in new[] { from, to })
                    Assert.Equal((long)Math.Round((decimal)ms * rate / 1000m, MidpointRounding.AwayFromZero), SliceRenderer.FrameOf(ms, rate));
    }

    [Fact]
    public void SlicesHoldExactlyTheFramesOfTheRangeAndNothingOutsideIt()
    {
        foreach (int rate in Rates)
        {
            var source = SliceFixture.Ramp(rate, rate + 10); // a little over one second
            foreach (var (from, to) in Spans)
            {
                long start = SliceRenderer.FrameOf(from, rate), end = SliceRenderer.FrameOf(to, rate);
                var wav = SliceRenderer.Render(source, start, end);
                var samples = SliceFixture.Samples(wav, out int outRate, out int channels);
                Assert.Equal(rate, outRate); Assert.Equal(2, channels);
                Assert.Equal(end - start, samples.Length / 2);
                for (long i = 0; i < end - start; i++)
                {
                    Assert.Equal(SliceFixture.Low(start + i), samples[i * 2]);
                    Assert.Equal(SliceFixture.High(start + i), samples[i * 2 + 1]);
                }
                // Said outright: the first sample is source[start], the last is source[end - 1], and the frames on
                // either side of the range are nowhere in the file.
                Assert.Equal((SliceFixture.Low(start), SliceFixture.High(start)), (samples[0], samples[1]));
                Assert.Equal((SliceFixture.Low(end - 1), SliceFixture.High(end - 1)), (samples[^2], samples[^1]));
                var frames = Enumerable.Range(0, samples.Length / 2).Select(i => (samples[i * 2], samples[i * 2 + 1])).ToHashSet();
                if (start > 0) Assert.DoesNotContain((SliceFixture.Low(start - 1), SliceFixture.High(start - 1)), frames);
                Assert.DoesNotContain((SliceFixture.Low(end), SliceFixture.High(end)), frames);
                Assert.Equal(wav, SliceRenderer.RenderMs(source, from, to));
            }
        }
    }

    [Fact]
    public void ARangeOutsideTheSourceIsATypedError()
    {
        var source = SliceFixture.Mono(48000, 1000);
        var before = Assert.Throws<SliceRangeException>(() => SliceRenderer.Render(source, -1, 10));
        Assert.Equal((-1, 10, 1000), (before.StartFrame, before.EndFrame, before.FrameCount));
        Assert.Throws<SliceRangeException>(() => SliceRenderer.Render(source, 990, 1001));
        Assert.Throws<SliceRangeException>(() => SliceRenderer.Render(source, 10, 10));
        Assert.Throws<SliceRangeException>(() => SliceRenderer.Render(source, 20, 10));
        Assert.Throws<SliceRangeException>(() => SliceRenderer.RenderMs(source, 0, 25)); // 1,200 frames asked of 1,000
        Assert.Equal(1000, SliceFixture.Samples(SliceRenderer.Render(source, 0, 1000), out _, out _).Length); // the whole source is fine

        // Clamping is opt-in and still exact about what remains.
        var clamp = new SliceOptions { ClampToSource = true };
        var head = SliceFixture.Samples(SliceRenderer.Render(source, -10, 20, clamp), out _, out _);
        Assert.Equal(20, head.Length); Assert.Equal(SliceFixture.Low(0), head[0]);
        var tail = SliceFixture.Samples(SliceRenderer.Render(source, 995, 5000, clamp), out _, out _);
        Assert.Equal(5, tail.Length); Assert.Equal(SliceFixture.Low(999), tail[^1]);
        Assert.Throws<SliceRangeException>(() => SliceRenderer.Render(source, 2000, 3000, clamp));
        Assert.Equal((0L, 1000L), SliceRenderer.CheckRange(source, -5, 4000, clamp: true));
    }

    private sealed class Expired(ISliceSource inner, long firstHeld) : ISliceSource
    {
        public int SampleRate => inner.SampleRate;
        public int Channels => inner.Channels;
        public long FrameCount => inner.FrameCount;
        public int Read(long startFrame, Span<float> destination) => startFrame < firstHeld ? 0 : inner.Read(startFrame, destination);
    }

    [Fact]
    public void AudioTheSourceNoLongerHoldsIsAnErrorNotSilence()
    {
        // Rolling history: the frames are inside FrameCount but the ring has moved on.
        var source = new Expired(SliceFixture.Mono(48000, 1000), 500);
        Assert.Throws<SliceRangeException>(() => SliceRenderer.Render(source, 400, 600));
        Assert.Equal(100, SliceFixture.Samples(SliceRenderer.Render(source, 500, 600), out _, out _).Length);
    }

    [Fact]
    public void ReverseIsTheSameFramesBackwardsWithChannelsInPlace()
    {
        var source = SliceFixture.Ramp(44100, 70000);
        long start = 59990, end = 60020; // across the point where the second channel changes
        var samples = SliceFixture.Samples(SliceRenderer.Render(source, start, end, new SliceOptions { Reverse = true }), out _, out _);
        Assert.Equal(end - start, samples.Length / 2);
        for (long i = 0; i < end - start; i++)
        {
            Assert.Equal(SliceFixture.Low(end - 1 - i), samples[i * 2]);
            Assert.Equal(SliceFixture.High(end - 1 - i), samples[i * 2 + 1]);
        }
    }

    [Fact]
    public void FadesAndGainWorkInsideTheRangeOnly()
    {
        const int rate = 48000; const short level = 16384;
        // Half scale everywhere, with a full-scale frame just before and just after the range.
        var data = Enumerable.Repeat(0.5f, 10000).ToArray(); data[999] = data[5800] = 1f;
        var source = new PcmSliceSource(data, rate, 1);
        var samples = SliceFixture.Samples(SliceRenderer.Render(source, 1000, 5800, new SliceOptions { FadeInMs = 10, FadeOutMs = 20 }), out _, out _);
        Assert.Equal(4800, samples.Length);
        for (int i = 0; i < 480; i++) Assert.Equal((short)Math.Round(level * (i + 0.5) / 480), samples[i]);              // 10 ms up
        for (int i = 480; i < 4800 - 960; i++) Assert.Equal(level, samples[i]);                                           // untouched between the ramps
        for (int i = 0; i < 960; i++) Assert.Equal((short)Math.Round(level * (i + 0.5) / 960), samples[4799 - i]);        // 20 ms down
        Assert.InRange(samples[0], (short)0, (short)40); Assert.InRange(samples[^1], (short)0, (short)20);
        Assert.DoesNotContain(short.MaxValue, samples); // the neighbours never got in

        // No fades asked: the first and last samples are the source's own.
        var exact = SliceFixture.Samples(SliceRenderer.Render(source, 1000, 5800), out _, out _);
        Assert.All(exact, sample => Assert.Equal(level, sample));

        // Ramps longer than the slice share it instead of failing.
        var tiny = SliceFixture.Samples(SliceRenderer.Render(source, 1000, 1100, new SliceOptions { FadeInMs = 1000, FadeOutMs = 1000 }), out _, out _);
        Assert.Equal(100, tiny.Length); Assert.True(tiny[50] > tiny[0] && tiny[49] > tiny[99]); Assert.All(tiny, sample => Assert.InRange(sample, (short)0, level));

        Assert.All(SliceFixture.Samples(SliceRenderer.Render(source, 1000, 1100, new SliceOptions { Gain = 0.5 }), out _, out _), sample => Assert.Equal((short)8192, sample));
        Assert.All(SliceFixture.Samples(SliceRenderer.Render(source, 1000, 1100, new SliceOptions { Gain = 4 }), out _, out _), sample => Assert.Equal(short.MaxValue, sample));
        Assert.Throws<InvalidDataException>(() => SliceRenderer.Render(source, 1000, 1100, new SliceOptions { FadeInMs = -1 }));
        Assert.Throws<InvalidDataException>(() => SliceRenderer.Render(source, 1000, 1100, new SliceOptions { Gain = double.NaN }));
    }

    [Fact]
    public void TargetRateAndChannelsConvertOnlyTheSlice()
    {
        // A loud tone outside the range must not ring into a resampled slice of silence: the filter only sees the slice.
        var data = SliceFixture.Tone(16000, 1000, 0.9, 16000); Array.Clear(data, 4000, 4000);
        var quiet = SliceRenderer.RenderSamples(new PcmSliceSource(data, 16000, 1), 4000, 8000, new SliceOptions { TargetRate = 48000, TargetChannels = 2 });
        Assert.Equal((48000, 2, 12000L), (quiet.Rate, quiet.Channels, quiet.Frames));
        Assert.All(quiet.Samples, sample => Assert.Equal(0f, sample));

        var tone = SliceRenderer.RenderSamples(new PcmSliceSource(SliceFixture.Tone(16000, 1000, 0.5, 16000), 16000, 1), 0, 16000, new SliceOptions { TargetRate = 48000, TargetChannels = 2 });
        Assert.Equal(48000, tone.Frames);
        var left = Enumerable.Range(0, 48000).Select(i => tone.Samples[i * 2]).ToArray();
        for (int i = 0; i < 48000; i += 97) Assert.Equal(left[i], tone.Samples[i * 2 + 1]); // mono goes to both channels
        Assert.InRange(SliceFixture.Hertz(left.AsSpan(12000, 24000), 48000), 995, 1005);

        var stereo = new PcmSliceSource(Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 0.25f : 0.75f).ToArray(), 48000, 2);
        Assert.All(SliceFixture.Samples(SliceRenderer.Render(stereo, 10, 60, new SliceOptions { TargetChannels = 1 }), out _, out int channels), sample => Assert.Equal((short)16384, sample));
        Assert.Equal(1, channels);
        // Six channels to stereo keeps the sides apart: even channels left, odd channels right.
        var surround = SliceRenderer.Remix([0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f], 6, 2);
        Assert.Equal(0.3f, surround[0], 5); Assert.Equal(0.4f, surround[1], 5);
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(48000, 16000)]
    [InlineData(16000, 48000)]
    [InlineData(22050, 48000)]
    [InlineData(8000, 44100)]
    public void ResamplingKeepsATonesFrequencyLevelAndTiming(int from, int to)
    {
        const double hertz = 1000, amplitude = 0.5;
        var output = SliceResampler.Resample(SliceFixture.Tone(from, hertz, amplitude, from), 1, from, to);
        Assert.Equal(to, output.Length); // one second in, one second out
        Assert.Equal(SliceResampler.OutputFrames(from, from, to), output.Length);
        var middle = output.AsSpan(to / 4, to / 2);
        Assert.InRange(SliceFixture.Hertz(middle, to), hertz - 5, hertz + 5);
        Assert.InRange(SliceFixture.Rms(middle), amplitude / Math.Sqrt(2) * 0.99, amplitude / Math.Sqrt(2) * 1.01);
        // Zero phase: output frame i is the tone at time i / to. A linear resampler misses this by about 0.01 at 16 kHz.
        double worst = 0;
        for (int i = to / 4; i < to * 3 / 4; i++) worst = Math.Max(worst, Math.Abs(output[i] - amplitude * Math.Sin(2 * Math.PI * hertz * i / to)));
        Assert.InRange(worst, 0, 0.002);
    }

    [Fact]
    public void ResamplingDownRemovesWhatTheNewRateCannotHoldAndKeepsStereoApart()
    {
        // 12 kHz cannot exist at 16 kHz; without a proper filter it would come back as a loud 4 kHz tone.
        var alias = SliceResampler.Resample(SliceFixture.Tone(48000, 12000, 0.5, 48000), 1, 48000, 16000);
        Assert.InRange(SliceFixture.Rms(alias.AsSpan(4000, 8000)), 0, 0.001);

        var stereo = new float[48000 * 2];
        var a = SliceFixture.Tone(48000, 500, 0.4, 48000); var b = SliceFixture.Tone(48000, 2000, 0.2, 48000);
        for (int i = 0; i < 48000; i++) { stereo[i * 2] = a[i]; stereo[i * 2 + 1] = b[i]; }
        var output = SliceResampler.Resample(stereo, 2, 48000, 44100);
        var left = Enumerable.Range(11025, 22050).Select(i => output[i * 2]).ToArray(); var right = Enumerable.Range(11025, 22050).Select(i => output[i * 2 + 1]).ToArray();
        Assert.InRange(SliceFixture.Hertz(left, 44100), 495, 505); Assert.InRange(SliceFixture.Hertz(right, 44100), 1995, 2005);

        var same = new float[] { 0.1f, 0.2f };
        Assert.Same(same, SliceResampler.Resample(same, 1, 48000, 48000));
        Assert.Equal(1, SliceResampler.OutputFrames(1, 48000, 8000)); // audio never rounds away to nothing
    }

    [Fact]
    public void WavBytesRoundTripExactly()
    {
        for (int value = short.MinValue; value <= short.MaxValue; value++) Assert.Equal((short)value, SliceWav.ToPcm16(value / 32768f));
        Assert.Equal(short.MaxValue, SliceWav.ToPcm16(1.5f)); Assert.Equal(short.MinValue, SliceWav.ToPcm16(-1.5f)); Assert.Equal((short)0, SliceWav.ToPcm16(float.NaN));

        var source = SliceFixture.Ramp(22050, 500);
        var wav = SliceRenderer.Render(source, 0, 500);
        Assert.True(SliceWav.TryRead(wav, out var info));
        Assert.Equal((22050, 2, 16, 500L), (info.SampleRate, info.Channels, info.BitsPerSample, info.Frames));
        Assert.Equal(SliceFixture.Samples(wav, out _, out _), SliceWav.Pcm16(wav));
        Assert.Equal(500 * 1000.0 / 22050, info.DurationMs, 6);

        var silence = SliceWav.Silence(4800, 48000, 2);
        Assert.True(SliceWav.IsDigitalSilence(silence)); Assert.Equal(4800 * 2, SliceFixture.Samples(silence, out _, out _).Length);
        Assert.False(SliceWav.IsDigitalSilence(wav));
        var almost = (byte[])silence.Clone(); almost[^1] = 1;
        Assert.False(SliceWav.IsDigitalSilence(almost));
        Assert.False(SliceWav.TryRead([1, 2, 3], out _)); Assert.False(SliceWav.IsDigitalSilence(new byte[64]));
        Assert.Throws<InvalidDataException>(() => SliceWav.Encode([], 48000, 1));
    }
}
