using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Canned HTTP answers for the updater (2.44). No request ever leaves the process.</summary>
internal sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public readonly List<string> Requests = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request.Method + " " + request.RequestUri);
        return Task.FromResult(answer(request));
    }
    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    public static HttpResponseMessage Redirect(string to) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute); return r; }
}

/// <summary>The release check, downloads and the checks of a downloaded EXE (2.44, WP2).</summary>
public sealed class UpdateReleaseTests : IDisposable
{
    private static readonly UpdateSource GitHub = UpdateSource.ForRepository("owner/soundpad-web")!;
    private static readonly Version Current = new(2, 44, 0);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-update-release-" + Guid.NewGuid().ToString("N"));
    public UpdateReleaseTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);

    private static string Release(string tag, bool draft = false, bool prerelease = false, string? digest = null, long size = 1234, string host = "https://github.com") =>
        $$"""
        {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://github.com/owner/soundpad-web/releases/tag/{{tag}}",
         "assets":[{"name":"SoundpadWeb.exe","size":{{size}},"browser_download_url":"{{host}}/owner/soundpad-web/releases/download/{{tag}}/SoundpadWeb.exe"{{(digest != null ? $",\"digest\":\"{digest}\"" : "")}}},
                   {"name":"SHA256.txt","size":66,"browser_download_url":"{{host}}/owner/soundpad-web/releases/download/{{tag}}/SHA256.txt"}]}
        """;

    private static async Task<CheckResult> Check(Func<HttpRequestMessage, HttpResponseMessage> answer, UpdateSource? source = null)
    {
        using var client = new ReleaseClient(source ?? GitHub, new CannedHandler(answer));
        return await client.Check(Current, CancellationToken.None);
    }

    [Fact]
    public async Task OnlyANewerPublishedVersionTagIsOffered()
    {
        // Drafts, prereleases, tags that are not vX.Y.Z, an old version rebuilt later and marked latest, the same version: nothing.
        foreach (var body in new[] { Release("v2.45.0", draft: true), Release("v2.45.0", prerelease: true), Release("v2.45"), Release("2.45.0"), Release("v2.45.0-beta"),
                     Release("v2.14.3"), Release("v2.44.0"), Release("v2.43.9") })
            Assert.Null((await Check(_ => CannedHandler.Json(body))).Release);
        var found = (await Check(_ => CannedHandler.Json(Release("v2.44.1", digest: "sha256:" + new string('a', 64))))).Release!;
        Assert.Equal(("2.44.1", "v2.44.1", 1234L, new string('a', 64)), (found.Version, found.Tag, found.ExeSize!.Value, found.ExeDigest));
        Assert.Equal("https://github.com/owner/soundpad-web/releases/download/v2.44.1/SHA256.txt", found.ShaUrl.ToString());
        Assert.True((await Check(_ => CannedHandler.Json(Release("v3.0.0")))).Release != null);
        Assert.True((await Check(_ => CannedHandler.Json(Release("v2.44.10")))).Release != null); // numbers, not text
    }

    [Fact]
    public async Task TheCheckAsksGitHubsApiWithAGenericAgentAndNoPersonalData()
    {
        HttpRequestMessage? seen = null;
        await Check(r => { seen = r; return CannedHandler.Json(Release("v2.44.1")); });
        Assert.Equal("https://api.github.com/repos/owner/soundpad-web/releases/latest", seen!.RequestUri!.ToString());
        Assert.Equal("SoundpadWeb-companion", seen.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", seen.Headers.Accept.ToString());
        Assert.Equal("2022-11-28", seen.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Null(seen.Headers.Authorization);
    }

    [Fact]
    public async Task AReleaseWithoutBothAssetsOrWithForeignAssetHostsIsNotOffered()
    {
        var missing = Release("v2.44.1").Replace("\"SHA256.txt\"", "\"notes.txt\"");
        Assert.Equal(UpdateReasons.NoAssets, (await Check(_ => CannedHandler.Json(missing))).Reason);
        Assert.Equal(UpdateReasons.NoAssets, (await Check(_ => CannedHandler.Json(Release("v2.44.1", host: "https://evil.example")))).Reason);
        Assert.Equal(UpdateReasons.NoAssets, (await Check(_ => CannedHandler.Json(Release("v2.44.1", host: "http://github.com")))).Reason);
        Assert.Equal(UpdateReasons.TooLarge, (await Check(_ => CannedHandler.Json(Release("v2.44.1", size: ReleaseClient.MaxExeBytes + 1)))).Reason);
    }

    [Theory]
    [InlineData(403, "x-ratelimit-remaining", "0")]
    [InlineData(429, "retry-after", "60")]
    [InlineData(403, "retry-after", "60")]
    [InlineData(304, null, null)]
    public async Task ARateLimitedApiFallsBackToTheLatestRedirect(int status, string? header, string? value)
    {
        var handler = new CannedHandler(r =>
        {
            if (r.RequestUri!.Host == "api.github.com")
            {
                var limited = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
                if (header != null) limited.Headers.TryAddWithoutValidation(header, value);
                return limited;
            }
            Assert.Equal(HttpMethod.Head, r.Method);
            Assert.Equal("https://github.com/owner/soundpad-web/releases/latest", r.RequestUri.ToString());
            return CannedHandler.Redirect("https://github.com/owner/soundpad-web/releases/tag/v2.44.2");
        });
        using var client = new ReleaseClient(GitHub, handler);
        var found = (await client.Check(Current, CancellationToken.None)).Release!;
        Assert.Equal("2.44.2", found.Version);
        Assert.Null(found.ExeSize); Assert.Null(found.ExeDigest); // the size comes from Content-Length then
        Assert.Equal("https://github.com/owner/soundpad-web/releases/download/v2.44.2/SoundpadWeb.exe", found.ExeUrl.ToString());
    }

    [Fact]
    public async Task ALimitWithoutAUsableRedirectFailsWithItsReasonAndOtherRefusalsAreNetworkFailures()
    {
        var limited = await Check(r => r.Method == HttpMethod.Head ? CannedHandler.Redirect("https://github.com/someone-else/repo/releases/tag/v9.0.0")
            : new HttpResponseMessage(HttpStatusCode.Forbidden) { Headers = { { "x-ratelimit-remaining", "0" } } });
        Assert.Equal(UpdateReasons.RateLimited, limited.Reason);
        var older = await Check(r => r.Method == HttpMethod.Head ? CannedHandler.Redirect("https://github.com/owner/soundpad-web/releases/tag/v2.40.0")
            : new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { { "retry-after", "5" } } });
        Assert.Null(older.Release); Assert.Null(older.Reason); // never a downgrade, even by redirect
        Assert.Equal(UpdateReasons.Network, (await Check(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))).Reason);
        Assert.Equal(UpdateReasons.Network, (await Check(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))).Reason);
        Assert.Equal(CheckResult.None, await Check(_ => new HttpResponseMessage(HttpStatusCode.NotFound))); // no release yet
        Assert.Equal(UpdateReasons.Network, (await Check(_ => throw new HttpRequestException("offline"))).Reason);
    }

    [Fact]
    public async Task DownloadsFollowRedirectsOnlyToGitHubOverHttps()
    {
        var bytes = RandomNumberGenerator.GetBytes(5000);
        async Task<string?> Fetch(string redirect)
        {
            using var client = new ReleaseClient(GitHub, new CannedHandler(r => r.RequestUri!.Host == "github.com" ? CannedHandler.Redirect(redirect) : CannedHandler.Bytes(bytes)));
            using var into = new MemoryStream();
            try { await client.Download(new("https://github.com/owner/soundpad-web/releases/download/v2.44.1/SoundpadWeb.exe"), into, 10000, null, CancellationToken.None); return into.Length == bytes.Length ? "ok" : "short"; }
            catch (UpdateRefusedException ex) { return ex.Reason; }
        }
        Assert.Equal("ok", await Fetch("https://objects.githubusercontent.com/github-production-release-asset/1/2?x=y"));
        Assert.Equal("ok", await Fetch("https://release-assets.githubusercontent.com/github-production-release-asset/1/2"));
        Assert.Equal(UpdateReasons.Network, await Fetch("http://objects.githubusercontent.com/x")); // never plain http
        Assert.Equal(UpdateReasons.Network, await Fetch("https://evil.example/SoundpadWeb.exe"));
        Assert.Equal(UpdateReasons.Network, await Fetch("https://objects.githubusercontent.com:8443/x")); // GitHub's hosts on their own port only
        // Too large, by Content-Length or by what arrives.
        using var big = new ReleaseClient(GitHub, new CannedHandler(_ => CannedHandler.Bytes(bytes)));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => big.Download(new("https://github.com/a/b/releases/download/v1.0.0/SoundpadWeb.exe"), new MemoryStream(), 4999, null, CancellationToken.None));
        // Redirect loops end.
        using var loop = new ReleaseClient(GitHub, new CannedHandler(r => CannedHandler.Redirect(r.RequestUri!.ToString())));
        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() => loop.Download(new("https://github.com/a/b/x"), new MemoryStream(), 10, null, CancellationToken.None));
        Assert.Contains("redirects", refused.Message);
    }

    [Fact]
    public void Sha256TextIsSixtyFourHexCharactersInAnyCaseWithOptionalLineEnd()
    {
        var hash = new string('A', 32) + new string('f', 32);
        Assert.Equal(hash.ToLowerInvariant(), Sha256Text.Parse(hash + "\r\n"));
        Assert.Equal(hash.ToLowerInvariant(), Sha256Text.Parse("  " + hash + "\n"));
        Assert.Null(Sha256Text.Parse(hash[..63]));
        Assert.Null(Sha256Text.Parse(hash + "0"));
        Assert.Null(Sha256Text.Parse(hash[..63] + "g"));
        Assert.Null(Sha256Text.Parse(hash + "  SoundpadWeb.exe")); // the release writes the hash alone
    }

    [Fact]
    public void ADownloadMustMatchItsSizeHashDigestAndVersion()
    {
        // A real PE with a real ProductVersion: the companion's own assembly.
        var source = typeof(Bridge).Assembly.Location;
        var path = Path.Combine(folder, ".SoundpadWeb.2.44.1.download");
        File.Copy(source, path);
        long size = new FileInfo(path).Length;
        var hash = UpdateVerifier.Hash(path);
        var product = FileVersionInfo.GetVersionInfo(path).ProductVersion!;
        var version = product.Split('+')[0];
        ReleaseInfo Info(long? bytes = null, string? digest = null, string? v = null) => new(v ?? version, "v" + (v ?? version), "https://github.com/x", new("https://github.com/x"), bytes, digest, new("https://github.com/y"));
        UpdateVerifier.Verify(path, Info(size), size, null, hash.ToUpperInvariant() + "\r\n");
        UpdateVerifier.Verify(path, Info(null, hash), size, size, hash); // the API digest, and Content-Length as the size
        string Refused(Action check) => Assert.Throws<UpdateRefusedException>(check).Reason;
        Assert.Equal(UpdateReasons.SizeMismatch, Refused(() => UpdateVerifier.Verify(path, Info(size + 1), size, null, hash)));
        Assert.Equal(UpdateReasons.SizeMismatch, Refused(() => UpdateVerifier.Verify(path, Info(null), size, size - 1, hash)));
        Assert.Equal(UpdateReasons.HashMismatch, Refused(() => UpdateVerifier.Verify(path, Info(size), size, null, new string('0', 64))));
        Assert.Equal(UpdateReasons.HashMismatch, Refused(() => UpdateVerifier.Verify(path, Info(size), size, null, "not a hash")));
        Assert.Equal(UpdateReasons.DigestMismatch, Refused(() => UpdateVerifier.Verify(path, Info(size, new string('0', 64)), size, null, hash)));
        Assert.Equal(UpdateReasons.VersionMismatch, Refused(() => UpdateVerifier.Verify(path, Info(size, null, "9.9.9"), size, null, hash)));
        // Not a program at all, with a matching hash.
        var text = Path.Combine(folder, "fake.download");
        File.WriteAllText(text, "MZ but not really a program");
        long textSize = new FileInfo(text).Length;
        Assert.Equal(UpdateReasons.VersionMismatch, Refused(() => UpdateVerifier.Verify(text, Info(textSize), textSize, null, UpdateVerifier.Hash(text))));
        Assert.True(UpdateVersion.ProductMatches("2.44.1+abc", "2.44.1"));
        Assert.True(UpdateVersion.ProductMatches("2.44.1", "2.44.1"));
        Assert.False(UpdateVersion.ProductMatches("2.44.10", "2.44.1"));
        Assert.False(UpdateVersion.ProductMatches(null, "2.44.1"));
    }

    [Fact]
    public void BuildsWithoutARepositoryDoNotUpdateAndTheTestSourceIsLoopbackOnly()
    {
        var none = new Updater(new UpdateOptions { Current = "2.44.0", Source = UpdateSource.ForRepository(null), ExePath = Path.Combine(folder, "SoundpadWeb.exe"), DataDir = folder });
        Assert.Equal(("unsupported", UpdateReasons.NoRepository), (none.Status.State, none.Status.Reason));
        var harness = new Updater(new UpdateOptions { Current = "2.44.0", Source = GitHub, ExePath = null, DataDir = folder });
        Assert.Equal(("unsupported", UpdateReasons.NoExe), (harness.Status.State, harness.Status.Reason));
        Assert.Null(UpdateSource.ForRepository("owner/../etc"));
        Assert.Null(UpdateSource.ForRepository("https://github.com/owner/repo"));
        foreach (var bad in new[] { "http://example.com:8080/", "http://192.168.1.2:8080/", "http://localhost:8080/", "http://127.0.0.1/", "ftp://127.0.0.1:21/", "http://127.0.0.1:8080/x", "http://u:p@127.0.0.1:8080/", "http://127.0.0.1:8080/?q=1", "http://127.0.0.2:8080/" })
            Assert.Null(UpdateSource.ForTest(bad, out _));
        var local = UpdateSource.ForTest("http://127.0.0.1:8080/", out var refusal)!;
        Assert.Null(refusal);
        Assert.NotNull(UpdateSource.ForTest("http://[::1]:8080/", out _));
        Assert.Equal("http://127.0.0.1:8080/api/releases/latest", local.ApiLatest.ToString());
        Assert.True(local.AllowedDownload(new("http://127.0.0.1:8080/releases/download/v2.44.1/SoundpadWeb.exe")));
        Assert.False(local.AllowedDownload(new("http://127.0.0.1:8081/x")));
        Assert.False(local.AllowedDownload(new("https://github.com/x"))); // a test source replaces GitHub completely
    }

    [Fact]
    public async Task TheUpdaterDownloadsVerifiesAndTestRunsBeforeItIsReady()
    {
        var exe = Path.Combine(folder, "SoundpadWeb.exe");
        File.WriteAllText(exe, "the running version");
        var image = File.ReadAllBytes(typeof(Bridge).Assembly.Location);
        var product = FileVersionInfo.GetVersionInfo(typeof(Bridge).Assembly.Location).ProductVersion!.Split('+')[0];
        var current = "1.0.0";
        var hash = Convert.ToHexString(SHA256.HashData(image));
        var source = UpdateSource.ForTest("http://127.0.0.1:9/", out _)!;
        string? probeRan = null; int probeExit = 0;
        var handler = new CannedHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/releases/latest" => CannedHandler.Json($$"""{"tag_name":"v{{product}}","assets":[{"name":"SoundpadWeb.exe","size":{{image.Length}},"browser_download_url":"http://127.0.0.1:9/releases/download/v{{product}}/SoundpadWeb.exe"},{"name":"SHA256.txt","browser_download_url":"http://127.0.0.1:9/releases/download/v{{product}}/SHA256.txt"}]}"""),
            var p when p.EndsWith("SHA256.txt") => CannedHandler.Json(hash + "\r\n"),
            _ => CannedHandler.Bytes(image),
        });
        using var updater = new Updater(new UpdateOptions
        {
            Current = current, Source = source, ExePath = exe, DataDir = folder, Handler = handler, LocationProblem = _ => null,
            Probe = (path, _) => { probeRan = path; return Task.FromResult((probeExit, probeExit == 0 ? "MIT License\nCopyright (c) 2017 Leppsoft" : "")); },
        });
        var states = new List<string>(); updater.Changed += () => { lock (states) states.Add(updater.Status.State); };
        Assert.True(await updater.Check());
        Assert.Equal(("available", product), (updater.Status.State, updater.Status.Latest));
        Assert.True(await updater.Download(product));
        Assert.Equal("ready", updater.Status.State);
        Assert.Equal(Path.Combine(folder, $".SoundpadWeb.{product}.download"), updater.DownloadPath);
        Assert.Equal(updater.DownloadPath, probeRan); // the test run is of the checked file
        Assert.Contains("verifying", states); Assert.Contains("downloading", states);
        // A test run that fails (Windows or an antivirus blocked it): failed, said plainly, and the file is gone.
        probeExit = 1;
        Assert.True(await updater.Check());
        Assert.False(await updater.Download(product));
        Assert.Equal(("failed", UpdateReasons.Blocked), (updater.Status.State, updater.Status.Reason));
        Assert.False(File.Exists(Path.Combine(folder, $".SoundpadWeb.{product}.download")));
        // Later is remembered for this launch.
        updater.Later(); Assert.True(updater.Status.Offered);
        // A folder that cannot take the update: unsupported, still with the version and its page.
        using var readOnly = new Updater(new UpdateOptions { Current = current, Source = source, ExePath = exe, DataDir = folder, Handler = handler, LocationProblem = _ => UpdateReasons.NotWritable });
        Assert.True(await readOnly.Check());
        Assert.Equal(("unsupported", UpdateReasons.NotWritable, product), (readOnly.Status.State, readOnly.Status.Reason, readOnly.Status.Latest));
        Assert.NotNull(readOnly.Status.NotesUrl);
    }

    [Fact]
    public void UpdateSettingsAreStrictSmallAndKeepUnknownFields()
    {
        var path = Path.Combine(folder, "update-settings.v1.json");
        var file = new UpdateSettingsFile(path);
        Assert.True(file.CheckAtLaunch()); // no file: on
        File.WriteAllText(path, """{"format":1,"checkAtLaunch":false,"later":{"x":1}}""");
        Assert.False(file.CheckAtLaunch());
        file.Save(true);
        Assert.Contains("\"later\":{\"x\":1}", File.ReadAllText(path));
        foreach (var damaged in new[] { "{", """{"format":2,"checkAtLaunch":false}""", """{"format":1,"checkAtLaunch":"no"}""", """{"checkAtLaunch":false}""", "[]", "{\"format\":1,\"pad\":\"" + new string('x', 5000) + "\"}" })
        {
            File.WriteAllText(path, damaged);
            Assert.True(file.CheckAtLaunch()); // damaged: the default
            Assert.True(file.Damaged);
            Assert.Equal(damaged, File.ReadAllText(path)); // and left as it was
        }
    }
}
