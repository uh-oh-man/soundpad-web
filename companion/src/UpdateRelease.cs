using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

/// <summary>
/// Where updates come from (2.44). Release builds name this project's GitHub repository (AssemblyMetadata
/// UpdateRepository, set only by CI); <c>--update-source</c> replaces GitHub with a local test server, and is accepted
/// only for a loopback address (http(s)://127.0.0.1:&lt;port&gt;/ or [::1]). Nothing else can point the updater elsewhere.
/// </summary>
public sealed partial class UpdateSource
{
    public string? Repository { get; }
    public Uri? TestBase { get; }
    private UpdateSource(string? repository, Uri? testBase) { Repository = repository; TestBase = testBase; }

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")] private static partial Regex RepositoryPattern();

    /// <summary>The GitHub repository "owner/name", or null when it is missing or malformed.</summary>
    public static UpdateSource? ForRepository(string? repository) => repository != null && RepositoryPattern().IsMatch(repository) && !repository.Contains("..") ? new(repository, null) : null;

    /// <summary>A loopback test source, or null (with the reason) for anything else.</summary>
    public static UpdateSource? ForTest(string url, out string? refusal)
    {
        refusal = "--update-source must be http(s)://127.0.0.1:<port>/ or http(s)://[::1]:<port>/.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host is not ("127.0.0.1" or "[::1]")
            || uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !uri.AbsolutePath.EndsWith('/')) return null;
        refusal = null;
        return new(null, uri);
    }

    public Uri ApiLatest => TestBase != null ? new(TestBase, "api/releases/latest") : new($"https://api.github.com/repos/{Repository}/releases/latest");
    public Uri WebLatest => TestBase != null ? new(TestBase, "releases/latest") : new($"https://github.com/{Repository}/releases/latest");
    public Uri Download(string tag, string name) => TestBase != null ? new(TestBase, $"releases/download/{tag}/{name}") : new($"https://github.com/{Repository}/releases/download/{tag}/{name}");
    public string ReleasePage(string tag) => TestBase != null ? new Uri(TestBase, $"releases/tag/{tag}").ToString() : $"https://github.com/{Repository}/releases/tag/{tag}";
    public string ReleasesPage => TestBase != null ? new Uri(TestBase, "releases").ToString() : $"https://github.com/{Repository}/releases";

    /// <summary>Hosts a download (or one of its redirects) may come from: GitHub's own, over HTTPS only; or the test server.</summary>
    public bool AllowedDownload(Uri uri)
    {
        if (TestBase != null) return uri.Scheme == TestBase.Scheme && uri.Host == TestBase.Host && uri.Port == TestBase.Port;
        return uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && uri.Host is "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";
    }

    /// <summary>A redirect of <see cref="WebLatest"/> to this repository's tag page, as GitHub answers without the API.</summary>
    public string? TagFromRedirect(Uri location)
    {
        if (TestBase == null && (location.Scheme != "https" || location.Host != "github.com")) return null;
        if (TestBase != null && !AllowedDownload(location)) return null;
        var expected = TestBase != null ? new Uri(TestBase, "releases/tag/").AbsolutePath : $"/{Repository}/releases/tag/";
        if (!location.AbsolutePath.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return null;
        var tag = location.AbsolutePath[expected.Length..];
        return UpdateVersion.Parse(tag) != null ? tag : null;
    }
}

/// <summary>Release tags: exactly vX.Y.Z, compared as numbers.</summary>
public static partial class UpdateVersion
{
    [GeneratedRegex(@"^v(\d{1,5})\.(\d{1,5})\.(\d{1,6})$")] private static partial Regex TagPattern();
    public static Version? Parse(string? tag) => tag != null && TagPattern().Match(tag) is { Success: true } m ? new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    public static Version? ParsePlain(string? version) => version == null ? null : Parse("v" + version);
    /// <summary>"2.44.1" for a ProductVersion of "2.44.1" or "2.44.1+abcdef" (never "2.44.10").</summary>
    public static bool ProductMatches(string? product, string version) => product != null && product.StartsWith(version, StringComparison.Ordinal) && (product.Length == version.Length || product[version.Length] is '+' or '-');
}

