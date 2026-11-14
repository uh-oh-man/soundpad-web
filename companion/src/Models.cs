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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? GuestKeep = null);
public record Snapshot(int V, long Revision, bool Available, string? Error, Library Library, Playback Playback, int Counter, Permissions Permissions,
    int Protocol = 2, string? HostId = null, long StatisticsVersion = 0, string? CompanionVersion = null, long ManagedAudioVersion = 0, MimicState? Mimic = null,
    // True while the host has suspended guest playback with Panic (2.35). In memory only: a restart clears it.
    bool PlaybackSuspended = false,
    // 2.40: rises when anyone's drafts change (pages fetch draftList), when guest budgets or their requests change
    // (budgetStatus), and the number of Temp clips a crashed launch left, waiting for the host's Recover or Discard.
    long Drafts = 0, long Budgets = 0, int? TempRecovery = null);
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
    string? DraftId = null, int? TempMb = null, int? KeptMb = null, string? BudgetMode = null, bool? GuestKeep = null);
public record Reply(string Type, string Id, bool Ok, string? Error = null, object? Data = null);
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, MaxDepth = 32 };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
