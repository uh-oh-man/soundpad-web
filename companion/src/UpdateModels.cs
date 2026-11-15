using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
namespace SoundpadWeb;

/// <summary>
/// The self-update state the host's pages see (2.44, gate updater; snapshot field <c>update</c>, stripped for guests).
/// <paramref name="State"/> is idle, checking, available, downloading, verifying, ready, installing, failed or
/// unsupported. <paramref name="Reason"/> is a fixed word the website turns into plain text (see UpdateReasons);
/// <paramref name="Error"/> is the same in words, for logs and older pages. <paramref name="Offered"/>: the host said
/// Later (or was already offered the update) in this launch, so the launch notice does not come back.
/// </summary>
public record UpdateStatus(string State, string Current,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Latest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NotesUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Total = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Offered = null);

/// <summary>The fixed reason words of <see cref="UpdateStatus.Reason"/>.</summary>
public static class UpdateReasons
{
    public const string NoRepository = "noRepository", NoExe = "noExe", NotWritable = "notWritable", NotNtfs = "notNtfs",
        RateLimited = "rateLimited", Network = "network", NoAssets = "noAssets", TooLarge = "tooLarge", SizeMismatch = "sizeMismatch",
        HashMismatch = "hashMismatch", DigestMismatch = "digestMismatch", VersionMismatch = "versionMismatch", Blocked = "blocked",
        Busy = "busy", SwapFailed = "swapFailed", RolledBack = "rolledBack";
}

/// <summary>A release newer than the running companion, as the check found it.</summary>
/// <param name="ExeSize">The asset size from the API, or Content-Length when the API was rate limited; null when unknown.</param>
/// <param name="ExeDigest">The API's "sha256:…" digest when it has one (absent means unknown, not a failure).</param>
public sealed record ReleaseInfo(string Version, string Tag, string NotesUrl, Uri ExeUrl, long? ExeSize, string? ExeDigest, Uri ShaUrl);

/// <summary>
/// update-settings.v1.json in the data folder (2.44): whether to check for updates at launch. At most 4 KiB, checked
/// strictly; fields this version does not know are kept when it writes. A damaged file means the default (check at
/// launch) and is left as it is. Older companions never read it.
/// </summary>
public sealed class UpdateSettingsFile(string path)
{
    public const int MaxBytes = 4096;
    public string PathName => path;
    /// <summary>True when the file exists but could not be read: it is left untouched until the host changes the setting.</summary>
    public bool Damaged { get; private set; }

    public bool CheckAtLaunch()
    {
        var root = Read();
        return root?["checkAtLaunch"]?.GetValue<bool>() ?? true;
    }

    private JsonObject? Read()
    {
        Damaged = false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > MaxBytes) { Damaged = true; return null; }
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["format"] is not JsonValue format || format.GetValueKind() != JsonValueKind.Number || format.GetValue<int>() != 1) { Damaged = true; return null; }
            if (root["checkAtLaunch"] is { } check && check.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) { Damaged = true; return null; }
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException) { Damaged = true; return null; }
    }

    /// <summary>Writes the setting atomically (.tmp, flush, move), keeping unknown fields of a readable file.</summary>
    public void Save(bool checkAtLaunch)
    {
        var root = Read() ?? new JsonObject();
        root["format"] = 1;
        root["checkAtLaunch"] = checkAtLaunch;
        var bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        if (bytes.Length > MaxBytes) { root = new JsonObject { ["format"] = 1, ["checkAtLaunch"] = checkAtLaunch }; bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()); }
        UpdateFiles.WriteAtomic(path, bytes);
        Damaged = false;
    }
}

/// <summary>What the update hand-over carries from the old process to the new one (2.44). See docs/UPDATES.md.</summary>
public sealed record UpdateHandoff
{
    public int Format { get; init; } = 1;
    public required string Id { get; init; }
    public long CreatedUtcMs { get; init; }
    public required string DataDir { get; init; }
    public int OldPid { get; init; }
    public required string FromVersion { get; init; }
    public required string ToVersion { get; init; }
    public required ServerHandoff Server { get; init; }
    public required BridgeHandoff Bridge { get; init; }
    public WindowHandoff Window { get; init; } = new(false, false);
}

/// <summary>The loopback server's launch values: the same port and token, so open pages reconnect by themselves.</summary>
/// <param name="Identity">The owner's invitation identity (kept in memory only by the old process), so guests rejoin with the same invitation.</param>
/// <param name="Ended">The owner had pressed End hosting: the new owner page must not start hosting by itself.</param>
/// <param name="OwnerClientId">The page that ran the session, whose place is held for it while pages reconnect.</param>
public sealed record ServerHandoff(string Token, int Port, JsonElement? Identity, JsonElement? Settings, string? Session, bool Ended, string? OwnerClientId);

public sealed record WindowHandoff(bool Hidden, bool Minimized);

/// <summary>The Bridge's in-memory session (everything else is already on disk). Speaker groups and the per-launch
/// guest word and phrase ids are not carried: they are rebuilt.</summary>
public sealed record BridgeHandoff
{
    public long ExitedUtcMs { get; init; }
    public long LaunchedUtcMs { get; init; }
    public string? HistorySessionId { get; init; }
    public CaptureHandoff Capture { get; init; } = new(false, null, null, false);
    public HotkeyHandoff Hotkey { get; init; } = new("F6", 10, false);
    public DraftHandoff[] Drafts { get; init; } = [];
    public GuestJoinHandoff[] GuestJoins { get; init; } = [];
    public Dictionary<string, long> ForgottenGuests { get; init; } = [];
    public HistoryRange[] SharedRanges { get; init; } = [];
    public string[] GuestsHere { get; init; } = [];
    public Dictionary<string, long> GuestsAway { get; init; } = [];
    public OwnReplayHandoff[] OwnReplays { get; init; } = [];
    public BudgetsHandoff Budgets { get; init; } = new([], []);
    public Dictionary<string, LibrarySource> Provenance { get; init; } = [];
    public MimicClipInfo[] RecentClips { get; init; } = [];
    public int CounterSession { get; init; }
    public bool PlaybackSuspended { get; init; }
    public bool SessionAccepting { get; init; }
    public string? AudioAudience { get; init; }
    public string[] RevokedPeers { get; init; } = [];
}
public sealed record CaptureHandoff(bool Running, string? SourceId, string? SourceName, bool Transcribing);
public sealed record HotkeyHandoff(string Key, int Seconds, bool Active);
public sealed record DraftHandoff(string Id, string Owner, string OwnerName, string SessionId, long Start, long End, string? Name, bool NameTyped, long NameStart, long NameEnd, long Created, long Updated, string? ClipAssetId, long ClipStart, long ClipEnd);
public sealed record GuestJoinHandoff(string Key, long Wall, long Frame, string? SessionId);
public sealed record OwnReplayHandoff(string SessionId, long Start, long End, string? Label);
public sealed record BudgetGuestHandoff(string Id, string Name, long SessionKept, long ExtraTemp, long ExtraKept, BudgetRequestView? Request);
public sealed record BudgetAssetHandoff(string AssetId, string Guest, long Bytes);
public sealed record BudgetsHandoff(BudgetGuestHandoff[] Guests, BudgetAssetHandoff[] TempAssets);

/// <summary>The ready file the new process writes (update-ready.&lt;id&gt;), read by the old one's watchdog.</summary>
public sealed record UpdateReady(string Version, int Pid);
