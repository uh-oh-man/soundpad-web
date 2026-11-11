using Xunit;
namespace SoundpadWeb.Tests;

public sealed class SliceAssemblyTests
{
    private static short[] Wav(IReadOnlyList<SliceItem> items, SliceAssemblyOptions? options = null) => SliceFixture.Samples(SliceAssembly.Render(items, options), out _, out _);

    [Fact]
    public void PiecesLandOnTheExactOutputFrame()
    {
        var source = SliceFixture.Mono(48000, 20000);
        var samples = Wav([new SlicePiece(source, 100, 300, 0), new SlicePiece(source, 5000, 5100, 12.5)]); // 12.5 ms = frame 600
        Assert.Equal(700, samples.Length);
        for (int i = 0; i < 200; i++) Assert.Equal(SliceFixture.Low(100 + i), samples[i]);
        for (int i = 200; i < 600; i++) Assert.Equal(0, samples[i]);
        for (int i = 0; i < 100; i++) Assert.Equal(SliceFixture.Low(5000 + i), samples[600 + i]);

        // Offsets round like every other boundary, at any rate: 33.3 ms at 44.1 kHz is frame 1,469 (1,468.53).
        var odd = SliceFixture.Mono(44100, 20000);
        var output = SliceAssembly.RenderSamples([new SlicePiece(odd, 7, 57, 33.3), new SlicePiece(odd, 900, 950, 0.4)]);
        Assert.Equal((44100, 1), (output.Audio.Rate, output.Audio.Channels));
        Assert.Equal((1469L, 1519L, false), (output.Placements[0].StartFrame, output.Placements[0].EndFrame, output.Placements[0].Cut));
        Assert.Equal((18L, 68L), (output.Placements[1].StartFrame, output.Placements[1].EndFrame)); // 17.64 rounds to 18
        var placed = SliceFixture.Samples(output.Audio.ToWav(), out _, out _);
        Assert.Equal(1519, placed.Length);
        Assert.Equal(SliceFixture.Low(7), placed[1469]); Assert.Equal(0, placed[1468]); Assert.Equal(SliceFixture.Low(56), placed[1518]);
        Assert.Equal(0, placed[17]); Assert.Equal(SliceFixture.Low(900), placed[18]); Assert.Equal(SliceFixture.Low(949), placed[67]); Assert.Equal(0, placed[68]);
        Assert.False(output.Limited);
    }

    [Fact]
    public void PiecesThatTouchAreJoinedSampleForSample()
    {
        var source = SliceFixture.Mono(48000, 20000);
        // The second piece starts exactly where the first ends: no overlap, so no crossfade and no gap.
        var samples = Wav([new SlicePiece(source, 0, 100, 0), new SlicePiece(source, 2000, 2100, SliceRenderer.MsOf(100, 48000))]);
        Assert.Equal(200, samples.Length);
        for (int i = 0; i < 100; i++) { Assert.Equal(SliceFixture.Low(i), samples[i]); Assert.Equal(SliceFixture.Low(2000 + i), samples[100 + i]); }
    }

