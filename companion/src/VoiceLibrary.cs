using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

// The voice library (2.41): every kept clip as a recording with a stable id, where it came from, its words with
// timings, and metadata people edit (voice, tags, favourite, title, corrected text) with an audit trail and Undo.
// See reports/OVERHAUL-PLAN.md, "Voice library, performance and sentences". Its files live in <data>\library\, next
// to (never inside) the kept audio folders, as new versioned files that older companions do not know and ignore:
//   voice-library.v1.json   recordings, voices, wanted words and settings; strictly validated, written atomically,
//                           the previous version kept as voice-library.v1.bak.json
//   voice-words.v1.jsonl    one line per recording's words (append only; a later line replaces an earlier one)
//   voice-audit.v1.jsonl    one line per change (append only)
// Deleting a recording from the library never deletes its file.

/// <summary>One word of a library recording or an indexed sound, in milliseconds from the start of its file.</summary>
public sealed record LibraryWord(
    [property: JsonPropertyName("s")] long StartMs,
    [property: JsonPropertyName("e")] long EndMs,
    [property: JsonPropertyName("t")] string Text,
    [property: JsonPropertyName("p")] float Probability,
    [property: JsonPropertyName("u")] bool Uncertain);

/// <summary>Where a recording came from (provenance).</summary>
public sealed class LibrarySource
{
    /// <summary>"history" (a range of a recording-history session), "upload", "copy" (an edited copy), "vrs" (Voice Replay Studio), "soundpad" (a Soundpad sound added by hand).</summary>
    public string Kind { get; set; } = "upload";
    public string? SessionId { get; set; }
    public long? StartFrame { get; set; }
    public long? EndFrame { get; set; }
    public int? Rate { get; set; }
    public string? VrsId { get; set; }
    public string? SoundId { get; set; }
    public string? SourceAssetId { get; set; }
    public static readonly string[] Kinds = ["history", "upload", "copy", "vrs", "soundpad"];
}

public sealed class LibraryRecording
{
    public string Id { get; set; } = "";
    public long Created { get; set; }
    public long Updated { get; set; }
    public LibrarySource Source { get; set; } = new();
    /// <summary>The managed-audio asset it was kept from, if any.</summary>
    public string? AssetId { get; set; }
    /// <summary>The kept file. Host-only: never sent to anyone.</summary>
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public long DurationMs { get; set; }
    public string Title { get; set; } = "";
    public string? VoiceId { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool Favorite { get; set; }
    /// <summary>Text a person corrected; kept apart from the words and their timings, which it never moves.</summary>
    public string? CorrectedText { get; set; }
    /// <summary>Done in the review queue (or set by hand).</summary>
    public bool Reviewed { get; set; }
    /// <summary>"ready", "pending" (to be transcribed in the background) or "failed".</summary>
    public string WordsState { get; set; } = "pending";
    public string? WordsError { get; set; }
    public string By { get; set; } = "host";
    public string ByName { get; set; } = "Host";
    public static readonly string[] States = ["ready", "pending", "failed"];
}

public sealed class LibraryVoice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7c6cf2";
    public long Created { get; set; }
}

