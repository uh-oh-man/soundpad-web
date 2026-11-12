using System.Runtime.InteropServices;
using System.Text.Json;
namespace SoundpadWeb;

/// <summary>
/// The app's own slot files for exact cue starts and regions (2.37): <c>&lt;data&gt;\slots\slot-1.wav</c> to
/// <c>slot-3.wav</c>, and <c>slots.json</c> next to them saying which paths were made and where they were last
/// registered. That record lives here and not in state.json or the managed-audio journal, so older companions
/// never read it and their saved data is unchanged. An older companion that finds slot entries in the Temp category
/// treats them as foreign: it does not remove that category and Temp asks the person, which is safe.
/// </summary>
internal sealed class SlotFiles
{
    /// <summary>Three, so the one playing is never rewritten while the next slice is written into another (measured 2.36: a playing slot is locked).</summary>
    public const int Count = 3;
    private const int RecordLimit = 16 * 1024;
    private readonly Lazy<SlotPool> pool;
    private readonly object sync = new();
    private string[]? recorded;

    public SlotFiles(string dataDirectory)
    {
        // Soundpad lists a path in its long form even when it was given the short 8.3 one (measured 2.36: TEMP came
        // as C:\Users\UH-OH-~1\...), and slots are found again by exact path, so they are named in long form.
        Folder = Path.Combine(LongPath(Path.GetFullPath(dataDirectory)), "slots");
        Paths = Enumerable.Range(1, Count).Select(i => Path.Combine(Folder, SlotPool.FileName(i))).ToArray();
        // A slot stays held while it plays (a region may repeat for a long time); every holder releases it itself.
        pool = new(() => new SlotPool(Folder, Count, new() { MaxHoldMs = 6 * 60 * 60 * 1000 }));
    }

    public string Folder { get; }
    public IReadOnlyList<string> Paths { get; }
    public SlotPool Pool => pool.Value;
    private string RecordPath => Path.Combine(Folder, "slots.json");

    /// <summary>
    /// Every path that is the app's own slot file: the current ones and any an earlier run recorded (a future
    /// version may use more). Only these, by exact path, may be removed with the Temp category.
    /// </summary>
    public IReadOnlyCollection<string> Owned()
    {
        lock (sync) recorded ??= ReadRecord();
        return Paths.Concat(recorded).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Records the slot paths and the category they were registered in. Best effort: the record only helps cleanup.</summary>
    public void Record(string? category)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            ManagedFiles.RequireOwnedPath(RecordPath, Folder);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new SlotRecord(1, [.. Paths], category, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), Json.Options);
            using (var file = new FileStream(RecordPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(RecordPath + ".tmp", RecordPath, true);
            lock (sync) recorded = [.. Paths];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private sealed record SlotRecord(int Version, string[] Slots, string? Category, long RegisteredAt);

    /// <summary>The recorded paths that are plausibly ours: slot-named files directly in this folder. Anything else is ignored.</summary>
    private string[] ReadRecord()
    {
        try
        {
            if (!File.Exists(RecordPath) || new FileInfo(RecordPath).Length > RecordLimit) return [];
            ManagedFiles.RequireOwnedPath(RecordPath, Folder);
            var record = JsonSerializer.Deserialize<SlotRecord>(File.ReadAllBytes(RecordPath), Json.Options);
            if (record is not { Version: 1, Slots: { Length: <= 64 } slots }) return [];
            return slots.Where(p => p != null && Path.IsPathFullyQualified(p) && SlotPool.IsSlotFile(p)
                && string.Equals(Path.GetDirectoryName(p), Folder, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException) { return []; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);
    /// <summary>The long form of an existing path (unchanged when Windows cannot expand it).</summary>
    public static string LongPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var buffer = new System.Text.StringBuilder(1024);
        uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }
}