    [Fact]
    public void ALaterStartingItemCutsTheEarlierOneOff()
    {
        var source = SliceFixture.Mono(48000, 20000);
        var hard = new SliceAssemblyOptions { CrossfadeMs = 0 };
        double at400 = SliceRenderer.MsOf(400, 48000);
        var early = new SlicePiece(source, 0, 1000, 0); var late = new SlicePiece(source, 5000, 5200, at400);
        foreach (var items in new SliceItem[][] { [early, late], [late, early] }) // list order does not decide; the start does
        {
            var output = SliceAssembly.RenderSamples(items, hard);
            var samples = SliceFixture.Samples(output.Audio.ToWav(), out _, out _);
            // Not mixed, and the earlier piece does not come back after the later one ends.
            Assert.Equal(600, samples.Length);
            for (int i = 0; i < 400; i++) Assert.Equal(SliceFixture.Low(i), samples[i]);
            for (int i = 0; i < 200; i++) Assert.Equal(SliceFixture.Low(5000 + i), samples[400 + i]);
            var cut = output.Placements[Array.IndexOf(items, early)];
            Assert.True(cut.Cut); Assert.Equal((0L, 400L), (cut.StartFrame, cut.EndFrame));
            Assert.False(output.Placements[Array.IndexOf(items, late)].Cut);
        }

        // Items that start together: the later one in the list wins outright.
        var together = Wav([new SlicePiece(source, 0, 1000, 0), new SlicePiece(source, 5000, 5200, 0)], hard);
        Assert.Equal(200, together.Length);
        for (int i = 0; i < 200; i++) Assert.Equal(SliceFixture.Low(5000 + i), together[i]);

        // A pause cuts too, and three in a row each cut the one before.
        var chain = Wav([new SlicePiece(source, 0, 1000, 0), new SlicePause(SliceRenderer.MsOf(300, 48000), SliceRenderer.MsOf(100, 48000)), new SlicePiece(source, 7000, 7050, SliceRenderer.MsOf(350, 48000))], hard);
        Assert.Equal(400, chain.Length);
        for (int i = 0; i < 300; i++) Assert.Equal(SliceFixture.Low(i), chain[i]);
        for (int i = 300; i < 350; i++) Assert.Equal(0, chain[i]);
        for (int i = 0; i < 50; i++) Assert.Equal(SliceFixture.Low(7000 + i), chain[350 + i]);
    }

    [Fact]
    public void TheCutIsCrossfadedSoItDoesNotClick()
    {
        const short high = 16384, low = -8192;
        var a = SliceFixture.Constant(48000, 2000, 0.5f); var b = SliceFixture.Constant(48000, 2000, -0.25f);
        var items = new SliceItem[] { new SlicePiece(a, 0, 1000, 0), new SlicePiece(b, 0, 500, SliceRenderer.MsOf(400, 48000)) };
        var samples = Wav(items); // default crossfade: 4 ms = 192 frames
        Assert.Equal(900, samples.Length);
        for (int i = 0; i < 400; i++) Assert.Equal(high, samples[i]);                 // the earlier piece, untouched up to the cut
        for (int i = 0; i < 192; i++)
        {
            double t = (i + 0.5) / 192, expected = high * (1 - t) + low * t;          // earlier fades out as the later fades in
            Assert.InRange(samples[400 + i], expected - 1, expected + 1);
        }
        for (int i = 592; i < 900; i++) Assert.Equal(low, samples[i]);                // then only the later piece
        int step = Enumerable.Range(1, 899).Max(i => Math.Abs(samples[i] - samples[i - 1]));
        Assert.InRange(step, 0, 130);                                                 // a ramp, where the hard cut jumps 24,576
        var hardCut = Wav(items, new SliceAssemblyOptions { CrossfadeMs = 0 });
        Assert.Equal(24576, Enumerable.Range(1, 899).Max(i => Math.Abs(hardCut[i] - hardCut[i - 1])));

        // The crossfade never outlasts the item that did the cutting. Here that item lasts 48 frames before a third
        // cuts it in turn, so by frame 448 the first piece is gone and only the second and third are crossfading.
        var brief = Wav([new SlicePiece(a, 0, 1000, 0), new SlicePiece(b, 0, 500, SliceRenderer.MsOf(400, 48000)), new SlicePiece(a, 0, 100, SliceRenderer.MsOf(448, 48000))]);
        Assert.Equal(548, brief.Length);
        for (int i = 0; i < 100; i++)
        {
            double t = (i + 0.5) / 100, expected = low * (1 - t) + high * t;
            Assert.InRange(brief[448 + i], expected - 1, expected + 1);
        }
    }