public sealed class WantedWord
{
    public string Id { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string Text { get; set; } = "";
    public string By { get; set; } = "host";
    public string ByName { get; set; } = "Host";
    public long At { get; set; }
}

public sealed record LibrarySettings
{
    /// <summary>Guests may name and tag recordings and voices (with Undo and audit). On by default.</summary>
    public bool GuestLibraryEdit { get; init; } = true;
    /// <summary>"Group speakers (experimental)": off by default (see SpeakerGrouping.cs).</summary>
    public bool GroupSpeakers { get; init; }
    /// <summary>Soundpad categories (by name) whose sounds are transcribed in the background for word search.</summary>
    public List<string> IndexCategories { get; init; } = [];
    public bool IndexPaused { get; init; }
}

public sealed class LibraryFile
{
    public int SchemaVersion { get; set; } = VoiceLibrary.SchemaVersion;
    public List<LibraryRecording> Recordings { get; set; } = [];
    public List<LibraryVoice> Voices { get; set; } = [];
    public List<WantedWord> Wanted { get; set; } = [];
    public LibrarySettings Settings { get; set; } = new();
    public long NextAudit { get; set; } = 1;
}

/// <summary>One change: who changed what, from what to what. Values are JSON. A group is one action (bulk edits share it).</summary>
public sealed record LibraryAudit(long Id, long Group, long At, string By, string ByName, string Target, string TargetId, string Field, string? Before, string? After, long? UndoOf = null);

/// <summary>An edit of one or more recordings; null fields stay as they are.</summary>
public sealed record LibraryEdit(string? Title = null, string? VoiceId = null, bool ClearVoice = false, string[]? Tags = null, string[]? AddTags = null, string[]? RemoveTags = null, bool? Favorite = null, string? CorrectedText = null, bool? Reviewed = null);

/// <summary>Which recordings a list or search shows.</summary>
public sealed record LibraryFilter(string? VoiceId = null, bool Unassigned = false, string? Tag = null, bool? Favorite = null, bool? Reviewed = null, string? Text = null);

public sealed partial class VoiceLibrary
{
    public const int SchemaVersion = 1;
    public const int MaxRecordings = 50000, MaxVoices = 256, MaxWantedPerVoice = 200, MaxTags = 32, MaxTagLength = 40, MaxTitle = 200, MaxText = 4000, MaxWords = 20000, MaxCategories = 256;
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public static readonly string[] Palette = ["#7c6cf2", "#e0699a", "#3fb68b", "#e2a03f", "#4aa3df", "#d9634c", "#a77ee8", "#5cc0c0", "#c9b33a", "#8fbf5a", "#e07fd0", "#6c8cf2"];
    private readonly object gate = new();
    private readonly WordIndex index;
    private readonly Func<long> now;
    private LibraryFile file = new();
    /// <summary>The settings as last loaded or saved, read without the lock (2.41 review: the snapshot reads them).</summary>
    private volatile LibrarySettings settingsNow = new();
    /// <summary>The largest library file a save writes (tests lower it): Load refuses anything larger.</summary>
    internal long SaveLimit { get; init; } = MaxFileBytes;
    /// <summary>The audit file starts over (the old one kept once) when an append takes it past this (tests lower it).</summary>
    internal long AuditLimit { get; init; } = AuditRotateBytes;
    public const long AuditRotateBytes = 32L * 1024 * 1024;
    private readonly Dictionary<string, LibraryRecording> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LibraryWord[]> words = new(StringComparer.Ordinal);
    private readonly List<LibraryAudit> audit = [];
    private long revision;
    private int obsoleteWordLines;
    public string Folder { get; }
    private string MainPath => System.IO.Path.Combine(Folder, "voice-library.v1.json");
    private string BackupPath => System.IO.Path.Combine(Folder, "voice-library.v1.bak.json");
    private string WordsPath => System.IO.Path.Combine(Folder, "voice-words.v1.jsonl");
    private string AuditPath => System.IO.Path.Combine(Folder, "voice-audit.v1.jsonl");
    /// <summary>True when the saved library could not be read (nor its backup): nothing is written over it.</summary>
    public bool Blocked { get; private set; }
    /// <summary>Why it is blocked, or the last write error.</summary>
    public string? Problem { get; private set; }
    public long Revision => Interlocked.Read(ref revision);
    public event Action? Changed;

    public VoiceLibrary(string dataDirectory, WordIndex index, Func<long>? clock = null)
    {
        Folder = System.IO.Path.Combine(dataDirectory, "library");
        this.index = index;
        now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Load();
    }

    private static string DocKey(string id) => "r:" + id;
    public static bool IsRecordingKey(string key) => key.StartsWith("r:", StringComparison.Ordinal);
    public static string RecordingOf(string key) => key[2..];
    public static string NewId() => Guid.NewGuid().ToString("N");
    private static readonly Regex IdPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex ColorPattern = new("^#[0-9a-f]{6}$", RegexOptions.CultureInvariant);
    public static bool ValidId(string? id) => id != null && IdPattern.IsMatch(id);

