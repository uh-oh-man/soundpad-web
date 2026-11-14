using Xunit;
namespace SoundpadWeb.Tests;

[CollectionDefinition("Speech", DisableParallelization = true)]
public sealed class SpeechCollection;

/// <summary>
/// The 2.38 word-timing pipeline (WordTiming.cs): anchor-valley boundaries on synthetic tones with known edges, the
/// status rule, and one optional check with real Whisper on real speech (skipped where the model is not installed).
/// Runs on its own: a real recognition takes a CPU core for seconds, which would slow the timing-sensitive tests.
/// </summary>
[Collection("Speech")]
public sealed class WordTimingTests
{
    private const int Rate = 16000;
    private static float[] Signal(double totalMs, params (double FromMs, double ToMs, double Hz, float Amplitude)[] parts)
    {
        var samples = new float[(int)(totalMs * Rate / 1000)];
        foreach (var (from, to, hz, amplitude) in parts)
            for (int i = (int)(from * Rate / 1000); i < Math.Min(samples.Length, (int)(to * Rate / 1000)); i++)
                samples[i] += amplitude * MathF.Sin(2 * MathF.PI * (float)hz * i / Rate);
        return samples;
    }
    /// <summary>A Whisper-like token: times in centiseconds, its DTW point inside it.</summary>
    private static TranscribedToken Token(string text, double fromMs, double toMs, double dtwMs, float p = 0.9f) => new(text, (long)(fromMs / 10), (long)(toMs / 10), (long)(dtwMs / 10), p);