    [Fact]
    public void PausesAndBleepsHaveExactLengths()
    {
        // With no pieces the output is 48 kHz mono.
        var pause = SliceFixture.Samples(SliceAssembly.Render([new SlicePause(0, 250)]), out int rate, out int channels);
        Assert.Equal((48000, 1, 12000), (rate, channels, pause.Length));
        Assert.All(pause, sample => Assert.Equal(0, sample));

        var options = new SliceAssemblyOptions { Rate = 44100, Channels = 2 };
        var samples = SliceFixture.Samples(SliceAssembly.Render([new SliceBleep(100, 300), new SlicePause(400, 50)], options), out rate, out channels);
        Assert.Equal((44100, 2), (rate, channels));
        Assert.Equal((4410 + 13230 + 2205) * 2, samples.Length);
        var left = Enumerable.Range(0, samples.Length / 2).Select(i => samples[i * 2]).ToArray();
        for (int i = 0; i < samples.Length / 2; i++) Assert.Equal(left[i], samples[i * 2 + 1]);
        Assert.All(left[..4410], sample => Assert.Equal(0, sample));
        Assert.All(left[(4410 + 13230)..], sample => Assert.Equal(0, sample));
        var tone = SliceFixture.Floats(left[4410..(4410 + 13230)]);
        Assert.InRange(SliceFixture.Hertz(tone.AsSpan(3000, 8820), 44100), 995, 1005);            // 1 kHz by default
        Assert.InRange(tone.Max(), 0.2f * 0.99f, 0.2f * 1.01f);                                    // default level
        // 5 ms ramps (220.5 -> 221 frames) at both ends: it starts and ends near zero and stays under the ramp.
        for (int i = 0; i < 221; i++)
        {
            float bound = 0.2f * (i + 0.5f) / 221 + 0.0001f;
            Assert.InRange(Math.Abs(tone[i]), 0, bound); Assert.InRange(Math.Abs(tone[^(i + 1)]), 0, bound);
        }

        var custom = SliceAssembly.RenderSamples([new SliceBleep(0, 200) { FrequencyHz = 440, Level = 0.5, RampMs = 0 }]).Audio;
        Assert.Equal(9600, custom.Frames);
        Assert.InRange(SliceFixture.Hertz(custom.Samples, 48000), 430, 450); Assert.InRange(custom.Samples.Max(), 0.495f, 0.5f);
        Assert.Throws<InvalidDataException>(() => SliceAssembly.Render([new SliceBleep(0, 100) { FrequencyHz = 30000 }]));
        Assert.Throws<InvalidDataException>(() => SliceAssembly.Render([]));
        Assert.Throws<InvalidDataException>(() => SliceAssembly.Render([new SlicePause(0, 0)]));
        Assert.Throws<InvalidDataException>(() => SliceAssembly.Render([new SlicePause(-5, 10)]));
    }

    [Fact]
    public void PiecesFromDifferentRatesAreResampledToOne()
    {
        var slow = new PcmSliceSource(SliceFixture.Tone(16000, 1000, 0.4, 16000), 16000, 1);
        var fast = new PcmSliceSource(SliceFixture.Tone(48000, 1000, 0.4, 48000), 48000, 1);
        // Half a second of each. The output takes the higher rate unless told otherwise.
        var output = SliceAssembly.RenderSamples([new SlicePiece(slow, 0, 8000, 0), new SlicePiece(fast, 0, 24000, 500)]).Audio;
        Assert.Equal((48000, 48000L), (output.Rate, output.Frames));
        Assert.InRange(SliceFixture.Hertz(output.Samples.AsSpan(6000, 12000), 48000), 995, 1005);
        Assert.InRange(SliceFixture.Hertz(output.Samples.AsSpan(30000, 12000), 48000), 995, 1005);
        Assert.InRange(SliceFixture.Rms(output.Samples.AsSpan(6000, 12000)), 0.4 / Math.Sqrt(2) * 0.99, 0.4 / Math.Sqrt(2) * 1.01);

        var chosen = SliceAssembly.RenderSamples([new SlicePiece(slow, 0, 8000, 0), new SlicePiece(fast, 0, 24000, 500)], new SliceAssemblyOptions { Rate = 44100 });
        Assert.Equal((44100, 44100L), (chosen.Audio.Rate, chosen.Audio.Frames));
        Assert.Equal((22050L, 44100L), (chosen.Placements[1].StartFrame, chosen.Placements[1].EndFrame));
        Assert.InRange(SliceFixture.Hertz(chosen.Audio.Samples.AsSpan(5000, 11025), 44100), 995, 1005);
        Assert.InRange(SliceFixture.Hertz(chosen.Audio.Samples.AsSpan(27000, 11025), 44100), 995, 1005);
    }

