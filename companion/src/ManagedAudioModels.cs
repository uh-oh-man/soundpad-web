namespace SoundpadWeb;

public sealed class AudioJournal
{
    public int SchemaVersion { get; set; } = 3;
    public string AppId { get; set; } = Guid.NewGuid().ToString("N");
    public string? SessionId { get; set; }
    public bool SessionOpen { get; set; }
    public long Revision { get; set; }
    public List<AudioAsset> Assets { get; set; } = [];
    public OwnedCategory? TempCategory { get; set; }
    public OwnedCategory? SavedCategory { get; set; }
    public List<AudioIssue> Issues { get; set; } = [];
}
public sealed class AudioAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Origin { get; set; } = "upload";
    public string Name { get; set; } = "Session audio";
    public string Uploader { get; set; } = "Guest";
    public string UploaderId { get; set; } = "anonymous";
    public string? SourceSoundId { get; set; }
    public string SourceHash { get; set; } = "";
    public string? SourcePath { get; set; }
    public string? UploadHash { get; set; }
    public string? WorkspaceKey { get; set; }
    public AudioStaging? Input { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long ModifiedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string State { get; set; } = "processing";
    public List<AudioVariant> Variants { get; set; } = [];
    public PromotionPlan? Promotion { get; set; }
}
public sealed class AudioVariant
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Orientation { get; set; } = "normal";
    public string? SessionId { get; set; }
    public string? LeaseSessionId { get; set; }
    public string SourceHash { get; set; } = "";
    public string Path { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string Storage { get; set; } = "session";
    public string? WorkspaceKey { get; set; }
    public long Bytes { get; set; }
    public bool OwnsFile { get; set; }
    public bool Current { get; set; } = true;
    public bool Permanent { get; set; }
    public bool LegacyApproval { get; set; }
    public bool Retired { get; set; }
    public string State { get; set; } = "processing";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public NativeAudioReference? Native { get; set; }
}
public sealed class AudioStaging
{
    public string SessionId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Bytes { get; set; }
    public string State { get; set; } = "planned";
}
public sealed class NativeAudioReference
{
    public string? SoundId { get; set; }
    public string Path { get; set; } = "";
    public int ObservedIndex { get; set; }
    public int? CategoryId { get; set; }
    public string LibraryFingerprint { get; set; } = "";
    public long VerifiedAt { get; set; }
}
public sealed class OwnedCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int? NativeId { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "planned";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public sealed class PromotionPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SourceVariantId { get; set; } = "";
    public string TargetVariantId { get; set; } = "";
    public string Destination { get; set; } = "";
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string State { get; set; } = "planned";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public record AudioIssue(string Id, string? AssetId, string Code, string Message, string Severity = "error", string Action = "retry");
public record AudioVariantView(string Id, string? SoundId, string Orientation, string State, bool Ready, bool Permanent, string? NativeLabel, bool Current, bool LegacyApproval);
public record AudioAssetView(string Id, string Name, string Origin, string Uploader, string State, long CreatedAt, AudioVariantView? Normal, AudioVariantView? Reversed, AudioVariantView[] Variants, bool HasTemporary);
public record AudioManagerView(long Revision, AudioAssetView[] Assets, AudioIssue[] Issues, int? TempCategoryId, string? TempCategoryName, bool Blocked, long TemporaryBytes);
public record NativeEntry(int Index, string Path);
public record NativeCategory(int Index, string Name, string? Type, string[] Paths);
public record NativeCatalog(NativeEntry[] Entries, NativeCategory[] Categories, string Fingerprint);
// Soundpad's documented selection API cannot atomically bind deletion to a path. An adapter
// must explicitly supply that stronger capability; production RC currently does not.
public interface IAtomicOwnedEntryRemoval
{
    bool CanRemoveOwnedAtomically { get; }
    Task<bool> RemoveOwnedEntry(string path, string expectedCatalogFingerprint);
}
