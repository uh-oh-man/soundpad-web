using System.Text.Json;
namespace SoundpadWeb;

/// <summary>What drafts.v1.json holds (2.45): the host's drafts on one recording history, as an update hands them over.</summary>
/// <param name="Format">Always 1. Any other value is not read.</param>
/// <param name="SessionId">The recording history the drafts pin. Drafts of any other history are ignored.</param>
public sealed record DraftFile(int Format, string SessionId, long SavedUtcMs, DraftHandoff[] Drafts);

/// <summary>
/// drafts.v1.json (2.45): the host's drafts, written whenever they change, so a crash does not lose them. Only the
/// host's: a guest's drafts would be no use after a crash (a guest sees nothing recorded before this launch), and
/// guests' names stay off the disk. A normal quit deletes the file with the recording history; after a crash the next
/// launch offers the drafts back with the recovered history ("Recover previous session"). An update hands drafts over
/// in memory as before (update-handoff.v1.bin) and the new process writes this file again.
/// Older companions never read it, and state.json and the managed-audio journal are untouched.
/// Writes go through a flushed .tmp file and a rename, retried while a virus scanner holds either (as state.json).
/// Reading checks every field and drops anything that does not fit; it never loosens what is accepted.
/// </summary>
public sealed class DraftStore(string directory)
{
    public const string FileName = "drafts.v1.json";
    public const int MaxBytes = 1024 * 1024;
    /// <summary>The host's own limit (Bridge.MaxDraftsPerPerson) with room for drafts still waiting for Recover.</summary>
    public const int MaxDrafts = 256;
    public string PathName => Path.Combine(directory, FileName);
    private readonly object writing = new();

    /// <summary>Writes the file durably, or deletes it when <paramref name="file"/> is null or holds no drafts.</summary>
    public void Save(DraftFile? file)
    {
        lock (writing)
        {
            if (file == null || file.Drafts.Length == 0) { Delete(); return; }
            if (!Directory.Exists(directory)) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(file with { Format = 1, Drafts = [.. file.Drafts.Take(MaxDrafts)] }, Json.Options);
            if (bytes.Length > MaxBytes) throw new InvalidDataException("The drafts are too large to save.");
            var temp = PathName + ".tmp";
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { f.Write(bytes); f.Flush(true); }
                    break;
                }
                catch (IOException) when (attempt < 5) { temp = PathName + "." + attempt + ".tmp"; Thread.Sleep(20); }
            }
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, PathName, true); break; }
                catch (Exception ex) when (attempt < 40 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(25); }
                catch { try { File.Delete(temp); } catch (Exception) { } throw; }
            }
            foreach (var stale in Directory.EnumerateFiles(directory, FileName + ".*.tmp")) { try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    /// <summary>Removes the file (a normal quit, nothing left to keep). Never throws.</summary>
    public void Delete()
    {
        lock (writing)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try { if (File.Exists(PathName)) File.Delete(PathName); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(25); }
            }
        }
    }

    /// <summary>
    /// The drafts the file holds for <paramref name="sessionId"/>, each checked: format 1, the host's, on that history,
    /// a range within [0, <paramref name="liveFrame"/>], names of at most 100 characters. Anything else is left out; an
    /// unreadable or oversized file gives nothing (and is left on disk for the next normal quit to delete).
    /// </summary>
    public DraftHandoff[] Load(string sessionId, long liveFrame)
    {
        try
        {
            var info = new FileInfo(PathName);
            if (!info.Exists || info.Length > MaxBytes) return [];
            var bytes = File.ReadAllBytes(PathName);
            using (var doc = JsonDocument.Parse(bytes))
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.Number || !f.TryGetInt32(out int format) || format != 1) return [];
            var file = JsonSerializer.Deserialize<DraftFile>(bytes, Json.Options);
            if (file is not { Format: 1, Drafts: { } drafts } || file.SessionId != sessionId) return [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return [.. drafts.Take(MaxDrafts).Where(d => Valid(d, sessionId, liveFrame) && seen.Add(d.Id))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException) { return []; }
    }

    private static bool Valid(DraftHandoff? d, string sessionId, long liveFrame) =>
        d != null && d.Id is { Length: 32 } id && id.All(Uri.IsHexDigit) && d.Owner == "host" && d.OwnerName is { Length: > 0 and <= 40 } && d.SessionId == sessionId
        && d.Start >= 0 && d.End > d.Start && d.End <= liveFrame
        && (d.Name == null || d.Name.Length is > 0 and <= 100 && !d.Name.Any(char.IsControl))
        && d.NameStart >= 0 && d.NameEnd >= d.NameStart && d.NameEnd <= liveFrame
        && d.Created > 0 && d.Updated >= d.Created
        && (d.ClipAssetId == null || d.ClipAssetId.Length is > 0 and <= 64) && d.ClipStart >= 0 && d.ClipEnd >= d.ClipStart;
}
