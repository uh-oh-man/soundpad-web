using System.Text.RegularExpressions;
namespace SoundpadWeb;

/// <summary>
/// The companion EXE's file names (2.48). Every release publishes the same bytes twice: <c>SoundpadWeb.exe</c> (what
/// companions 2.44–2.47 update from, and what releases/latest/download/SoundpadWeb.exe links to) and
/// <c>SoundpadWeb-&lt;version&gt;.exe</c>. GitHub turns spaces in asset names into dots, so the published name uses a
/// hyphen; a file the host renamed to "SoundpadWeb 2.48.0.exe" is recognised too.
/// <para>
/// The update target: a running EXE with a versioned name is updated to the new version's name in the same folder,
/// keeping its separator ("SoundpadWeb-2.47.0.exe" → "SoundpadWeb-2.48.0.exe"); "SoundpadWeb.exe" and any other
/// name is updated in place, so shortcuts to it keep working.
/// </para>
/// </summary>
public static partial class UpdateNames
{
    public const string Plain = "SoundpadWeb.exe";
    public const string Stem = "SoundpadWeb";

    /// <summary>The versioned release asset, "SoundpadWeb-2.48.0.exe".</summary>
    public static string Versioned(string version) => $"{Stem}-{version}.exe";

    [GeneratedRegex(@"^(SoundpadWeb)([- ])(\d{1,5}\.\d{1,5}\.\d{1,6})\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedPattern();
    [GeneratedRegex(@"^SoundpadWeb[- ]\d{1,5}\.\d{1,5}\.\d{1,6}\.previous\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedPreviousPattern();

    /// <summary>"SoundpadWeb-X.Y.Z.exe" or "SoundpadWeb X.Y.Z.exe" (any case): the stem as written, the separator and the version.</summary>
    public static bool TryVersioned(string fileName, out string stem, out char separator, out string version)
    {
        var m = VersionedPattern().Match(fileName);
        stem = m.Success ? m.Groups[1].Value : ""; separator = m.Success ? m.Groups[2].Value[0] : '\0'; version = m.Success ? m.Groups[3].Value : "";
        return m.Success;
    }

    /// <summary>Names that update themselves: "SoundpadWeb.exe" and the versioned names. Anything else (a dev build,
    /// the harness, "SoundpadWeb (1).exe") stays "This build doesn't update itself", as before 2.48.</summary>
    public static bool Updatable(string fileName) => fileName.Equals(Plain, StringComparison.OrdinalIgnoreCase) || TryVersioned(fileName, out _, out _, out _);

    /// <summary>Where version <paramref name="newVersion"/> goes: the new versioned name beside a versioned EXE, else
    /// <paramref name="exePath"/> itself (in place).</summary>
    public static string Target(string exePath, string newVersion)
    {
        if (!TryVersioned(Path.GetFileName(exePath), out var stem, out var separator, out _)) return exePath;
        return Path.Combine(Path.GetDirectoryName(exePath)!, $"{stem}{separator}{newVersion}.exe");
    }

    /// <summary>
    /// The earlier versions' "SoundpadWeb-X.Y.Z.previous.exe" in <paramref name="folder"/>, other than
    /// <paramref name="keep"/>: after a successful update from a versioned name only the newest previous version stays
    /// (as one SoundpadWeb.previous.exe does for the plain name). The plain name's previous is never listed.
    /// </summary>
    public static IReadOnlyList<string> StalePrevious(string folder, string keep)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            return [.. Directory.EnumerateFiles(folder, Stem + "*.previous.exe")
                .Where(f => VersionedPreviousPattern().IsMatch(Path.GetFileName(f)) && !string.Equals(Path.GetFullPath(f), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>How the host runs this EXE by hand in a message: its own name when it is one of ours, else SoundpadWeb.exe.</summary>
    public static string ForMessages(string? processPath)
    {
        var name = processPath == null ? null : Path.GetFileName(processPath);
        if (name == null || !Updatable(name)) return Plain;
        return name.Contains(' ') ? $"\"{name}\"" : name;
    }
}