/// <summary>The outcome of one check: nothing newer, a newer release, or why it failed.</summary>
public sealed record CheckResult(ReleaseInfo? Release, string? Reason = null, string? Error = null)
{
    public static readonly CheckResult None = new((ReleaseInfo?)null);
}

/// <summary>
/// Talks to GitHub (or the test server) for the updater (2.44): the latest-release check, with a fallback to the
/// releases/latest redirect when the API is rate limited, and size-capped HTTPS downloads that follow redirects by hand,
/// only to GitHub's own hosts. Sends a generic User-Agent and nothing about the person.
/// </summary>
public sealed class ReleaseClient : IDisposable
{
    public const long MaxExeBytes = 256L * 1024 * 1024;
    public const int MaxShaBytes = 4096;
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10), IdleTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient http;
    private readonly UpdateSource source;
    public TimeSpan Idle { get; init; } = IdleTimeout;

    /// <param name="handler">Tests pass a canned handler. It must not follow redirects itself.</param>
    public ReleaseClient(UpdateSource source, HttpMessageHandler? handler = null)
    {
        this.source = source;
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SoundpadWeb-companion");
    }
    public void Dispose() => http.Dispose();

    /// <summary>Is there a release newer than <paramref name="current"/>? Never a downgrade, a draft or a prerelease.</summary>
    public async Task<CheckResult> Check(Version current, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.ApiLatest);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return CheckResult.None; // no release yet
            if (RateLimited(response)) return await CheckByRedirect(current, timeout.Token);
            if (!response.IsSuccessStatusCode) return new(null, UpdateReasons.Network, $"GitHub answered {(int)response.StatusCode}.");
            var body = await ReadCapped(response, 2 * 1024 * 1024, timeout.Token);
            return Parse(body, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException)
        {
            if (cancel.IsCancellationRequested) throw;
            return new(null, UpdateReasons.Network, ex is OperationCanceledException ? "GitHub did not answer in time." : "GitHub could not be reached.");
        }
    }

    private static bool RateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.NotModified
        || (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && (response.Headers.RetryAfter != null || response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0"));

    /// <summary>Rate limited: the releases/latest page redirects to the latest tag without using the API.</summary>
    private async Task<CheckResult> CheckByRedirect(Version current, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, source.WebLatest);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var absolute = location.IsAbsoluteUri ? location : new Uri(source.WebLatest, location);
                if (source.TagFromRedirect(absolute) is { } tag)
                {
                    var version = UpdateVersion.Parse(tag)!;
                    if (version <= current) return CheckResult.None;
                    return new(new(Plain(version), tag, source.ReleasePage(tag), source.Download(tag, UpdateNames.Plain), null, null, source.Download(tag, "SHA256.txt")));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { }
        return new(null, UpdateReasons.RateLimited, "GitHub's limit for update checks was reached. Try again later.");
    }

    private static string Plain(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>The releases/latest API answer.</summary>
    public CheckResult Parse(string body, Version current)
    {
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unexpected release answer.");
        bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        if (Flag("draft") || Flag("prerelease")) return CheckResult.None;
        var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (UpdateVersion.Parse(tag) is not { } version || version <= current) return CheckResult.None;
        string notes = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String && Uri.TryCreate(html.GetString(), UriKind.Absolute, out var page) && (source.TestBase != null ? source.AllowedDownload(page) : page.Scheme == "https" && page.Host == "github.com") ? page.ToString() : source.ReleasePage(tag!);
        // 2.48: the versioned asset (SoundpadWeb-<version>.exe) is preferred, SoundpadWeb.exe is the fallback (every
        // release has it, the same bytes); SHA256SUMS.txt names each file, SHA256.txt is the bare hash 2.44-2.47 read.
        (Uri Url, long? Size, string? Digest)? plain = null, versioned = null; Uri? sha = null, sums = null;
        var versionedName = UpdateNames.Versioned(Plain(version));
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray().Take(64))
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                if (!asset.TryGetProperty("browser_download_url", out var u) || u.ValueKind != JsonValueKind.String || !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var url) || !source.AllowedDownload(url)) continue;
                var name = n.GetString();
                if (name == UpdateNames.Plain || name == versionedName)
                {
                    long? size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long bytes) && bytes > 0 ? bytes : null;
                    string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { } dg && dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && Sha256Text.IsHex(dg[7..]) ? dg[7..].ToLowerInvariant() : null;
                    if (name == UpdateNames.Plain) plain = (url, size, digest); else versioned = (url, size, digest);
                }
                else if (name == "SHA256.txt") sha = url;
                else if (name == Sha256Text.SumsName) sums = url;
            }
        var exe = versioned ?? plain;
        var exeName = versioned != null ? versionedName : UpdateNames.Plain;
        if (exe == null || (sha ?? sums) == null) return new(null, UpdateReasons.NoAssets, $"Release {tag} has no SoundpadWeb.exe and SHA256.txt.");
        if (exe.Value.Size > MaxExeBytes) return new(null, UpdateReasons.TooLarge, "The new version is larger than the updater accepts.");
        return new(new(Plain(version), tag!, notes, exe.Value.Url, exe.Value.Size, exe.Value.Digest, (sums ?? sha)!, exeName));
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="destination"/>, following up to 5 redirects by hand and only
    /// to allowed hosts. Refuses more than <paramref name="cap"/> bytes; gives up after <see cref="Idle"/> without data.
    /// Returns the bytes written and the Content-Length of the final answer (null when absent).
    /// </summary>
    public async Task<(long Bytes, long? Length)> Download(Uri url, Stream destination, long cap, Action<long, long?>? progress, CancellationToken cancel)
    {
        var next = url;
        for (int hop = 0; hop <= 5; hop++)
        {
            if (!source.AllowedDownload(next)) throw new UpdateRefusedException(UpdateReasons.Network, "The download was sent to a host the updater does not trust.");
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            idle.CancelAfter(Idle);
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (response.Headers.Location is not { } location) throw new UpdateRefusedException(UpdateReasons.Network, "GitHub sent a redirect without a target.");
                next = location.IsAbsoluteUri ? location : new Uri(next, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new UpdateRefusedException(UpdateReasons.Network, $"The download failed ({(int)response.StatusCode}).");
            long? length = response.Content.Headers.ContentLength;
            if (length > cap) throw new UpdateRefusedException(UpdateReasons.TooLarge, "The new version is larger than the updater accepts.");
            await using var body = await response.Content.ReadAsStreamAsync(idle.Token);
            var buffer = new byte[81920]; long total = 0; int read;
            while (true)
            {
                idle.CancelAfter(Idle);
                try { read = await body.ReadAsync(buffer, idle.Token); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new UpdateRefusedException(UpdateReasons.Network, "The download stalled."); }
                if (read == 0) break;
                total += read;
                if (total > cap) throw new UpdateRefusedException(UpdateReasons.TooLarge, "The new version is larger than the updater accepts.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancel);
                progress?.Invoke(total, length);
            }
            return (total, length);
        }
        throw new UpdateRefusedException(UpdateReasons.Network, "Too many redirects.");
    }

    private static async Task<string> ReadCapped(HttpResponseMessage response, int cap, CancellationToken cancel)
    {
        if (response.Content.Headers.ContentLength > cap) throw new InvalidDataException("Answer too large.");
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        using var copy = new MemoryStream();
        var buffer = new byte[16384]; int read;
        while ((read = await body.ReadAsync(buffer, cancel)) > 0) { copy.Write(buffer, 0, read); if (copy.Length > cap) throw new InvalidDataException("Answer too large."); }
        return System.Text.Encoding.UTF8.GetString(copy.ToArray());
    }
}

/// <summary>A refusal with a reason word, shown to the host in plain words.</summary>
public sealed class UpdateRefusedException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The release's hashes. SHA256.txt: 64 hex characters (any case), then optional whitespace, and nothing else: that
/// is all companions 2.44-2.47 accept, so it never gains names. SHA256SUMS.txt (2.48): one "&lt;hash&gt;  &lt;name&gt;"
/// line per EXE, as sha256sum writes it, for both SoundpadWeb.exe and SoundpadWeb-&lt;version&gt;.exe.
/// </summary>
public static partial class Sha256Text
{
    public const string SumsName = "SHA256SUMS.txt";
    public static bool IsHex(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
    public static string? Parse(string text)
    {
        var trimmed = text.Trim();
        return IsHex(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"^([0-9A-Fa-f]{64}) [ *](.+)$")] private static partial Regex SumsLine();

    /// <summary>
    /// The hash of <paramref name="name"/>: a bare hash (SHA256.txt) is taken as it is; otherwise the one
    /// SHA256SUMS.txt line for exactly that name (null when it is missing, listed twice with different hashes, or any
    /// line is not a hash and a name).
    /// </summary>
    public static string? ParseFor(string text, string name)
    {
        if (Parse(text) is { } bare) return bare;
        string? found = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0) continue;
            if (SumsLine().Match(line) is not { Success: true } m) return null;
            if (m.Groups[2].Value != name) continue;
            var hash = m.Groups[1].Value.ToLowerInvariant();
            if (found != null && found != hash) return null;
            found = hash;
        }
        return found;
    }
}

