using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace SoundpadWeb;

internal static class ManagedFiles
{
    public static string Hash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
    public static void LocalSource(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !File.Exists(path) || new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("The source must be an existing local audio file up to 512 MiB.");
    }
    public static string Name(string value)
    {
        var result = new string(value.Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)).Take(60).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "Audio" : result;
    }
    public static bool Beneath(string path, string root)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var full = Path.GetFullPath(path); var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(@"\\");
    }
    public static void RequireOwnedPath(string path, string root)
    {
        if (!Beneath(path, root)) throw new InvalidDataException("The journal references a file outside its owned storage. Cleanup is blocked.");
        var current = new FileInfo(path).Directory;
        while (current != null && (Beneath(current.FullName, root) || string.Equals(current.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)))
        {
            if (current.Exists && current.LinkTarget != null) throw new InvalidDataException("A managed directory is a link. Cleanup is blocked.");
            current = current.Parent;
        }
        if (File.Exists(path) && new FileInfo(path).LinkTarget != null) throw new InvalidDataException("A managed file was replaced by a link. Cleanup is blocked.");
    }
    public static bool Verified(string path, string hash, long bytes)
    {
        try { return hash.Length == 64 && File.Exists(path) && new FileInfo(path).Length == bytes && Hash(path) == hash; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public static void CopyNew(string source, string destination, string expectedHash, long bytes)
    {
        if (File.Exists(destination))
        {
            if (!Verified(destination, expectedHash, bytes)) throw new InvalidDataException("The permanent destination contains a conflicting file. Inspect it and move the conflict aside before Retry recovery; the source has been preserved.");
            return;
        }
        for(var parent=new DirectoryInfo(Path.GetDirectoryName(destination)!);parent!=null;parent=parent.Parent)
            if(parent.Exists && parent.LinkTarget!=null) throw new InvalidDataException("Choose a real permanent directory; linked directory destinations are not supported.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        RequireOwnedPath(destination,Path.GetDirectoryName(destination)!);
        // The journal records the final GUID destination before copying. A deterministic staging
        // file can resume after a crash, but only if every existing byte matches the source prefix.
        var staging = destination + ".copying"; RequireOwnedPath(staging,Path.GetDirectoryName(destination)!);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(staging, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if(input.Length!=bytes || output.Length>bytes) throw new InvalidDataException("The permanent staging file conflicts with the preserved source.");
            var buffer = new byte[65536];var existing=new byte[65536];long remaining=output.Length;
            while(remaining>0)
            {
                int count=(int)Math.Min(buffer.Length,remaining);input.ReadExactly(buffer.AsSpan(0,count));output.ReadExactly(existing.AsSpan(0,count));
                if(!buffer.AsSpan(0,count).SequenceEqual(existing.AsSpan(0,count))) throw new InvalidDataException("The permanent staging file was modified. Preserve it for inspection, move the conflicting staging file aside, then Retry recovery.");remaining-=count;
            }
            input.CopyTo(output); output.Flush(true);input.Position=0;output.Position=0;
            if(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()!=expectedHash || Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant()!=expectedHash) throw new InvalidDataException("The durable copy failed verification. The temporary source has been preserved.");
        }
        File.Move(staging,destination,false);
        if (!Verified(destination, expectedHash, bytes)) throw new InvalidDataException("The durable copy failed verification. The temporary source has been preserved.");
    }
    // Keep an exclusive DELETE-capable handle from verification through deletion. This prevents
    // another writer swapping the bytes between a hash check and File.Delete(path).
    public static void DeleteVerified(string path, string hash, long bytes, string root)
    {
        RequireOwnedPath(path, root); if (!File.Exists(path)) return;
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Owned file cleanup requires the Windows handle verifier.");
        using var handle = CreateFile(path, 0x80000000u | 0x00010000u, 0, IntPtr.Zero, 3, 0x08000080, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The managed file is in use. Cleanup will retry.", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot verify the managed file's final path.");
        var resolved = buffer.ToString(); if (resolved.StartsWith(@"\\?\")) resolved = resolved[4..];
        if (!string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !Beneath(resolved, root)) throw new InvalidDataException("Managed storage changed during cleanup. The file was preserved.");
        using var stream = new FileStream(handle, FileAccess.Read);
        if (hash.Length != 64 || stream.Length != bytes || Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != hash) throw new InvalidDataException("The managed file was modified outside the app. It has been preserved.");
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw new IOException("Windows could not complete safe file cleanup.", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { public byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDisposition information, uint size);
}
