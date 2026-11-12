using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
namespace SoundpadWeb;

public sealed class StateStore(string directory)
{
    public string DirectoryPath => directory;
    private string PathName => Path.Combine(directory, "state.json");
    public DiskState Load()
    {
        Directory.CreateDirectory(directory);
        if (!File.Exists(PathName)) { var initial = new DiskState { SchemaVersion = 2 }; Save(initial); return initial; }
        try
        {
            if (new FileInfo(PathName).Length > 64 * 1024 * 1024) throw new InvalidDataException("State exceeds the supported storage limit.");
            var state = JsonSerializer.Deserialize<DiskState>(File.ReadAllText(PathName), Json.Options) ?? throw new InvalidDataException();
            if (state.SchemaVersion is < 1 or > 2 || state.Counter < 0 || Convert.FromBase64String(state.IdentityKey).Length != 32) throw new InvalidDataException("Unsupported or invalid state schema.");
            if (state.SchemaVersion == 1)
            {
                // Original bytes are preserved before any migration writes. Images are committed before metadata.
                var backup = Path.Combine(directory, "state.v1.backup.json");
                if (!File.Exists(backup)) File.Copy(PathName, backup);
                foreach (var pair in state.Icons.ToArray())
                {
                    try { state.IconFiles[pair.Key] = WriteIcon(pair.Key, IconData.Normalize(pair.Value)); state.Icons.Remove(pair.Key); }
                    catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException) { /* Retain corrupt legacy data in both original backup and state for recovery. */ }
                }
                state.SchemaVersion = 2; Save(state);
            }
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException) { throw new InvalidDataException("Local state is unreadable. Preserve state.json and repair it before starting; it has not been reset.", ex); }
    }
    public string WriteIcon(string id, string data)
    {
        if (id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidDataException("Invalid icon identity.");
        var bytes = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]);
        var file = id + "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + ".png";
        var folder = Path.Combine(directory, "icons"); Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, file);
        if (!File.Exists(destination)) { File.WriteAllBytes(destination + ".tmp", bytes); File.Move(destination + ".tmp", destination, true); }
        return file;
    }
    public Dictionary<string, string> ReadIcons(DiskState state)
    {
        var icons = new Dictionary<string, string>();
        foreach (var pair in state.Icons) { try { icons[pair.Key] = IconData.Normalize(pair.Value); } catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException) { } }
        foreach (var pair in state.IconFiles)
        {
            if (Path.GetFileName(pair.Value) != pair.Value || !pair.Value.EndsWith(".png", StringComparison.Ordinal)) continue;
            try { var path = Path.Combine(directory, "icons", pair.Value); if (new FileInfo(path).Length <= 131072) icons[pair.Key] = IconData.Normalize("data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidDataException) { }
        }
        return icons;
    }
    /// <summary>The state as it was at one moment, ready to write. A higher <see cref="Sequence"/> is newer.</summary>
    public sealed record StateImage(long Sequence, byte[] Json, string[] IconFiles);
    private readonly object writing = new();
    private long captured, committed;
    /// <summary>
    /// Copies the state into bytes (2.35). The caller must be the only one changing <paramref name="state"/> at
    /// that moment; the write itself (<see cref="Commit"/>) can then happen later and elsewhere.
    /// </summary>
    public StateImage Capture(DiskState state) => new(Interlocked.Increment(ref captured), JsonSerializer.SerializeToUtf8Bytes(state, Json.Options), [.. state.IconFiles.Values]);
    /// <summary>
    /// Writes an image durably (write-through, flush, rename), unless a newer image is already on disk: a deferred
    /// write that lost the race with a later durable one must not put older state back (2.35). False when skipped.
    /// <paramref name="createFolder"/> is off for deferred writes, so a data folder that was removed meanwhile is
    /// not brought back. The icon folder is scanned for obsolete files only with <paramref name="pruneIcons"/>.
    /// </summary>
    public bool Commit(StateImage image, bool pruneIcons = true, bool createFolder = true)
    {
        lock (writing)
        {
            if (image.Sequence <= committed) return false;
            if (createFolder) Directory.CreateDirectory(directory); else if (!Directory.Exists(directory)) return false;
            // A virus scanner can open a file just written for a moment (seen on GitHub's Windows runners in 2.35.0
            // CI), which refuses the rename, or the next write to the same temporary name. Retry briefly; a temporary
            // file that is still held gets a fresh name instead of failing the save.
            var temp = PathName + ".tmp";
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        f.Write(image.Json); f.Flush(true);
                    }
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
            committed = image.Sequence;
            foreach (var stale in Directory.EnumerateFiles(directory, "state.json.*.tmp")) { try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (pruneIcons) { try { PruneIcons(image.IconFiles); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return true;
        }
    }
    /// <summary>Writes the state durably before returning. Icons are pruned only when asked: nothing else changes them (2.35).</summary>
    public void Save(DiskState state, bool pruneIcons = true) => Commit(Capture(state), pruneIcons);
    private void PruneIcons(string[] iconFiles)
    {
        // Metadata is durable before obsolete, app-owned icon revisions are pruned.
        var iconFolder = Path.Combine(directory, "icons");
        if (Directory.Exists(iconFolder))
        {
            var retained = iconFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(iconFolder, "*.png"))
            {
                var name = Path.GetFileName(file);
                if (!retained.Contains(name) && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-fA-F0-9]{64}-[a-fA-F0-9]{16}\.png$"))
                {
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
public static class IconData
{
    public static string Normalize(string data)
    {
        string prefix = data.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? "data:image/png;base64," : data.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) ? "data:image/jpeg;base64," : throw new InvalidDataException("Use PNG or JPEG.");
        if (data.Length > 180000) throw new InvalidDataException("Icon must be under 128 KiB.");
        var bytes = Convert.FromBase64String(data[prefix.Length..]);
        if (bytes.Length > 131072) throw new InvalidDataException("Icon too large.");
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream, true, true);
        if (image.Width > 512 || image.Height > 512 || image.Width < 1 || image.Height < 1) throw new InvalidDataException("Icon dimensions must be 1–512 pixels.");
        if (image.RawFormat.Guid != ImageFormat.Png.Guid && image.RawFormat.Guid != ImageFormat.Jpeg.Guid) throw new InvalidDataException("Use PNG or JPEG.");
        // Re-encode to strip metadata and any appended content; keep transmissions compact.
        using var thumb = new Bitmap(image, new Size(96, 96));
        using var output = new MemoryStream(); thumb.Save(output, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
    }
}
