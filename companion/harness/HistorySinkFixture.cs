namespace SoundpadWeb.Testing;

/// <summary>Delivers capture audio straight into a HistoryStore, for diagnostics that run the transcriber without a Bridge.</summary>
public sealed class HistorySinkFixture(HistoryStore store) : IMimicSink
{
    public void Write(ReadOnlySpan<float> mono) => store.Append(mono);
    public void Silence(long frames, double endBehindSeconds) => store.AppendSilence(frames);
    public void Lost(string status) => store.EndRun(HistoryGapReason.DeviceLost);
    public void Drain() => store.Flush(10000);

    /// <summary>A throwaway history (and transcript) in a new temporary folder; delete it with <see cref="Delete"/>.</summary>
    public static (HistoryStore Store, TranscriptWords Words, string Folder) Temporary(int rate, IHistoryFiles? files = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "soundpad-web-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = HistoryStore.Create(folder, new HistoryOptions { Rate = rate, TailSeconds = 600, Files = files });
        return (store, TranscriptWords.Open(folder, store.SessionId), folder);
    }
    public static void Delete(HistoryStore store, string folder)
    {
        store.Close(true);
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
