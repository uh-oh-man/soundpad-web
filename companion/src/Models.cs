using System.Text.Json;
namespace SoundpadWeb;

public record Sound(string Id, string Name, string? Icon);
public record Category(string Id, string Name, string? Icon, string[] SoundIds, Category[] Children);
public record Library(string Version, Sound[] Sounds, Category[] Categories);
public record Playback(string Status = "UNKNOWN", long? PositionMs = null, long? DurationMs = null);
public record Permissions(bool RemotePlayback = true, bool GuestCounter = false);
public record Snapshot(int V, long Revision, bool Available, string? Error, Library Library, Playback Playback, int Counter, Permissions Permissions);
public sealed class DiskState
{
    public int Counter { get; set; }
    public Permissions Permissions { get; set; } = new();
    public string IdentityKey { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public Dictionary<string, string> Icons { get; set; } = [];
    public Dictionary<string, long> Receipts { get; set; } = [];
    public string? Website { get; set; }
}
public record Command(int V, string Id, long At, string Op, string? SoundId = null, string? LibraryVersion = null, int? Delta = null, string? Image = null, Permissions? Permissions = null);
public record Reply(string Type, string Id, bool Ok, string? Error = null);
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, MaxDepth = 32 };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