    [Fact]
    public void BalancingBringsPiecesFromDifferentRecordingsToOneLevel()
    {
        const int rate = 48000;
        var loud = new PcmSliceSource(SliceFixture.Tone(rate, 300, 0.5, rate), rate, 1);    // about -9 dBFS
        var quiet = new PcmSliceSource(SliceFixture.Tone(rate, 500, 0.03, rate), rate, 1);  // about -33.5 dBFS
        SliceItem[] items = [new SlicePiece(loud, 0, rate, 0), new SlicePiece(quiet, 0, rate, 1000)];
        double Level(short[] samples, int second) => SliceFixture.Db(SliceFixture.Rms(SliceFixture.Floats(samples[(second * rate + 4800)..((second + 1) * rate - 4800)])));

        var plain = Wav(items);
        Assert.InRange(Level(plain, 0) - Level(plain, 1), 23, 26); // unbalanced they are 24 dB apart

        var output = SliceAssembly.RenderSamples(items, new SliceAssemblyOptions { Balance = true });
        var balanced = SliceFixture.Samples(output.Audio.ToWav(), out _, out _);
        Assert.InRange(Math.Abs(Level(balanced, 0) - Level(balanced, 1)), 0, 1);
        Assert.InRange(Level(balanced, 0), -21, -19); Assert.InRange(Level(balanced, 1), -21, -19); // the default target
        Assert.InRange(balanced.Max(s => Math.Abs((int)s)), 1, 32000); Assert.False(output.Limited); // nowhere near clipping
        Assert.InRange(output.Placements[0].LoudnessDb!.Value, -9.5, -8.5); Assert.InRange(output.Placements[1].LoudnessDb!.Value, -34, -33);
        Assert.InRange(output.Placements[1].Gain, 4, 5.2); // about +13.5 dB

        // Per piece: opt out, opt in while the sentence is not balanced, and a manual trim on top of balancing.
        var optOut = Wav([new SlicePiece(loud, 0, rate, 0), new SlicePiece(quiet, 0, rate, 1000) { Balance = false }], new SliceAssemblyOptions { Balance = true });
        Assert.InRange(Level(optOut, 1), -34, -33);
        var optIn = Wav([new SlicePiece(loud, 0, rate, 0), new SlicePiece(quiet, 0, rate, 1000) { Balance = true }]);
        Assert.InRange(Level(optIn, 0), -9.5, -8.5); Assert.InRange(Level(optIn, 1), -21, -19);
        var trimmed = Wav([new SlicePiece(loud, 0, rate, 0) { Gain = 2 }, new SlicePiece(quiet, 0, rate, 1000)], new SliceAssemblyOptions { Balance = true });
        Assert.InRange(Level(trimmed, 0) - Level(trimmed, 1), 5, 7);
        var target = Wav(items, new SliceAssemblyOptions { Balance = true, TargetDb = -26 });
        Assert.InRange(Level(target, 0), -27, -25);
        // A very quiet piece is raised by at most MaxBoostDb, not all the way.
        var capped = Wav(items, new SliceAssemblyOptions { Balance = true, MaxBoostDb = 6 });
        Assert.InRange(Level(capped, 1), -28, -27);
    }