/// <summary>
/// The checks a downloaded EXE must pass before it may replace the running one (2.44), in this order: its size, its
/// SHA-256 against SHA256.txt, the API digest when there is one, a PE file whose ProductVersion is the release's, and
/// finally a test run of <c>--licenses</c>. The test run happens only after the hash matched.
/// </summary>
public static class UpdateVerifier
{
    /// <summary>A line <c>--licenses</c> always prints (the Soundpad notice): the test run must show it.</summary>
    public const string NoticesMarker = "2017 Leppsoft";

    public static string Hash(string path)
    {
        using var stream = OpenWithRetry(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void Verify(string path, ReleaseInfo release, long bytes, long? contentLength, string sha256Text)
    {
        long? expected = release.ExeSize ?? contentLength;
        if (expected is long size && bytes != size) throw new UpdateRefusedException(UpdateReasons.SizeMismatch, "The download is incomplete. Try again.");
        if (new FileInfo(path).Length != bytes) throw new UpdateRefusedException(UpdateReasons.SizeMismatch, "The download is incomplete. Try again.");
        var published = Sha256Text.ParseFor(sha256Text, release.ExeName) ?? throw new UpdateRefusedException(UpdateReasons.HashMismatch, $"The release's hashes do not include {release.ExeName}.");
        var actual = Hash(path);
        if (actual != published) throw new UpdateRefusedException(UpdateReasons.HashMismatch, "The download does not match the hash published with the release.");
        if (release.ExeDigest != null && release.ExeDigest != actual) throw new UpdateRefusedException(UpdateReasons.DigestMismatch, "The download does not match GitHub's digest for the release.");
        if (!IsPe(path)) throw new UpdateRefusedException(UpdateReasons.VersionMismatch, "The download is not a Windows program.");
        var product = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        if (!UpdateVersion.ProductMatches(product, release.Version)) throw new UpdateRefusedException(UpdateReasons.VersionMismatch, $"The download says it is version {product ?? "unknown"}, not {release.Version}.");
    }

    public static bool IsPe(string path)
    {
        try
        {
            using var stream = OpenWithRetry(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false;
            stream.Position = 0x3C; int offset = reader.ReadInt32();
            if (offset <= 0 || offset > stream.Length - 4) return false;
            stream.Position = offset;
            return reader.ReadUInt32() == 0x00004550;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Runs the new EXE once with <c>--licenses</c> (no window, output read here, 20 s): Windows or an antivirus that would
    /// block it blocks this run too, while the old version is still in place. Exit 0 and the notices marker pass.
    /// </summary>
    public static async Task<(int Exit, string Output)> Probe(string path, CancellationToken cancel)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(path)! };
                info.ArgumentList.Add("--licenses");
                using var process = Process.Start(info) ?? throw new IOException("The test run did not start.");
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(cancel);
                var errors = process.StandardError.ReadToEndAsync(cancel);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { process.Kill(true); } catch (Exception) { } return (-1, "timed out"); }
                return (process.ExitCode, await output + await errors);
            }
            // An antivirus scanning the new file can hold it for a moment (as Persistence.cs retries).
            catch (Exception ex) when (attempt < 8 && ex is System.ComponentModel.Win32Exception or IOException) { await Task.Delay(250 * (attempt + 1), cancel); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { return (-1, ex.Message); }
        }
    }

    public static FileStream OpenWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException) when (attempt < 40) { Thread.Sleep(25); }
        }
    }
}
