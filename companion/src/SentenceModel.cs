using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

// Sentences, the speaking palette's named pieces and performance sets (2.42). See reports/OVERHAUL-PLAN.md, "Voice
// library, performance and sentences". Everything a sentence says comes from recordings in the voice library: a
// piece is a range of a library recording (a word, a phrase, a named nonverbal piece such as [laugh], or the parts of
// a splice a person approved). Pauses, gaps and censor bleeps are the only audio that is made, never speech.
// Persistence: <data>\library\sentences.v1.json (strictly validated, written atomically, the previous version kept as
// sentences.v1.bak.json) and each kept version's rendered audio in <data>\library\sentences\<id>\v<N>-<hash>.wav,
// written once and never changed. Older companions do not know these files and ignore them.

/// <summary>A range of a library recording, in milliseconds of its file.</summary>
public sealed class SentenceSource
{
    public string RecordingId { get; set; } = "";
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    /// <summary>The recording's voice when it was chosen (for colour and filters; the recording's own voice may change later).</summary>
    public string? VoiceId { get; set; }
    /// <summary>For a splice part: which letters of which recorded word it is ("sun" of "sunday").</summary>
    public string? Label { get; set; }
}

/// <summary>Effects on one piece or on the whole sentence (the edited-copy effects, ManagedAudio's AudioEffects).</summary>
public sealed class PieceEffect
{
    public bool Reverse { get; set; }
    public double Speed { get; set; } = 1;
    public bool KeepPitch { get; set; }
    public bool Echo { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsNone => !Reverse && Speed == 1 && !Echo;
}

public sealed class SentencePiece
{
    public string Id { get; set; } = "";
    /// <summary>"word" (recorded words or a phrase), "named" (a nonverbal piece), "missing", "pause" or "splice".</summary>
    public string Kind { get; set; } = "word";
    /// <summary>The words of the sentence text this piece stands for, [TokenStart, TokenEnd). Null for a piece a person inserted.</summary>
    public int? TokenStart { get; set; }
    public int? TokenEnd { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Where it starts on the timeline.</summary>
    public double OffsetMs { get; set; }
    /// <summary>A person placed it (moved it, set its edges, inserted it): it stays where it is.</summary>
    public bool Manual { get; set; }
    public SentenceSource? Source { get; set; }
    /// <summary>The parts of an approved splice, played one after another.</summary>
    public List<SentenceSource>? Parts { get; set; }
    /// <summary>A similar recorded word a person chose for a missing one (shown as such; never chosen automatically).</summary>
    public string? Substitute { get; set; }
    public string? NamedId { get; set; }
    /// <summary>A person picked this take (Swap, an alternative): Rematch keeps it.</summary>
    public bool Chosen { get; set; }
    public double FadeInMs { get; set; } = 5;
    public double FadeOutMs { get; set; } = 8;
    /// <summary>Manual volume (1 = as recorded). Null: balanced with the others (when Balance volumes is on).</summary>
    public double? Gain { get; set; }
    /// <summary>Length of a pause or a missing word's gap or bleep.</summary>
    public double DurationMs { get; set; }
    /// <summary>A missing word: "gap" (silence) or "bleep" (a censor tone).</summary>
    public string? Missing { get; set; }
    public PieceEffect? Effect { get; set; }
    /// <summary>The pause after these words in their recording, for "original timing".</summary>
    public long GapAfterMs { get; set; }
    /// <summary>
    /// 2.45: a template's voice slot or a phrase block chose this voice for these words, so every later choice (the
    /// mix, Swap, Shuffle, Find missing again, similar words) stays within it. Null (and left out of the file, so
    /// older sentences are written byte for byte as before): the sentence's own voice mix applies.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceLock { get; set; }
}

public sealed class SentenceSettings
{
    /// <summary>Smooth (0) to Chaotic (100): how often the automatic choice switches voices and splits phrases.</summary>
    public int Chaos { get; set; } = 45;
    public int Seed { get; set; }
    /// <summary>Shuffles so far: each one draws new choices from the same seed.</summary>
    public int Shuffles { get; set; }
    /// <summary>"fluent" (short even joins) or "original" (the pauses the words had in their recordings).</summary>
    public string Timing { get; set; } = "fluent";
    public bool Balance { get; set; } = true;
    /// <summary>Gentle cleanup of piece edges and noise between words; the recordings themselves are never changed.</summary>
    public bool Cleanup { get; set; }
    /// <summary>Altered takes (reversed, sped-up or effect copies in the library) may be chosen automatically.</summary>
    public bool Altered { get; set; }
    /// <summary>The voices the automatic choice mixes (none = every voice).</summary>
    public List<string> VoiceIds { get; set; } = [];
    /// <summary>An effect on the whole sentence.</summary>
    public PieceEffect? Effect { get; set; }
}

/// <summary>A source recording's revision a kept version used: its id, its file's content hash and the frames taken.</summary>
public sealed class PinnedSource
{
    public string PieceId { get; set; } = "";
    public string RecordingId { get; set; } = "";
    public string Hash { get; set; } = "";
    public long StartFrame { get; set; }
    public long EndFrame { get; set; }
    public int Rate { get; set; }
}

public sealed class SentenceVersion
{
    public int N { get; set; }
    public long At { get; set; }
    public string By { get; set; } = "host";
    public string ByName { get; set; } = "Host";
    public string Text { get; set; } = "";
    public List<SentencePiece> Pieces { get; set; } = [];
    public SentenceSettings Settings { get; set; } = new();
    /// <summary>The rendered audio's file name in the sentence's folder (never rewritten).</summary>
    public string File { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Bytes { get; set; }
    public long DurationMs { get; set; }
    public List<PinnedSource> Sources { get; set; } = [];
}

public sealed class SentenceProject
{
    public string Id { get; set; } = "";
    /// <summary>Whose project it is (the host, or a guest's participant id). Unkept projects are only theirs.</summary>
    public string Owner { get; set; } = "host";
    public string OwnerName { get; set; } = "Host";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public long Created { get; set; }
    public long Updated { get; set; }
    /// <summary>Rises with every change; Play renders the revision it was asked at.</summary>
    public long Revision { get; set; }
    /// <summary>"typed", "performance" (a recorded performance) or "template".</summary>
    public string Origin { get; set; } = "typed";
    public List<SentencePiece> Pieces { get; set; } = [];
    public SentenceSettings Settings { get; set; } = new();
    public List<SentenceVersion> Versions { get; set; } = [];
    /// <summary>Shown on the board as a tile (kept sentences only).</summary>
    public bool Pinned { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Kept => Versions.Count > 0;
    public static readonly string[] Origins = ["typed", "performance", "template"];
}

/// <summary>A named nonverbal piece a person marked on a library recording ([laugh], [gasp], [breath] …).</summary>
public sealed class NamedPiece
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RecordingId { get; set; } = "";
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string By { get; set; } = "host";
    public string ByName { get; set; } = "Host";
    public long Created { get; set; }
}

/// <summary>A palette item kept as a reference (a favourite): a range of a recording, or a named piece. Never audio.</summary>
public sealed class PaletteRef
{
    public string? RecordingId { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Text { get; set; } = "";
    public string? NamedId { get; set; }
}

public sealed class SentenceTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The text, with slots a person replaces written as {slot}.</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// A reusable named phrase block (2.45): words to say, and optionally the voice that says them. Added to a sentence (at
/// the caret) or used in a template as {Name:block}; it is matched like typed text, within its voice. Text only.
/// </summary>
public sealed class PhraseBlock
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public string? VoiceId { get; set; }
}

/// <summary>Voice filters, palette favourites and templates; references to library items only.</summary>
public sealed class PerformanceSet
{
    public string Id { get; set; } = SentenceStore.DefaultSet;
    public string Name { get; set; } = "Default";
    public List<string> VoiceIds { get; set; } = [];
    public List<PaletteRef> Favorites { get; set; } = [];
    public List<SentenceTemplate> Templates { get; set; } = [];
    /// <summary>2.45: phrase blocks. Null when there are none (left out of the file, which older companions then read as before).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PhraseBlock>? Blocks { get; set; }
}

public sealed class PersonSets
{
    public string Owner { get; set; } = "host";
    public string Active { get; set; } = SentenceStore.DefaultSet;
    public List<PerformanceSet> Sets { get; set; } = [];
}

public sealed class SentenceFile
{
    public int SchemaVersion { get; set; } = SentenceStore.SchemaVersion;
    public List<SentenceProject> Sentences { get; set; } = [];
    public List<NamedPiece> Named { get; set; } = [];
    public List<PersonSets> People { get; set; } = [];
}

public sealed class SentenceStore
{
    public const int SchemaVersion = 1;
    public const string DefaultSet = "default";
    public const int MaxSentences = 2000, MaxPieces = 400, MaxTokens = 400, MaxVersions = 50, MaxNamed = 5000, MaxPeople = 512, MaxSets = 32, MaxFavorites = 200, MaxTemplates = 100, MaxBlocks = 100, MaxText = 4000;
    public const double MaxTimelineMs = 600000;
    public const long MaxFileBytes = 64L * 1024 * 1024;
    private static readonly Regex IdPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex PiecePattern = new("^[a-f0-9]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex AudioPattern = new("^v[0-9]{1,3}-[a-f0-9]{8}\\.wav$", RegexOptions.CultureInvariant);
    public static readonly Regex NamePattern = new("^[a-z][a-z0-9 _-]{0,29}$", RegexOptions.CultureInvariant);
    public static readonly string[] Kinds = ["word", "named", "missing", "pause", "splice"];
    private readonly object gate = new();
    private readonly Func<long> now;
    private SentenceFile file = new();
    private long revision;
    public string Folder { get; }
    private string MainPath => Path.Combine(Folder, "sentences.v1.json");
    private string BackupPath => Path.Combine(Folder, "sentences.v1.bak.json");
    public string AudioFolder => Path.Combine(Folder, "sentences");
    public bool Blocked { get; private set; }
    public string? Problem { get; private set; }
    /// <summary>The largest file a save writes (tests lower it): Load refuses anything over <see cref="MaxFileBytes"/>.</summary>
    internal long SaveLimit { get; set; } = MaxFileBytes;
    /// <summary>A guest's change may grow the file only to this share of <see cref="SaveLimit"/>: the rest stays the host's.</summary>
    public const double GuestShare = 0.75;
    public long Revision => Interlocked.Read(ref revision);

    public SentenceStore(string dataDirectory, Func<long>? clock = null)
    {
        Folder = Path.Combine(dataDirectory, "library");
        now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Load();
    }

    public static bool ValidId(string? id) => id != null && IdPattern.IsMatch(id);
    public static bool ValidPieceId(string? id) => id != null && PiecePattern.IsMatch(id);
    public static string NewId() => Guid.NewGuid().ToString("N");
    public static string NewPieceId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    public long Now => now();

    private void Load()
    {
        SentenceFile? loaded = null; string? problem = null;
        foreach (var path in new[] { MainPath, BackupPath })
        {
            if (!File.Exists(path)) { if (path == MainPath) continue; else break; }
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("it is larger than 64 MiB");
                var read = JsonSerializer.Deserialize<SentenceFile>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException("it is empty");
                Validate(read);
                loaded = read;
                if (path == BackupPath) problem = "The sentences file was damaged; its previous version was loaded. The damaged file is kept as it was until the next change.";
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
            {
                problem ??= $"The sentences file could not be read ({(ex is InvalidDataException ? ex.Message : ex.GetType().Name)}). It was left untouched.";
            }
        }
        if (loaded == null && File.Exists(MainPath)) { Blocked = true; Problem = problem; return; }
        file = loaded ?? new SentenceFile();
        Problem = problem;
    }

    private static bool Clean(string? s, int max, bool allowEmpty = false) => s != null && s.Length <= max && (allowEmpty || s.Trim().Length > 0) && !s.Any(char.IsControl);
    private static bool Ms(double v, double max = MaxTimelineMs) => double.IsFinite(v) && v >= 0 && v <= max;

    private static void Require(bool ok, string what) { if (!ok) throw new InvalidDataException(what); }

    private static void ValidateSource(SentenceSource? s)
    {
        Require(s != null && ValidId(s.RecordingId) && s.StartMs >= 0 && s.EndMs > s.StartMs && s.EndMs <= 4L * 3600 * 1000, "bad piece source");
        Require(s!.VoiceId == null || ValidId(s.VoiceId), "bad piece voice");
        Require(s.Label == null || Clean(s.Label, 60), "bad piece label");
    }
    private static void ValidateEffect(PieceEffect? e) => Require(e == null || double.IsFinite(e.Speed) && e.Speed is >= 0.25 and <= 4, "bad effect");

    public static void ValidatePieces(List<SentencePiece>? pieces)
    {
        Require(pieces != null && pieces.Count <= MaxPieces, "too many pieces");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pieces!)
        {
            Require(p != null && ValidPieceId(p.Id) && ids.Add(p.Id) && Kinds.Contains(p.Kind), "bad piece");
            Require(p!.TokenStart is null == p.TokenEnd is null && (p.TokenStart is not int s || s >= 0 && p.TokenEnd > s && p.TokenEnd <= MaxTokens), "bad piece words");
            Require(Clean(p.Text, 400, true) && Ms(p.OffsetMs) && Ms(p.FadeInMs, 2000) && Ms(p.FadeOutMs, 2000) && Ms(p.DurationMs, 60000) && (p.Gain is null or (>= 0 and <= 8)) && p.GapAfterMs is >= 0 and <= 60000, "bad piece timing");
            Require(p.Substitute == null || Clean(p.Substitute, 100), "bad substitute");
            Require(p.NamedId == null || ValidId(p.NamedId), "bad named piece");
            Require(p.VoiceLock == null || ValidId(p.VoiceLock), "bad piece voice");
            ValidateEffect(p.Effect);
            switch (p.Kind)
            {
                case "word": case "named": ValidateSource(p.Source); Require(p.Parts == null && p.Missing == null, "bad piece"); break;
                case "splice": Require(p.Parts is { Count: >= 2 and <= 4 } && p.Source == null && p.Missing == null, "bad splice"); foreach (var part in p.Parts!) ValidateSource(part); break;
                case "missing": Require(p.Missing is "gap" or "bleep" && p.Source == null && p.Parts == null && p.DurationMs > 0, "bad missing word"); break;
                case "pause": Require(p.Source == null && p.Parts == null && p.Missing == null && p.DurationMs > 0, "bad pause"); break;
            }
        }
    }

