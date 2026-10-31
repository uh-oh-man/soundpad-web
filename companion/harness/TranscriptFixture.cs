namespace SoundpadWeb.Testing;

/// <summary>Test-only speech engine: every burst of sound becomes one line, "Fixture tone N". Never ships in the EXE.</summary>
public sealed class TranscriptFixture : ITranscriptEngine
{
    private int count;
    public bool Available { get; set; } = true;
    public string Status => "";
    public int Calls { get; private set; }
    public Task Prepare(CancellationToken cancel) { Available = true; return Task.CompletedTask; }
    public Task<IReadOnlyList<TranscribedSpan>> Transcribe(float[] samples, CancellationToken cancel)
    {
        Calls++;
        const int window = MimicTranscriber.SpeechRate / 100;
        var spans = new List<TranscribedSpan>();
        int? start = null;
        for (int i = 0; i <= samples.Length; i += window)
        {
            bool loud = i < samples.Length && samples.Skip(i).Take(window).Any(s => Math.Abs(s) > 0.05f);
            if (loud && start == null) start = i;
            else if (!loud && start is int from)
            {
                spans.Add(new(TimeSpan.FromSeconds((double)from / MimicTranscriber.SpeechRate), TimeSpan.FromSeconds((double)i / MimicTranscriber.SpeechRate), $"Fixture tone {++count}"));
                start = null;
            }
        }
        return Task.FromResult<IReadOnlyList<TranscribedSpan>>(spans);
    }
    public void Dispose() { }
}
