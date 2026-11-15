using System.Text.Json;
using System.Text.Json.Serialization;
namespace SoundpadWeb;

public record Sound(string Id, string Name, string? Icon, long? DurationMs = null, long? NativePlays = null, string? NativeLastPlayed = null, bool Temporary = false, string? UploadedBy = null, string? AssetId = null, string? Orientation = null, string? AudioKind = null, bool InternalVariant = false, string? AliasOf = null, string? WorkspaceId = null, string[]? Aliases = null);
public record Category(string Id, string Name, string? Icon, string[] SoundIds, Category[] Children);
public record Library(string Version, Sound[] Sounds, Category[] Categories);
public record Playback(string Status = "UNKNOWN", long? PositionMs = null, long? DurationMs = null,
    string? SoundId = null, string? Epoch = null, string? Requester = null, long SampledAt = 0, string IdentitySource = "unknown", long? RegionEndMs = null, bool Repeat = false, long PositionRevision = 0);
public record Permissions(bool RemotePlayback = true, bool GuestCounter = false, bool GuestSeeking = true, bool GuestSpotify = true, bool GuestUploads = true, bool GuestTransforms = true, bool GuestMimic = false,
    // Guest Keep (2.40). Never written to state.json (null there): it lives in keep-settings.json, which older companions ignore.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? GuestKeep = null,
    // Guests may edit the voice library (2.41). Never written to state.json either: it lives in library\voice-library.v1.json.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? GuestLibraryEdit = null);
public record Snapshot(int V, long Revision, bool Available, string? Error, Library Library, Playback Playback, int Counter, Permissions Permissions,
    int Protocol = 2, string? HostId = null, long StatisticsVersion = 0, string? CompanionVersion = null, long ManagedAudioVersion = 0, MimicState? Mimic = null,
    // True while the host has suspended guest playback with Panic (2.35). In memory only: a restart clears it.
    bool PlaybackSuspended = false,
    // 2.40: rises when anyone's drafts change (pages fetch draftList), when guest budgets or their requests change
    // (budgetStatus), and the number of Temp clips a crashed launch left, waiting for the host's Recover or Discard.
    long Drafts = 0, long Budgets = 0, int? TempRecovery = null,
    // 2.41: rises when the voice library, its word index, wanted-word notices or speaker groups change (pages fetch libraryStatus).
    long VoiceLibrary = 0,
    // 2.42: rises when sentences, named palette pieces or performance sets change (pages fetch sentenceList).
    long Sentences = 0,
    // 2.43: Kick presses since this companion started (gate kickSession). In memory only: never written to state.json.
    int CounterSession = 0,
    // 2.43: rises when the host's shared style (ui.json, gate uiSync) changes, so host pages fetch uiPrefsGet. In memory only.
    long UiRevision = 0,
    // 2.44 (Stage H): rises when the backup settings, a backup run or a restore changes (host pages fetch backupSettings).
    long Backups = 0);
public record Participant(string Id, string Name);
public record ParticipantPlays(string Name, long Count);
public sealed class SoundStatistics
{
    public long Total { get; set; }
    public long? LastPlayedAt { get; set; }
    public Dictionary<string, ParticipantPlays> Participants { get; set; } = [];
}
public record Activity(string CommandId, string SoundId, long At, string ParticipantId, string Name, string Source);
public sealed class DiskState
{
    public int SchemaVersion { get; set; } = 1;
    public int Counter { get; set; }
    public Permissions Permissions { get; set; } = new();
    public string IdentityKey { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public Dictionary<string, string> Icons { get; set; } = [];
    public Dictionary<string, string> IconFiles { get; set; } = [];
    public Dictionary<string, SoundStatistics> Statistics { get; set; } = [];
    public List<Activity> Activity { get; set; } = [];
    public long StatisticsVersion { get; set; }
    public bool SpotifyEnabled { get; set; }
    public Dictionary<string, long> Receipts { get; set; } = [];
    public string? Website { get; set; }
}
public record Command(int V, string Id, long At, string Op, string? SoundId = null, string? LibraryVersion = null, int? Delta = null, string? Image = null, Permissions? Permissions = null,
    long? PositionMs = null, string? Epoch = null, long? EndMs = null, bool Repeat = false, string? Action = null, string? AssetId = null, string? VariantId = null, string? Orientation = null, string? DestinationDirectory = null, int? CategoryId = null, string? CategoryName = null, bool Confirmed = false, string? AudienceId = null, string? OwnerId = null, string? SourceId = null, long? MimicEnd = null, string? Name = null, double? Speed = null, bool KeepPitch = false,
    // trimAudio, mimicClip, mimicLibraryImport (2.35): play the new clip when it is ready, if nothing newer was asked for meanwhile.
    bool Autoplay = false,
    // Recording history (2.39): a range of frames on the history timeline, waveform bins, the history length in seconds,
    // the subtitles setting, and a transcript correction (word id and its text).
    long? StartFrame = null, long? EndFrame = null, int? Bins = null, int? Seconds = null, string? Subtitles = null, long? WordId = null, string? Text = null,
    // beginSession (2.39): true when the invitation is new (Replace invite), so guest join times and shares start over.
    bool? NewInvite = null,
    // revokePeer (2.39): the participant the host disconnected or turned away, whose recording-history join time ends.
    string? GuestId = null,
    // Drafts (2.40): the draft a command is about; keep settings and guest budgets (MB per guest, and whether the
    // permanent budget counts each session or everything a guest ever kept).
    string? DraftId = null, int? TempMb = null, int? KeptMb = null, string? BudgetMode = null, bool? GuestKeep = null,
    // Voice library (2.41): which recording(s), metadata edits, an audit group to undo, search text and paging, a voice's
    // colour, the Soundpad categories to index, a speaker group, and filters (kind of result, unassigned, tag).
    string? RecordingId = null, string[]? RecordingIds = null, string? VoiceId = null, string[]? Tags = null, string[]? AddTags = null, string[]? RemoveTags = null,
    bool? Favorite = null, string? Title = null, string? CorrectedText = null, bool? Reviewed = null, long? AuditId = null, string? Query = null, int? Offset = null, int? Limit = null,
    string? Color = null, string[]? Categories = null, int? GroupId = null, string? Kind = null, bool? Unassigned = null, string? Tag = null, bool? GroupSpeakers = null, bool? GuestLibraryEdit = null,
    // Sentences and the speaking palette (2.42): the sentence a command is about, and what it asks (SentenceInput);
    // a performance set to create or change.
    string? SentenceId = null, SentenceInput? Sentence = null, PerformanceSetInput? Set = null,
    // uiPrefsSet (2.43): the style fields to change, checked field by field by UiPrefsStore.
    JsonElement? UiPrefs = null,
    // Stage H (2.44): the one payload field of every Stage H command, checked strictly by the command's own handler.
    JsonElement? Body = null);
/// <summary>The details of a sentence or palette command (2.42); null fields are not part of the request.</summary>
public sealed record SentenceInput(string? Do = null, string? Text = null, string[]? Pieces = null, double? OffsetMs = null, long? StartMs = null, long? EndMs = null,
    double? FadeInMs = null, double? FadeOutMs = null, double? Gain = null, bool? Balanced = null, double? DurationMs = null, string? Mode = null, PieceEffect? Effect = null,
    int? Chaos = null, string? Timing = null, bool? Balance = null, bool? Cleanup = null, bool? Altered = null, string[]? VoiceIds = null, int? Alternative = null, int? Splice = null,
    string? Word = null, int? Version = null, bool? Pinned = null, string? Variant = null, PaletteRef? Item = null, string? Origin = null);
/// <summary>A performance set to create or change (2.42).</summary>
public sealed record PerformanceSetInput(string? Id = null, string? Name = null, string[]? VoiceIds = null, PaletteRef[]? Favorites = null, SentenceTemplate[]? Templates = null);
public record Reply(string Type, string Id, bool Ok, string? Error = null, object? Data = null);
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, MaxDepth = 32 };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