    public static void ValidateSettings(SentenceSettings? s)
    {
        Require(s != null && s.Chaos is >= 0 and <= 100 && s.Shuffles >= 0 && s.Timing is "fluent" or "original", "bad sentence settings");
        Require(s!.VoiceIds != null && s.VoiceIds.Count <= 256 && s.VoiceIds.All(ValidId) && s.VoiceIds.Distinct().Count() == s.VoiceIds.Count, "bad voice filter");
        ValidateEffect(s.Effect);
    }

    private static void ValidateRef(PaletteRef? r)
    {
        Require(r != null && Clean(r.Text, 100, true), "bad favourite");
        if (r!.NamedId != null) Require(ValidId(r.NamedId) && r.RecordingId == null, "bad favourite");
        else Require(ValidId(r.RecordingId) && r.StartMs >= 0 && r.EndMs > r.StartMs && r.EndMs <= 4L * 3600 * 1000, "bad favourite");
    }

    /// <summary>Strict: anything out of place refuses the whole file (it is then left untouched).</summary>
    public static void Validate(SentenceFile f)
    {
        Require(f.SchemaVersion == SchemaVersion, "unknown schema version");
        Require(f.Sentences != null && f.Named != null && f.People != null, "a section is missing");
        Require(f.Sentences!.Count <= MaxSentences && f.Named!.Count <= MaxNamed && f.People!.Count <= MaxPeople, "too many entries");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in f.Sentences)
        {
            Require(s != null && ValidId(s.Id) && ids.Add(s.Id), "bad sentence id");
            Require(s!.Owner is { Length: > 0 and <= 128 } && Clean(s.OwnerName, 40) && Clean(s.Title, 200, true) && Clean(s.Text, MaxText, true), "bad sentence");
            Require(s.Created >= 0 && s.Updated >= 0 && s.Revision >= 0 && SentenceProject.Origins.Contains(s.Origin), "bad sentence");
            ValidatePieces(s.Pieces); ValidateSettings(s.Settings);
            Require(s.Versions != null && s.Versions.Count <= MaxVersions && (!s.Pinned || s.Versions.Count > 0), "bad versions");
            int last = 0;
            foreach (var v in s.Versions!)
            {
                Require(v != null && v.N > last && v.At >= 0 && v.By is { Length: > 0 and <= 128 } && Clean(v.ByName, 40) && Clean(v.Text, MaxText, true), "bad version");
                last = v!.N;
                Require(AudioPattern.IsMatch(v.File ?? "") && HashPattern.IsMatch(v.Hash ?? "") && v.Bytes > 0 && v.DurationMs >= 0, "bad version audio");
                ValidatePieces(v.Pieces); ValidateSettings(v.Settings);
                Require(v.Sources != null && v.Sources.Count <= MaxPieces * 4, "bad pinned sources");
                foreach (var src in v.Sources!)
                    Require(src != null && ValidPieceId(src.PieceId) && ValidId(src.RecordingId) && HashPattern.IsMatch(src.Hash ?? "") && src.StartFrame >= 0 && src.EndFrame > src.StartFrame && src.Rate is >= 8000 and <= 384000, "bad pinned source");
            }
        }
        var named = new HashSet<string>(StringComparer.Ordinal);
        // The section checks above compare with null, so the compiler treats these lists as maybe-null after them.
        foreach (var n in f.Named!)
        {
            Require(n != null && ValidId(n.Id) && named.Add(n.Id) && NamePattern.IsMatch(n.Name ?? "") && ValidId(n.RecordingId), "bad named piece");
            Require(n!.StartMs >= 0 && n.EndMs > n.StartMs && n.EndMs - n.StartMs <= 30000 && n.By is { Length: > 0 and <= 128 } && Clean(n.ByName, 40) && n.Created >= 0, "bad named piece");
        }
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in f.People!)
        {
            Require(p != null && p.Owner is { Length: > 0 and <= 128 } && owners.Add(p.Owner) && p.Sets != null && p.Sets.Count is >= 1 and <= MaxSets, "bad performance sets");
            var sets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var set in p!.Sets!)
            {
                Require(set != null && (set.Id == DefaultSet || ValidId(set.Id)) && sets.Add(set.Id) && Clean(set.Name, 40), "bad performance set");
                Require(set!.VoiceIds != null && set.VoiceIds.Count <= 256 && set.VoiceIds.All(ValidId), "bad performance set voices");
                Require(set.Favorites != null && set.Favorites.Count <= MaxFavorites && set.Templates != null && set.Templates.Count <= MaxTemplates, "bad performance set");
                foreach (var r in set.Favorites!) ValidateRef(r);
                foreach (var t in set.Templates!) Require(t != null && ValidId(t.Id) && Clean(t.Name, 40) && Clean(t.Text, 400), "bad template");
                if (set.Blocks == null) continue;
                Require(set.Blocks.Count <= MaxBlocks, "too many phrase blocks");
                var blockIds = new HashSet<string>(StringComparer.Ordinal); var blockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in set.Blocks)
                    Require(b != null && ValidId(b.Id) && blockIds.Add(b.Id) && Clean(b.Name, 40) && blockNames.Add(b.Name.Trim()) && Clean(b.Text, 400) && (b.VoiceId == null || ValidId(b.VoiceId)), "bad phrase block");
            }
            Require(sets.Contains(DefaultSet) && sets.Contains(p.Active), "bad active set");
        }
    }

    private void RequireWritable() { if (Blocked) throw new InvalidDataException(Problem ?? "The sentences file could not be read. It was left untouched."); }

    private void SaveLocked(bool guest, long before)
    {
        RequireWritable();
        Validate(file);
        // Never a file Load would refuse (review, 2.42): the change is refused instead, and Change puts it back. A
        // guest's change that grows the file stops earlier, so guests cannot fill the room the host's own sentences need.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json.Options);
        if (bytes.Length > SaveLimit) throw new InvalidDataException($"The sentences would take more than {SaveLimit / (1024 * 1024)} MiB. Discard some sentences or make shorter ones first.");
        if (guest && bytes.Length > before && bytes.Length > SaveLimit * GuestShare) throw new InvalidDataException("The sentences file is nearly full. The host can make room by removing sentences.");
        Directory.CreateDirectory(Folder);
        var temporary = MainPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(MainPath)) File.Replace(temporary, MainPath, BackupPath, true);
            else File.Move(temporary, MainPath);
            if (!Blocked) Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = "The sentences could not be saved: " + ex.Message;
            throw new InvalidDataException(Problem);
        }
        Interlocked.Increment(ref revision);
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the file's lock (2.47), so writing sentences' audio and recording the sentences
    /// that refer to it is one step for <see cref="SweepAudio"/>, and removing a sentence and freeing its audio is one
    /// step for a restore. Before, a sentence discarded while a backup restore was putting it back could have the audio
    /// the restore had just written swept away (it was on disk while the sentence was not in the file yet).
    /// The work may call this store's own methods (the lock is re-entrant), and nothing else that takes a lock.
    /// </summary>
    public T Exclusive<T>(Func<T> work) { lock (gate) return work(); }

    /// <summary>Reads under the lock. The callback must not keep references past the call (copy what it needs).</summary>
    public T Read<T>(Func<SentenceFile, T> read) { lock (gate) return read(file); }

    /// <summary>
    /// Changes the file and saves it; if the change is refused (validation, size) or cannot be saved, the file in memory
    /// goes back to what it was, so a failed change leaves nothing half done. <paramref name="guest"/>: a guest's change,
    /// which may use only <see cref="GuestShare"/> of the file's room.
    /// </summary>
    public T Change<T>(Func<SentenceFile, T> change, bool guest = false)
    {
        lock (gate)
        {
            RequireWritable();
            var before = JsonSerializer.Serialize(file, Json.Options);
            try { var result = change(file); SaveLocked(guest, System.Text.Encoding.UTF8.GetByteCount(before)); return result; }
            catch { file = JsonSerializer.Deserialize<SentenceFile>(before, Json.Options)!; throw; }
        }
    }

    /// <summary>A deep copy (versions, plays and previews work on copies, never on the live project).</summary>
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json.Options), Json.Options)!;

    /// <summary>
    /// Writes a kept version's audio once (temporary file, flush, rename; never over an existing file). Created: this
    /// call wrote it (so it may remove it again if the version cannot be recorded).
    /// </summary>
    public (string File, string Hash, bool Created) WriteAudio(string sentenceId, int n, byte[] wav)
    {
        RequireWritable();
        if (!ValidId(sentenceId)) throw new InvalidDataException("Unknown sentence.");
        var hash = Convert.ToHexString(SHA256.HashData(wav)).ToLowerInvariant();
        var name = $"v{n}-{hash[..8]}.wav";
        var folder = Path.Combine(AudioFolder, sentenceId);
        var path = Path.Combine(folder, name);
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(path)) return (name, hash, false);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(wav); stream.Flush(true); }
            File.Move(temporary, path, false);
            return (name, hash, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException("The kept sentence could not be saved: " + ex.Message); }
    }

    /// <summary>Removes a version's audio file that no version records (its Keep failed after writing it).</summary>
    public void RemoveAudio(string sentenceId, string fileName)
    {
        try { File.Delete(AudioPath(sentenceId, fileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreferenced file; nothing plays it */ }
    }

    /// <summary>
    /// 2.45 (D10): frees a sentence's kept audio that no version in the file refers to any more, after the sentence was
    /// removed. Only files named like kept audio (v&lt;N&gt;-&lt;hash8&gt;.wav), only in that sentence's own folder, never
    /// through a link; the folder goes too once it is empty. Under the file's lock, so no Keep can record a version
    /// meanwhile (a Keep that wrote its file first finds the sentence gone and removes that file itself). Source
    /// recordings and kept clips live elsewhere and are never touched. Nothing happens while the file is blocked.
    /// </summary>
    public (int Files, long Bytes) SweepAudio(string sentenceId)
    {
        if (!ValidId(sentenceId)) return (0, 0);
        lock (gate)
        {
            if (Blocked) return (0, 0);
            var referenced = file.Sentences.Where(s => s.Id == sentenceId).SelectMany(s => s.Versions).Select(v => v.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var folder = new DirectoryInfo(Path.Combine(AudioFolder, sentenceId));
            int count = 0; long bytes = 0;
            try
            {
                if (!folder.Exists || folder.LinkTarget != null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (0, 0);
                foreach (var audio in folder.EnumerateFiles())
                {
                    if (!AudioPattern.IsMatch(audio.Name) || referenced.Contains(audio.Name) || audio.LinkTarget != null) continue;
                    try { long length = audio.Length; audio.Delete(); count++; bytes += length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use or read-only: it stays, unreferenced */ }
                }
                if (!folder.EnumerateFileSystemInfos().Any()) folder.Delete(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            return (count, bytes);
        }
    }

    public string AudioPath(string sentenceId, string fileName)
    {
        if (!ValidId(sentenceId) || !AudioPattern.IsMatch(fileName)) throw new InvalidDataException("Unknown sentence audio.");
        return Path.Combine(AudioFolder, sentenceId, fileName);
    }

    // ---- Backups (2.44) ----------------------------------------------------------------------------------------------

    /// <summary>The file as it stands (its bytes) and every kept version's audio, for a backup. Null while the file is blocked.</summary>
    public (byte[] Bytes, long Revision, (string SentenceId, int Version, string File, string Hash, long Bytes)[] Audio)? CaptureBackup()
    {
        lock (gate)
        {
            if (Blocked) return null;
            return (JsonSerializer.SerializeToUtf8Bytes(file, Json.Options), Revision,
                [.. file.Sentences.SelectMany(s => s.Versions.Select(v => (s.Id, v.N, v.File, v.Hash, v.Bytes)))]);
        }
    }

    /// <summary>
    /// Restores sentences from a backup: one that is gone comes back with all its versions; one that is here is left as
    /// it is (<paramref name="replace"/> false) or replaced whole. Validated and saved like any change, within the
    /// file's limits. Returns what was restored and, for Undo, each restored sentence's previous state (null: it was not here).
    /// </summary>
    public (int Restored, int Skipped, List<(string Id, SentenceProject? Before, long Updated)> Undo) Merge(IReadOnlyList<SentenceProject> projects, bool replace)
    {
        int restored = 0, skipped = 0; var undo = new List<(string, SentenceProject?, long)>();
        Change(f =>
        {
            foreach (var p in projects)
            {
                var copy = Copy(p);
                int at = f.Sentences.FindIndex(s => s.Id == p.Id);
                if (at >= 0 && !replace) { skipped++; continue; }
                if (at < 0 && f.Sentences.Count >= MaxSentences) throw new InvalidDataException($"There are as many sentences as the library can hold ({MaxSentences}).");
                undo.Add((p.Id, at >= 0 ? Copy(f.Sentences[at]) : null, copy.Updated));
                if (at >= 0) f.Sentences[at] = copy; else f.Sentences.Add(copy);
                restored++;
            }
            return 0;
        });
        return (restored, skipped, undo);
    }

    /// <summary>Undoes a <see cref="Merge"/>: each sentence still as it was restored goes back to what it was. Returns how many did.</summary>
    public int UndoMerge(IReadOnlyList<(string Id, SentenceProject? Before, long Updated)> undo)
    {
        int done = 0;
        Change(f =>
        {
            foreach (var (id, before, updated) in undo)
            {
                int at = f.Sentences.FindIndex(s => s.Id == id);
                if (at < 0 || f.Sentences[at].Updated != updated) continue;
                if (before == null) f.Sentences.RemoveAt(at); else f.Sentences[at] = Copy(before);
                done++;
            }
            return 0;
        });
        return done;
    }

    /// <summary>
    /// Replaces the whole file with a backup's (validated first; the replaced file becomes sentences.v1.bak.json). A
    /// file that could not be read is kept as sentences.v1.damaged-&lt;utc&gt;.json first, then replaced.
    /// </summary>
    public void ReplaceAll(SentenceFile next)
    {
        Validate(next);
        lock (gate)
        {
            if (Blocked && File.Exists(MainPath)) { File.Copy(MainPath, Path.Combine(Folder, $"sentences.v1.damaged-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json"), true); Blocked = false; Problem = null; }
            var before = file;
            file = Copy(next);
            try { SaveLocked(false, 0); }
            catch { file = before; throw; }
        }
    }

    /// <summary>Checks a backup's sentences file as Load would.</summary>
    public static SentenceFile ParseBackup(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("The backup's sentences file is too large.");
        var read = JsonSerializer.Deserialize<SentenceFile>(bytes, Json.Options) ?? throw new InvalidDataException("The backup's sentences file is empty.");
        Validate(read);
        return read;
    }
}