    // ---- Load, validate, save -------------------------------------------------------------------------------

    private void Load()
    {
        LibraryFile? loaded = null; string? problem = null;
        foreach (var path in new[] { MainPath, BackupPath })
        {
            if (!File.Exists(path)) { if (path == MainPath) continue; else break; }
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("it is larger than 64 MiB");
                var read = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException("it is empty");
                Validate(read);
                loaded = read;
                if (path == BackupPath) problem = "The voice library file was damaged; its previous version was loaded. The damaged file is kept as it was until the next change.";
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
            {
                problem ??= $"The voice library file could not be read ({(ex is InvalidDataException ? ex.Message : ex.GetType().Name)}). It was left untouched.";
            }
        }
        if (loaded == null && File.Exists(MainPath)) { Blocked = true; Problem = problem; return; }
        file = loaded ?? new LibraryFile();
        settingsNow = file.Settings;
        Problem = problem;
        foreach (var r in file.Recordings) byId[r.Id] = r;
        LoadWords();
        LoadAudit();
    }

    private void LoadWords()
    {
        if (!File.Exists(WordsPath)) return;
        try
        {
            int lines = 0;
            foreach (var text in File.ReadLines(WordsPath))
            {
                lines++;
                try
                {
                    var line = JsonSerializer.Deserialize<WordsLine>(text, Json.Options);
                    if (line?.R is not { } id || !byId.ContainsKey(id) || line.W is not { } list || !ValidWords(list)) continue;
                    words[id] = list;
                }
                catch (JsonException) { /* a torn last line after a crash */ }
            }
            obsoleteWordLines = Math.Max(0, lines - words.Count);
            foreach (var id in words.Keys) IndexLocked(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem ??= "The library's words could not be read: " + ex.Message; }
        // A recording marked ready whose words are gone (a damaged line) is transcribed again.
        foreach (var r in file.Recordings) if (r.WordsState == "ready" && !words.ContainsKey(r.Id)) r.WordsState = "pending";
    }
    private sealed record WordsLine(string? R, LibraryWord[]? W);

    private void LoadAudit()
    {
        if (!File.Exists(AuditPath)) return;
        try
        {
            if (new FileInfo(AuditPath).Length > AuditLimit) { RotateAudit(); return; }
            foreach (var text in File.ReadLines(AuditPath))
            {
                try { if (JsonSerializer.Deserialize<LibraryAudit>(text, Json.Options) is { } entry && entry.Id > 0) audit.Add(entry); }
                catch (JsonException) { }
            }
            // The library file may be older than its audit (its backup was loaded): new changes must never reuse an
            // audit id, or Undo would take two unrelated changes for one (2.41 review).
            if (audit.Count > 0) file.NextAudit = Math.Max(file.NextAudit, audit.Max(a => a.Id) + 1);
            if (audit.Count > 2000) audit.RemoveRange(0, audit.Count - 2000);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void RotateAudit()
    {
        try { File.Move(AuditPath, System.IO.Path.Combine(Folder, "voice-audit.v1.old.jsonl"), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool ValidWords(LibraryWord[] list)
    {
        if (list.Length > MaxWords) return false;
        foreach (var w in list)
            if (w == null || w.StartMs < 0 || w.EndMs <= w.StartMs || w.EndMs > 4L * 3600 * 1000 || w.Text is not { Length: > 0 and <= 200 } || !float.IsFinite(w.Probability)) return false;
        return true;
    }

    private static bool CleanText(string? s, int max, bool allowEmpty = false) => s != null && s.Length <= max && (allowEmpty || s.Trim().Length > 0) && !s.Any(char.IsControl);

    /// <summary>Strict: anything out of place refuses the whole file (it is then left untouched).</summary>
    public static void Validate(LibraryFile f)
    {
        void Require(bool ok, string what) { if (!ok) throw new InvalidDataException(what); }
        Require(f.SchemaVersion == SchemaVersion, "unknown schema version");
        Require(f.Recordings != null && f.Voices != null && f.Wanted != null && f.Settings != null, "a section is missing");
        Require(f.Recordings!.Count <= MaxRecordings && f.Voices!.Count <= MaxVoices && f.Wanted!.Count <= MaxVoices * MaxWantedPerVoice, "too many entries");
        Require(f.NextAudit >= 1, "bad audit counter");
        var voices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in f.Voices!)
        {
            Require(v != null && ValidId(v.Id) && voices.Add(v.Id), "bad voice id");
            Require(CleanText(v!.Name, 40) && v.Name == v.Name.Trim() && v.Color != null && ColorPattern.IsMatch(v.Color) && v.Created >= 0, "bad voice");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in f.Recordings)
        {
            Require(r != null && ValidId(r.Id) && ids.Add(r.Id), "bad recording id");
            Require(r!.Source != null && LibrarySource.Kinds.Contains(r.Source.Kind), "bad provenance");
            Require(r.Source!.SessionId is null or { Length: <= 64 } && r.Source.VrsId is null or { Length: <= 64 } && r.Source.SoundId is null or { Length: <= 64 } && r.Source.SourceAssetId is null or { Length: <= 64 }, "bad provenance");
            Require(r.Source.StartFrame is null or >= 0 && (r.Source.EndFrame is null || r.Source.StartFrame is long s && r.Source.EndFrame > s) && r.Source.Rate is null or (>= 8000 and <= 384000), "bad provenance range");
            Require(r.AssetId == null || Guid.TryParse(r.AssetId, out _), "bad asset id");
            Require(r.Path is { Length: > 0 and <= 1024 } && System.IO.Path.IsPathFullyQualified(r.Path) && !r.Path.Any(char.IsControl), "bad path");
            Require(r.Bytes >= 0 && r.DurationMs is >= 0 and <= 4L * 3600 * 1000 && r.Created >= 0 && r.Updated >= 0, "bad size");
            Require(CleanText(r.Title, MaxTitle, true), "bad title");
            Require(r.VoiceId == null || voices.Contains(r.VoiceId), "unknown voice");
            Require(r.Tags != null && r.Tags.Count <= MaxTags && r.Tags.All(t => CleanText(t, MaxTagLength) && t == t.Trim()) && r.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() == r.Tags.Count, "bad tags");
            Require(r.CorrectedText == null || CleanText(r.CorrectedText, MaxText, true), "bad corrected text");
            Require(LibraryRecording.States.Contains(r.WordsState) && r.WordsError is null or { Length: <= 600 }, "bad words state");
            Require(r.By is { Length: > 0 and <= 128 } && CleanText(r.ByName, 40), "bad author");
        }
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in f.Wanted!)
        {
            Require(w != null && ValidId(w.Id) && wanted.Add(w.Id) && voices.Contains(w.VoiceId) && CleanText(w.Text, 100) && WordIndex.Query(w.Text).Length > 0, "bad wanted word");
            Require(w!.By is { Length: > 0 and <= 128 } && CleanText(w.ByName, 40), "bad wanted word author");
        }
        Require(f.Wanted.GroupBy(w => w.VoiceId).All(g => g.Count() <= MaxWantedPerVoice), "too many wanted words");
        Require(f.Settings!.IndexCategories != null && f.Settings.IndexCategories.Count <= MaxCategories && f.Settings.IndexCategories.All(c => c is { Length: > 0 and <= 256 }), "bad categories");
    }

    private void RequireWritable() { if (Blocked) throw new InvalidDataException(Problem ?? "The voice library file could not be read. It was left untouched."); }

    /// <summary>Validates and writes the main file atomically, keeping the previous version as the backup. The caller holds the gate.</summary>
    private void SaveLocked()
    {
        RequireWritable();
        Validate(file);
        Directory.CreateDirectory(Folder);
        var temporary = MainPath + ".tmp";
        // Never a file Load would refuse (2.41 review): the change is refused instead, and the caller puts it back.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json.Options);
        if (bytes.Length > SaveLimit) throw new InvalidDataException($"The voice library would be larger than {SaveLimit / (1024 * 1024)} MiB. Shorten or remove some recordings' text first.");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(MainPath)) File.Replace(temporary, MainPath, BackupPath, true);
            else File.Move(temporary, MainPath);
            if (Problem != null && !Blocked) Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = "The voice library could not be saved: " + ex.Message;
            throw new InvalidDataException(Problem);
        }
        Interlocked.Increment(ref revision);
    }

    private void Append(string path, IEnumerable<string> lines)
    {
        try
        {
            Directory.CreateDirectory(Folder); File.AppendAllLines(path, lines);
            // The audit trail is bounded while the companion runs too (2.41 review), not only at the next launch.
            if (path == AuditPath && new FileInfo(path).Length > AuditLimit) RotateAudit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem = "The voice library could not be saved: " + ex.Message; }
    }

    /// <summary>Listeners hear of a change after the lock is let go (they publish the snapshot, which reads the library).</summary>
    private void Raise()
    {
        Interlocked.Increment(ref revision);
        if (Changed is { } changed) ThreadPool.QueueUserWorkItem(_ => changed());
    }

    // ---- Recordings -----------------------------------------------------------------------------------------

    public LibrarySettings Settings => settingsNow;
    /// <summary>Why a recording cannot be added now (blocked, or full), or null; checked before a clip is kept for the library.</summary>
    public string? CannotAdd
    {
        get
        {
            lock (gate)
                return Blocked ? Problem ?? "The voice library file could not be read. It was left untouched."
                    : file.Recordings.Count >= MaxRecordings ? $"The voice library holds at most {MaxRecordings} recordings." : null;
        }
    }
    public int Count { get { lock (gate) return file.Recordings.Count; } }
    public LibraryVoice[] Voices { get { lock (gate) return [.. file.Voices]; } }
    public WantedWord[] Wanted { get { lock (gate) return [.. file.Wanted]; } }

    public LibraryRecording? Get(string id) { lock (gate) return byId.GetValueOrDefault(id); }
    public LibraryWord[] WordsOf(string id) { lock (gate) return words.GetValueOrDefault(id) ?? []; }

    /// <summary>
    /// Corrected text applied word by word when it has exactly as many words as were recognised (a word fixed in
    /// place); otherwise the recognised words stand and the correction is searchable as text only. Timings never move.
    /// </summary>
    private static string[]? Aligned(string? corrected, LibraryWord[] list)
    {
        if (string.IsNullOrWhiteSpace(corrected) || list.Length == 0) return null;
        var pieces = corrected.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length == list.Length && pieces.All(p => p.Length <= 200) ? pieces : null;
    }
    /// <summary>The words as people see and search them (corrections applied where they line up).</summary>
    public LibraryWord[] DisplayWords(string id)
    {
        lock (gate)
        {
            var list = words.GetValueOrDefault(id) ?? [];
            var aligned = byId.TryGetValue(id, out var r) ? Aligned(r.CorrectedText, list) : null;
            return aligned == null ? list : [.. list.Select((w, i) => w with { Text = aligned[i] })];
        }
    }
    private void IndexLocked(string id)
    {
        if (!byId.TryGetValue(id, out var r) || !words.TryGetValue(id, out var list)) { index.Remove(DocKey(id)); return; }
        index.Set(DocKey(id), Aligned(r.CorrectedText, list) ?? [.. list.Select(w => w.Text)]);
    }
    /// <summary>Updates the word index for a recording whose corrected text changed (only that one).</summary>
    public void Reindex(string id) { lock (gate) IndexLocked(id); }
    public LibraryRecording? FindByAsset(string assetId) { lock (gate) return file.Recordings.FirstOrDefault(r => r.AssetId == assetId); }
    public LibraryRecording? FindByPath(string path) { lock (gate) return file.Recordings.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)); }