    [Fact]
    public void LoudnessCountsOnlyTheVoicedPart()
    {
        const int rate = 48000;
        var tone = SliceFixture.Tone(rate, 300, 0.5, rate);
        var gappy = (float[])tone.Clone(); Array.Clear(gappy, rate / 4, rate / 2); // half of it is a pause
        double whole = SliceLoudness.EstimateDb(new PcmSliceSource(tone, rate, 1), 0, rate)!.Value;
        double voiced = SliceLoudness.EstimateDb(new PcmSliceSource(gappy, rate, 1), 0, rate)!.Value;
        Assert.InRange(whole, -9.2, -8.9); Assert.InRange(Math.Abs(whole - voiced), 0, 0.3); // plain RMS would say 3 dB quieter
        Assert.Null(SliceLoudness.EstimateDb(SliceFixture.Constant(rate, 4800, 0), 0, 4800));
        Assert.Throws<SliceRangeException>(() => SliceLoudness.EstimateDb(SliceFixture.Constant(rate, 4800, 0), 0, 4801));
        // Silence has nothing to balance: it is left alone instead of being amplified.
        var output = SliceAssembly.RenderSamples([new SlicePiece(SliceFixture.Constant(rate, 4800, 0.0001f), 0, 4800, 0)], new SliceAssemblyOptions { Balance = true });
        Assert.Equal(1.0, output.Placements[0].Gain); Assert.Null(output.Placements[0].LoudnessDb);
        // A loudness the caller already knows is used as given.
        var known = SliceAssembly.RenderSamples([new SlicePiece(new PcmSliceSource(tone, rate, 1), 0, rate, 0) { LoudnessDb = -30 }], new SliceAssemblyOptions { Balance = true });
        Assert.Equal(Math.Pow(10, 0.5), known.Placements[0].Gain, 6);
    }

    [Fact]
    public void PeaksAreTurnedDownSmoothlyInsteadOfClipping()
    {
        const int rate = 48000;
        var tone = SliceFixture.Tone(rate, 200, 0.9, rate);
        var output = SliceAssembly.RenderSamples([new SlicePiece(new PcmSliceSource(tone, rate, 1), 0, rate, 0) { Gain = 3 }]); // 2.7 times full scale
        Assert.True(output.Limited);
        Assert.InRange(output.Audio.Samples.Max(v => Math.Abs(v)), 0.9f, 1.0f);
        var samples = SliceFixture.Samples(output.Audio.ToWav(), out _, out _);
        // A clipped sine sits on the rail for most of each half cycle; a limited one only touches it at its peaks.
        Assert.InRange(samples.Count(s => Math.Abs((int)s) >= 32767), 0, rate / 50);
        double dot = 0, a = 0, b = 0;
        for (int i = rate / 4; i < rate * 3 / 4; i++) { dot += tone[i] * (double)output.Audio.Samples[i]; a += tone[i] * (double)tone[i]; b += output.Audio.Samples[i] * (double)output.Audio.Samples[i]; }
        Assert.InRange(dot / Math.Sqrt(a * b), 0.995, 1.0001); // still the same sine, only quieter

        // Audio under the ceiling is not touched at all, and a lone peak only affects its neighbourhood.
        var quiet = SliceFixture.Mono(rate, 20000);
        var untouched = SliceAssembly.RenderSamples([new SlicePiece(quiet, 0, 20000, 0)]);
        Assert.False(untouched.Limited);
        var spiky = SliceFixture.Tone(rate, 200, 0.5, rate); spiky[24000] = 3f;
        var limited = (float[])spiky.Clone();
        Assert.True(SliceAssembly.Limit(limited, 1, rate));
        Assert.InRange(limited.Max(v => Math.Abs(v)), 0.99f, 1.0f);
        for (int i = 0; i < rate; i++) if (Math.Abs(i - 24000) > 600) Assert.Equal(spiky[i], limited[i]); // beyond about 10 ms either side
        Assert.InRange(Math.Abs(limited[24000]), 0.99f, 1.0f);

        var lower = (float[])tone.Clone();
        Assert.True(SliceAssembly.Limit(lower, 1, rate, ceiling: 0.5));
        Assert.InRange(lower.Max(v => Math.Abs(v)), 0.4f, 0.5f);
    }
}
