using System.Text.Json;
namespace SoundpadWeb;

/// <summary>
/// One recognised word on the history's absolute frame timeline (2.39). Boundaries are stored unpadded, as
/// computed; padding (20 ms before, 10 ms after) is applied only when audio is cut.
/// </summary>
/// <param name="Id">Stable for the life of the history and never reused.</param>
/// <param name="Start">First frame (capture rate). <paramref name="End"/> is one past the last.</param>
/// <param name="Probability">Lowest token probability in the word.</param>
/// <param name="StretchMs">Longest stretch between DTW points on a side without a pause; 0 when both edges are at a pause.</param>
/// <param name="Status">"done" or "uncertain" (<see cref="WordStatus"/>).</param>
/// <param name="PhraseId">The phrase (Whisper segment) the word belongs to; unique in the history.</param>
/// <param name="Revision">Rises when the stretch is recognised again.</param>
public sealed record TranscriptWord(long Id, long Start, long End, string Text, float Probability, int StretchMs, bool PauseBefore, bool PauseAfter, string Status, long PhraseId, int Revision = 0);
/// <summary>Corrected text for a word, kept apart from it so recognising again never loses it and it never moves a boundary.</summary>
public sealed record WordCorrection(long WordId, string Text, string By, long At);

/// <summary>
/// The transcript of one history session, persisted next to it in &lt;data&gt;\transcripts\&lt;session id&gt;\ (outside the
/// history folder, which HistoryStore owns and refuses to delete with foreign files in it). words.jsonl is
/// append-only: one record per line, {"w":word} or {"c":[start,end]} for a stretch that was recognised (words or
/// not). corrections.json holds the corrections. Damaged lines are skipped on load. Deleted with the history.
/// </summary>
public sealed class TranscriptWords
{
    private sealed record Line(TranscriptWord? W = null, long[]? C = null);
    private readonly object gate = new();
    private readonly List<TranscriptWord> words = [];
    private readonly List<HistoryRange> covered = [];
    private readonly Dictionary<long, WordCorrection> corrections = [];
    private long nextId = 1, nextPhrase = 1;
    private int dropped;
    public string Folder { get; }
    private string WordsPath => Path.Combine(Folder, "words.jsonl");
    private string CorrectionsPath => Path.Combine(Folder, "corrections.json");
    /// <summary>The last write error, if any; the transcript stays in memory.</summary>
    public string? WriteError { get; private set; }

    private TranscriptWords(string folder) { Folder = folder; }

    public static string FolderFor(string root, string sessionId) => Path.Combine(root, "transcripts", sessionId);