    [Fact]
    public void WordsSeparatedByPausesEndWhereThePausesBegin()
    {
        var audio = Signal(1700, (200, 500, 440, 0.5f), (700, 1000, 660, 0.5f), (1150, 1400, 880, 0.5f));
        // Token timestamps are deliberately off by up to 100 ms, as Whisper's are; the DTW points are inside each word.
        TranscribedSpan span = new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(1500), "One two three.", 0.9f,
            [new("[_BEG_]", 0, 0, -1, 1), Token(" One", 260, 560, 380), Token(" two", 640, 1060, 880), Token(" three", 1100, 1500, 1300), new(".", 150, 150, 1330, 0.9f)]);
        var words = WordAligner.Align([span], SpeechEnergy.Analyze(audio));
        Assert.Equal(["One", "two", "three."], words.Select(w => w.Text));
        (double From, double To)[] truth = [(200, 500), (700, 1000), (1150, 1400)];
        for (int i = 0; i < 3; i++)
        {
            Assert.InRange(words[i].StartMs, truth[i].From - 15, truth[i].From + 10);
            Assert.InRange(words[i].EndMs, truth[i].To - 10, truth[i].To + 15);
            Assert.True(words[i].PauseBefore && words[i].PauseAfter);
            Assert.Equal(0, words[i].StretchMs);
            Assert.Equal(WordStatus.Done, WordAligner.Status(words[i].Probability, words[i].StretchMs, false, 60));
        }
    }

    [Fact]
    public void RunTogetherWordsSplitAtTheEnergyValleyNotAtTheTokenTimes()
    {
        // "hello" 300-600 ms and "there" 600-900 ms with no pause, only a 30 ms dip at the boundary.
        var audio = Signal(1300, (300, 585, 300, 0.5f), (585, 615, 300, 0.03f), (615, 900, 500, 0.5f));
        TranscribedSpan span = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(950), "Hello there", 0.9f,
            [Token(" Hello", 320, 700, 480), Token(" there", 700, 950, 740)]);
        var words = WordAligner.Align([span], SpeechEnergy.Analyze(audio));
        Assert.Equal(2, words.Count);
        Assert.InRange(words[0].EndMs, 590, 615); // the valley, not the token boundary at 700 ms
        Assert.Equal(words[0].EndMs, words[1].StartMs);
        Assert.False(words[0].PauseAfter); Assert.False(words[1].PauseBefore);
        Assert.InRange(words[0].StretchMs, 255, 265); // the stretch between the two DTW points
        Assert.InRange(words[0].StartMs, 285, 310); Assert.InRange(words[1].EndMs, 890, 915);
    }

    [Fact]
    public void TheStatusRuleIsHonest()
    {
        Assert.Equal(WordStatus.Done, WordAligner.Status(0.9f, 120, false, 40));
        Assert.Equal(WordStatus.Uncertain, WordAligner.Status(0.45f, 0, false, 40)); // probably the wrong word
        Assert.Equal(WordStatus.Uncertain, WordAligner.Status(0.9f, 300, false, 40)); // boundary guessed over a long stretch
        Assert.Equal(WordStatus.Uncertain, WordAligner.Status(0.9f, 0, true, 40)); // touches a cut that was not at a pause
        Assert.Equal(WordStatus.Uncertain, WordAligner.Status(0.9f, 0, false, 10)); // loud background
        // Phrases without word timing are spread over their span and never claimed as done.
        var spread = WordAligner.Align([new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(900), "No tokens here", 0.8f)], SpeechEnergy.Analyze(new float[Rate]));
        Assert.Equal(["No", "tokens", "here"], spread.Select(w => w.Text));
        Assert.All(spread, w => Assert.Equal(WordStatus.Uncertain, WordAligner.Status(w.Probability, w.StretchMs, false, 40)));
        Assert.Equal(100, spread[0].StartMs); Assert.Equal(900, spread[^1].EndMs, 3);
    }

    [Fact]
    public void LongAudioIsRecognisedInPiecesUnderThirtySeconds()
    {
        // DTW anchors are lost after 30 s of one call (evaluation 3.8), so the engine splits; offsets are restored.
        Assert.True(WhisperEngine.MaxCallSeconds < 30 && WhisperEngine.PieceSeconds <= WhisperEngine.MaxCallSeconds);
        Assert.Equal(250, WhisperEngine.ShortContext(Rate * 2));
        Assert.Equal(1000, WhisperEngine.ShortContext(Rate * 15));
        Assert.Equal(1500, WhisperEngine.ShortContext(Rate * 30));
    }

    /// <summary>Runs only where the Whisper model and the sample recording are on this PC; skipped elsewhere (CI).</summary>
    private sealed class WhisperSampleFactAttribute : FactAttribute
    {
        public static readonly string Model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb", "models", "ggml-base.en.bin");
        /// <summary>whisper.cpp's jfk.wav, which the owner's original Voice Replay Studio folder (Apps\Mimic (Voice Replay Studio)\.tools) carries.</summary>
        public static string? Sample()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Mimic (Voice Replay Studio)", ".tools", "jfk.wav");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
        public WhisperSampleFactAttribute()
        {
            if (!File.Exists(Model)) Skip = "The Whisper model is not installed on this PC.";
            else if (Sample() == null) Skip = "The jfk.wav sample is not on this PC.";
        }
    }

    [WhisperSampleFact]
    public async Task RealWhisperGivesEveryWordAnAnchorAndEdgesFromTheAudio()
    {
        // Read only: the model file and a sample WAV. No device, nothing written outside the test output.
        using var file = new NAudio.Wave.WaveFileReader(WhisperSampleFactAttribute.Sample()!);
        var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(file);
        var mono = new List<float>(); var block = new float[provider.WaveFormat.Channels * 4096]; int read;
        while ((read = provider.Read(block)) > 0)
            for (int i = 0; i + provider.WaveFormat.Channels <= read; i += provider.WaveFormat.Channels) mono.Add(block[i]);
        var speech = MimicTranscriber.Resample([.. mono], provider.WaveFormat.SampleRate, MimicTranscriber.SpeechRate);
        using var engine = new WhisperEngine(WhisperSampleFactAttribute.Model);
        engine.UseMode(SubtitleMode.Gaming);
        var spans = await engine.Transcribe(speech, default);
        Assert.All(spans, s => Assert.NotEmpty(s.Tokens!));
        var words = WordAligner.Align(spans, SpeechEnergy.Analyze(speech));
        Assert.InRange(words.Count, 18, 26);
        for (int i = 1; i < words.Count; i++) Assert.True(words[i].StartMs >= words[i - 1].EndMs - 0.01);
        // "ask" starts near 3.3 s (the evaluation measured 3225 ms with the wide pause search); token times said 2770.
        string dump = string.Join(" ", words.Select(w => $"{w.Text}[{w.StartMs:0}-{w.EndMs:0}]"));
        // (With the window sized to the audio, base.en hears the first "ask" as "asked"; its edges are what matter here.)
        var ask = words.First(w => w.Text.StartsWith("ask", StringComparison.OrdinalIgnoreCase));
        Assert.True(ask.StartMs is >= 3050 and <= 3450, dump);
        var not = words.First(w => w.Text.StartsWith("not", StringComparison.OrdinalIgnoreCase));
        Assert.True(not.StartMs > ask.EndMs && not.EndMs < 4600, dump); // the pause after "ask" is found, not bridged
        var americans = words.First(w => w.Text.StartsWith("Americans", StringComparison.OrdinalIgnoreCase));
        Assert.True(americans.EndMs < 2500, $"Americans ends at {americans.EndMs} ms");
    }
}
