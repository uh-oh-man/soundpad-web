namespace SoundpadWeb;

public enum HistoryEntryKind { File, Directory, Other }

/// <summary>One entry of a folder. Other covers links and anything else that is not a plain file or folder.</summary>
public sealed record HistoryFileEntry(string Name, HistoryEntryKind Kind);

/// <summary>
/// The few file operations <see cref="HistoryStore"/> needs, so tests can slow them down or make them fail.
/// Every call opens and closes its own handle; the store never keeps a file open between calls.
/// </summary>
public interface IHistoryFiles
{
    void CreateDirectory(string path);
    /// <summary>Entries directly inside <paramref name="directory"/>; empty when it does not exist. Never recurses.</summary>
    IReadOnlyList<HistoryFileEntry> Entries(string directory);
    /// <summary>Length in bytes, or -1 when the file does not exist.</summary>
    long Length(string path);
    /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/> (creating the file) and flushes it to the device before returning.</summary>
    void Write(string path, long offset, ReadOnlySpan<byte> data);
    /// <summary>Fills as much of <paramref name="destination"/> as the file holds from <paramref name="offset"/>; returns the byte count. Throws FileNotFoundException when the file is gone.</summary>
    int Read(string path, long offset, Span<byte> destination);
    /// <summary>Cuts the file to <paramref name="length"/> bytes.</summary>
    void Truncate(string path, long length);
    /// <summary>Replaces the file in one step: write a temporary file (path + ".tmp"), flush it, rename it over the target.</summary>
    void ReplaceAtomically(string path, ReadOnlySpan<byte> data);
    /// <summary>Deletes one file; a missing file is not an error.</summary>
    void Delete(string path);
    /// <summary>Removes the folder only when it is empty; returns whether it is gone.</summary>
    bool RemoveDirectoryIfEmpty(string path);
}

/// <summary>The real disk.</summary>
public sealed class HistoryDiskFiles : IHistoryFiles
{
    public static HistoryDiskFiles Instance { get; } = new();
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public IReadOnlyList<HistoryFileEntry> Entries(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) return [];
        return [.. info.EnumerateFileSystemInfos().Select(e => new HistoryFileEntry(e.Name,
            (e.Attributes & FileAttributes.ReparsePoint) != 0 ? HistoryEntryKind.Other : (e.Attributes & FileAttributes.Directory) != 0 ? HistoryEntryKind.Directory : HistoryEntryKind.File))];
    }
    public long Length(string path) { var info = new FileInfo(path); return info.Exists ? info.Length : -1; }
    public void Write(string path, long offset, ReadOnlySpan<byte> data)
    {
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Delete, 1, FileOptions.None);
        file.Position = offset; file.Write(data); file.Flush(true);
    }
    public int Read(string path, long offset, Span<byte> destination)
    {
        // Delete sharing lets retention remove a segment while a reader still has it open; the reader then finishes with complete data.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        file.Position = offset;
        return file.ReadAtLeast(destination, destination.Length, false);
    }
    public void Truncate(string path, long length)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete);
        file.SetLength(length); file.Flush(true);
    }
    public void ReplaceAtomically(string path, ReadOnlySpan<byte> data)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough)) { file.Write(data); file.Flush(true); }
        File.Move(temp, path, true);
    }
    public void Delete(string path)
    {
        try { File.Delete(path); } catch (DirectoryNotFoundException) { }
    }
    public bool RemoveDirectoryIfEmpty(string path)
    {
        if (!Directory.Exists(path)) return true;
        if (Directory.EnumerateFileSystemEntries(path).Any()) return false;
        Directory.Delete(path, false); return true;
    }
}
