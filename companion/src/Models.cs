using System.Text.Json;
namespace SoundpadWeb;

public record Sound(string Id, string Name, string? Icon, long? DurationMs = null, long? NativePlays = null, string? NativeLastPlayed = null, bool Temporary = false, string? UploadedBy = null);
public record Category(string Id, string Name, string? Icon, string[] SoundIds, Category[] Children);
public record Library(string Version, Sound[] Sounds, Category[] Categories);
public record Playback(string Status = "UNKNOWN", long? PositionMs = null, long? DurationMs = null,
    string? SoundId = null, string? Epoch = null, string? Requester = null, long SampledAt = 0, string IdentitySource = "unknown", long? RegionEndMs = null, bool Repeat = false, long PositionRevision = 0);
public record Permissions(bool RemotePlayback = true, bool GuestCounter = false, bool GuestSeeking = true, bool GuestSpotify = true);
public record Snapshot(int V, long Revision, bool Available, string? Error, Library Library, Playback Playback, int Counter, Permissions Permissions,
    int Protocol = 2, string? HostId = null, long StatisticsVersion = 0, string? CompanionVersion = null);
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
    long? PositionMs = null, string? Epoch = null, long? EndMs = null, bool Repeat = false, string? Action = null);
public record Reply(string Type, string Id, bool Ok, string? Error = null, object? Data = null);
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, MaxDepth = 32 };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