    /// <summary>Opens (or starts) the transcript of a history session.</summary>
    public static TranscriptWords Open(string root, string sessionId)
    {
        var result = new TranscriptWords(FolderFor(root, sessionId));
        try
        {
            if (File.Exists(result.WordsPath))
                foreach (var text in File.ReadLines(result.WordsPath))
                {
                    try
                    {
                        var line = JsonSerializer.Deserialize<Line>(text, Json.Options);
                        if (line?.W is { } w && w.End > w.Start && w.Start >= 0 && w.Text.Length is > 0 and <= 200) result.words.Add(w);
                        else if (line?.C is [var s, var e] && e > s && s >= 0) result.AddCovered(new(s, e));
                    }
                    catch (JsonException) { /* a torn last line after a crash */ }
                }
            if (File.Exists(result.CorrectionsPath) && JsonSerializer.Deserialize<WordCorrection[]>(File.ReadAllText(result.CorrectionsPath), Json.Options) is { } fixes)
                foreach (var fix in fixes) result.corrections[fix.WordId] = fix;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { result.WriteError = "The saved transcript could not be read: " + ex.Message; }
        result.words.Sort((a, b) => a.Start.CompareTo(b.Start));
        result.nextId = result.words.Count == 0 ? 1 : result.words.Max(w => w.Id) + 1;
        result.nextPhrase = result.words.Count == 0 ? 1 : result.words.Max(w => w.PhraseId) + 1;
        return result;
    }

    /// <summary>Removes a session's transcript from disk (with its history).</summary>
    public static void Delete(string root, string sessionId)
    {
        try { var folder = FolderFor(root, sessionId); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public long NewPhraseId() { lock (gate) return nextPhrase++; }

    /// <summary>Adds the words recognised in <paramref name="stretch"/> and marks it recognised. Returns them with their ids.</summary>
    public IReadOnlyList<TranscriptWord> Add(IEnumerable<TranscriptWord> recognised, HistoryRange stretch)
    {
        List<TranscriptWord> added;
        lock (gate)
        {
            added = recognised.Select(w => w with { Id = nextId++ }).ToList();
            foreach (var w in added)
            {
                int at = words.FindLastIndex(x => x.Start <= w.Start) + 1;
                words.Insert(at, w);
            }
            if (stretch.EndFrame > stretch.StartFrame) AddCovered(stretch);
            var lines = added.Select(w => Json.Write(new Line(W: w))).ToList();
            if (stretch.EndFrame > stretch.StartFrame) lines.Add(Json.Write(new Line(C: [stretch.StartFrame, stretch.EndFrame])));
            Persist(() => { Directory.CreateDirectory(Folder); File.AppendAllLines(WordsPath, lines); });
        }
        return added;
    }

    private void AddCovered(HistoryRange r)
    {
        covered.Add(r);
        covered.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
        for (int i = covered.Count - 1; i > 0; i--)
            if (covered[i].StartFrame <= covered[i - 1].EndFrame)
            {
                covered[i - 1] = new(covered[i - 1].StartFrame, Math.Max(covered[i - 1].EndFrame, covered[i].EndFrame));
                covered.RemoveAt(i);
            }
    }

    private void Persist(Action write)
    {
        try { write(); WriteError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteError = "The transcript could not be saved: " + ex.Message; }
    }

    /// <summary>Words overlapping [start, end), oldest first, with corrections applied to their text.</summary>
    public IReadOnlyList<(TranscriptWord Word, WordCorrection? Correction)> Between(long start, long end)
    {
        lock (gate)
        {
            var result = new List<(TranscriptWord, WordCorrection?)>();
            foreach (var w in words) if (w.End > start && w.Start < end) result.Add((w, corrections.GetValueOrDefault(w.Id)));
            return result;
        }
    }

    /// <summary>Recognised stretches, merged.</summary>
    public HistoryRange[] Covered { get { lock (gate) return [.. covered]; } }

    /// <summary>Parts of [start, end) not recognised yet.</summary>
    public List<HistoryRange> Missing(long start, long end)
    {
        var result = new List<HistoryRange>();
        lock (gate)
        {
            long at = start;
            foreach (var c in covered)
            {
                if (c.EndFrame <= at) continue;
                if (c.StartFrame >= end) break;
                if (c.StartFrame > at) result.Add(new(at, Math.Min(c.StartFrame, end)));
                at = Math.Max(at, c.EndFrame);
                if (at >= end) break;
            }
            if (at < end) result.Add(new(at, end));
        }
        return result;
    }

    public WordCorrection Correct(long wordId, string text, string by)
    {
        lock (gate)
        {
            if (!words.Any(w => w.Id == wordId)) throw new InvalidDataException("That word is no longer in the transcript.");
            var fix = new WordCorrection(wordId, text, by, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            corrections[wordId] = fix;
            var all = corrections.Values.ToArray();
            Persist(() => { Directory.CreateDirectory(Folder); var temporary = CorrectionsPath + ".tmp"; File.WriteAllText(temporary, Json.Write(all)); File.Move(temporary, CorrectionsPath, true); });
            return fix;
        }
    }

    public TranscriptWord? Find(long wordId) { lock (gate) return words.FirstOrDefault(w => w.Id == wordId); }

    /// <summary>
    /// Forgets words and recognised stretches outside the audio still held (<paramref name="available"/>), so a
    /// transcript never outlives its audio. The file is rewritten once enough has been dropped.
    /// </summary>
    public void Prune(IReadOnlyList<HistoryRange> available)
    {
        lock (gate)
        {
            bool Held(long s, long e) => available.Any(r => s < r.EndFrame && e > r.StartFrame);
            int before = words.Count;
            words.RemoveAll(w => !Held(w.Start, w.End));
            foreach (var id in corrections.Keys.Where(id => !words.Any(w => w.Id == id)).ToArray()) corrections.Remove(id);
            var kept = new List<HistoryRange>();
            foreach (var c in covered)
                foreach (var r in available)
                {
                    long s = Math.Max(c.StartFrame, r.StartFrame), e = Math.Min(c.EndFrame, r.EndFrame);
                    if (s < e) kept.Add(new(s, e));
                }
            bool changed = kept.Count != covered.Count || kept.Where((k, i) => k != covered[i]).Any();
            covered.Clear(); covered.AddRange(kept);
            dropped += before - words.Count;
            if (dropped < 200 && !(changed && dropped > 0)) return;
            dropped = 0;
            var lines = words.Select(w => Json.Write(new Line(W: w))).Concat(covered.Select(c => Json.Write(new Line(C: [c.StartFrame, c.EndFrame])))).ToList();
            Persist(() => { Directory.CreateDirectory(Folder); var temporary = WordsPath + ".tmp"; File.WriteAllLines(temporary, lines); File.Move(temporary, WordsPath, true); });
        }
    }

    /// <summary>
    /// Drops words and recognised stretches that reach <paramref name="end"/> or beyond (audio a crash lost: the history
    /// resumes at its last durable frame and new audio reuses the frames after it), and rewrites the file when anything
    /// changed. A word straddling <paramref name="end"/> goes too: part of its audio is gone.
    /// </summary>
    public void CutAt(long end)
    {
        lock (gate)
        {
            int before = words.Count;
            words.RemoveAll(w => w.End > end);
            foreach (var id in corrections.Keys.Where(id => !words.Any(w => w.Id == id)).ToArray()) corrections.Remove(id);
            var kept = covered.Where(c => c.StartFrame < end).Select(c => new HistoryRange(c.StartFrame, Math.Min(c.EndFrame, end))).ToList();
            bool changed = words.Count != before || kept.Count != covered.Count || kept.Where((k, i) => k != covered[i]).Any();
            covered.Clear(); covered.AddRange(kept);
            if (!changed) return;
            var lines = words.Select(w => Json.Write(new Line(W: w))).Concat(covered.Select(c => Json.Write(new Line(C: [c.StartFrame, c.EndFrame])))).ToList();
            var fixes = corrections.Values.ToArray();
            Persist(() =>
            {
                Directory.CreateDirectory(Folder);
                var temporary = WordsPath + ".tmp"; File.WriteAllLines(temporary, lines); File.Move(temporary, WordsPath, true);
                if (File.Exists(CorrectionsPath)) { var fixesTemporary = CorrectionsPath + ".tmp"; File.WriteAllText(fixesTemporary, Json.Write(fixes)); File.Move(fixesTemporary, CorrectionsPath, true); }
            });
        }
    }

    public int Count { get { lock (gate) return words.Count; } }
}
