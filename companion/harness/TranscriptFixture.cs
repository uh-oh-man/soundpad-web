namespace SoundpadWeb.Testing;

/// <summary>
/// Test-only speech engine: every burst of sound becomes one phrase, "Fixture tone N", with word timing like
/// Whisper's (2.39): three tokens spread over the burst, each with a DTW point 60 % of the way through it.
/// Never ships in the EXE.
/// </summary>
public sealed class TranscriptFixture : ITranscriptEngine
{
    private int count;
    public bool Available { get; set; } = true;
    public string Status => "";
    public int Calls { get; private set; }
    /// <summary>The last subtitles setting the transcriber passed on.</summary>
    public SubtitleMode Mode { get; private set; }
    public void UseMode(SubtitleMode mode) => Mode = mode;
    public Task Prepare(CancellationToken cancel) { Available = true; return Task.CompletedTask; }
    public Task<IReadOnlyList<TranscribedSpan>> Transcribe(float[] samples, CancellationToken cancel)
    {
        Calls++;
        const int window = MimicTranscriber.SpeechRate / 100;
        var spans = new List<TranscribedSpan>();
        int? start = null;
        for (int i = 0; i <= samples.Length; i += window)
        {
            bool loud = false;
            for (int j = i; j < Math.Min(samples.Length, i + window) && !loud; j++) loud = Math.Abs(samples[j]) > 0.05f;
            if (loud && start == null) start = i;
            else if (!loud && start is int from)
            {
                spans.Add(Phrase(from, i, $"Fixture tone {++count}"));
                start = null;
            }
        }
        return Task.FromResult<IReadOnlyList<TranscribedSpan>>(spans);
    }
    /// <summary>A phrase over samples [from, to) whose words share the burst by length.</summary>
    private static TranscribedSpan Phrase(int from, int to, string text)
    {
        var words = text.Split(' ');
        double total = words.Sum(w => w.Length + 1.5), done = 0, fromCs = from / 160.0, lengthCs = (to - from) / 160.0;
        var tokens = new List<TranscribedToken> { new("[_BEG_]", (long)fromCs, (long)fromCs, -1, 1) };
        foreach (var word in words)
        {
            double s = fromCs + lengthCs * done / total; done += word.Length + 1.5;
            double e = fromCs + lengthCs * done / total;
            tokens.Add(new(" " + word, (long)Math.Round(s), (long)Math.Round(e), (long)Math.Round(s + 0.6 * (e - s)), 0.9f));
        }
        return new(TimeSpan.FromSeconds((double)from / MimicTranscriber.SpeechRate), TimeSpan.FromSeconds((double)to / MimicTranscriber.SpeechRate), text, 0.9f, [.. tokens]);
    }
    public void Dispose() { }
}