    /// <summary>
    /// Adds a recording. Its words go in at once when they are known (copied from the history transcript); otherwise
    /// it is "pending" and the background worker transcribes its file. Idempotent per asset and per file.
    /// </summary>
    public (LibraryRecording Recording, bool Added) Add(LibraryRecording recording, IReadOnlyList<LibraryWord>? known, string by, string byName)
    {
        lock (gate)
        {
            RequireWritable();
            var existing = recording.AssetId != null ? file.Recordings.FirstOrDefault(r => r.AssetId == recording.AssetId) : null;
            existing ??= file.Recordings.FirstOrDefault(r => string.Equals(r.Path, recording.Path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return (existing, false);
            if (file.Recordings.Count >= MaxRecordings) throw new InvalidDataException($"The voice library holds at most {MaxRecordings} recordings.");
            long at = now();
            recording.Id = ValidId(recording.Id) ? recording.Id : NewId();
            recording.Created = recording.Updated = at;
            recording.Title = Clip(recording.Title, MaxTitle);
            recording.By = by; recording.ByName = byName;
            var list = known is { Count: > 0 } && ValidWords([.. known]) ? known.ToArray() : null;
            recording.WordsState = list != null ? "ready" : "pending";
            file.Recordings.Add(recording); byId[recording.Id] = recording;
            long first = Reserve(1);
            try { SaveLocked(); }
            catch { file.Recordings.Remove(recording); byId.Remove(recording.Id); file.NextAudit = first; throw; }
            if (list != null) StoreWordsLocked(recording.Id, list);
            Record(first, by, byName, [new("recording", recording.Id, "record", null, Json.Write(recording))]);
            Raise();
            return (recording, true);
        }
    }

    private static string Clip(string text, int max)
    {
        var clean = new string((text ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length > max ? clean[..max].TrimEnd() : clean;
    }

    private void StoreWordsLocked(string id, LibraryWord[] list)
    {
        if (words.ContainsKey(id)) obsoleteWordLines++;
        words[id] = list;
        IndexLocked(id);
        Append(WordsPath, [Json.Write(new WordsLine(id, list))]);
        CompactWordsIfDue();
    }

    /// <summary>The words file only grows; once most of its lines are replaced or removed, it is rewritten once.</summary>
    private void CompactWordsIfDue()
    {
        if (obsoleteWordLines < Math.Max(500, words.Count)) return;
        try
        {
            var temporary = WordsPath + ".tmp";
            File.WriteAllLines(temporary, words.Where(p => byId.ContainsKey(p.Key)).Select(p => Json.Write(new WordsLine(p.Key, p.Value))));
            File.Move(temporary, WordsPath, true);
            obsoleteWordLines = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The background worker's result for a recording still pending.</summary>
    public void SetWords(string id, IReadOnlyList<LibraryWord> list)
    {
        lock (gate)
        {
            if (Blocked || !byId.TryGetValue(id, out var r)) return;
            var array = list.Where(w => w.EndMs > w.StartMs && w.Text.Length is > 0 and <= 200).Take(MaxWords).ToArray();
            r.WordsState = "ready"; r.WordsError = null; r.Updated = now();
            try { SaveLocked(); } catch (InvalidDataException) { return; }
            StoreWordsLocked(id, array);
            Raise();
        }
    }
    public void SetWordsFailed(string id, string error)
    {
        lock (gate)
        {
            if (Blocked || !byId.TryGetValue(id, out var r)) return;
            r.WordsState = "failed"; r.WordsError = error.Length > 600 ? error[..600] : error;
            try { SaveLocked(); } catch (InvalidDataException) { return; }
            Raise();
        }
    }
    /// <summary>Asks for a recording's words again (a failed transcription, say).</summary>
    public void Retranscribe(string id)
    {
        lock (gate)
        {
            if (Blocked || !byId.TryGetValue(id, out var r) || r.WordsState == "pending") return;
            r.WordsState = "pending"; r.WordsError = null;
            SaveLocked(); Raise();
        }
    }
    /// <summary>The oldest recording still waiting for its words, skipping <paramref name="skip"/>.</summary>
    public LibraryRecording? NextPending(ISet<string>? skip = null)
    {
        lock (gate) return file.Recordings.FirstOrDefault(r => r.WordsState == "pending" && (skip == null || !skip.Contains(r.Id)));
    }
    public int PendingCount { get { lock (gate) return file.Recordings.Count(r => r.WordsState == "pending"); } }
}
