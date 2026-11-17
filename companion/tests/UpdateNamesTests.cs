using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// The versioned EXE name (2.48): which release asset the updater takes, the hashes it checks it against (and that
/// SHA256.txt stays what companions 2.44-2.47 read), and where the new version goes.
/// </summary>
public sealed class UpdateNamesTests : IDisposable
{
    private static readonly UpdateSource GitHub = UpdateSource.ForRepository("owner/soundpad-web")!;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-update-names-" + Guid.NewGuid().ToString("N"));
    public UpdateNamesTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);

    private static string Asset(string name, string tag = "v2.48.0", long size = 1234, string host = "https://github.com", string? digest = null) =>
        $$"""{"name":"{{name}}","size":{{size}},"browser_download_url":"{{host}}/owner/soundpad-web/releases/download/{{tag}}/{{name}}"{{(digest != null ? $",\"digest\":\"sha256:{digest}\"" : "")}}}""";
    private static string Release(params string[] assets) => $$"""{"tag_name":"v2.48.0","html_url":"https://github.com/owner/soundpad-web/releases/tag/v2.48.0","assets":[{{string.Join(',', assets)}}]}""";
    private static CheckResult Parse(string body) { using var client = new ReleaseClient(GitHub); return client.Parse(body, new(2, 47, 0)); }

    [Fact]
    public void TheVersionedAssetIsPreferredWithItsOwnDigestAndSha256Sums()
    {
        var found = Parse(Release(Asset("SoundpadWeb.exe", digest: new string('a', 64)), Asset("SoundpadWeb-2.48.0.exe", size: 999, digest: new string('b', 64)), Asset("SHA256.txt"), Asset("SHA256SUMS.txt"))).Release!;
        Assert.Equal("SoundpadWeb-2.48.0.exe", found.ExeName);
        Assert.Equal("https://github.com/owner/soundpad-web/releases/download/v2.48.0/SoundpadWeb-2.48.0.exe", found.ExeUrl.ToString());
        Assert.Equal((999L, new string('b', 64)), (found.ExeSize!.Value, found.ExeDigest));
        Assert.Equal("https://github.com/owner/soundpad-web/releases/download/v2.48.0/SHA256SUMS.txt", found.ShaUrl.ToString());
    }

    [Fact]
    public void AReleaseWithOnlyThePlainAssetIsStillInstalledFromSoundpadWebExe()
    {
        // As every release up to 2.47 is.
        var found = Parse(Release(Asset("SoundpadWeb.exe"), Asset("SHA256.txt"))).Release!;
        Assert.Equal(UpdateNames.Plain, found.ExeName);
        Assert.EndsWith("/v2.48.0/SoundpadWeb.exe", found.ExeUrl.ToString());
        Assert.EndsWith("/v2.48.0/SHA256.txt", found.ShaUrl.ToString());
        // A versioned asset of another version, or on a host the updater does not trust, is not this release's: the plain one is used.
        Assert.Equal(UpdateNames.Plain, Parse(Release(Asset("SoundpadWeb.exe"), Asset("SoundpadWeb-2.47.0.exe"), Asset("SHA256.txt"))).Release!.ExeName);
        Assert.Equal(UpdateNames.Plain, Parse(Release(Asset("SoundpadWeb.exe"), Asset("SoundpadWeb-2.48.0.exe", host: "https://evil.example"), Asset("SHA256.txt"))).Release!.ExeName);
        Assert.Equal(UpdateNames.Plain, Parse(Release(Asset("SoundpadWeb.exe"), Asset("SoundpadWeb 2.48.0.exe"), Asset("SHA256.txt"))).Release!.ExeName);
        // The versioned asset alone, with the bare SHA256.txt (the same bytes as SoundpadWeb.exe), is enough.
        var alone = Parse(Release(Asset("SoundpadWeb-2.48.0.exe"), Asset("SHA256.txt"))).Release!;
        Assert.Equal(("SoundpadWeb-2.48.0.exe", true), (alone.ExeName, alone.ShaUrl.ToString().EndsWith("/SHA256.txt")));
        // No EXE, or no hash file at all: nothing to install.
        Assert.Equal(UpdateReasons.NoAssets, Parse(Release(Asset("SoundpadWeb-2.48.0.exe"))).Reason);
        Assert.Equal(UpdateReasons.NoAssets, Parse(Release(Asset("SHA256.txt"), Asset("SHA256SUMS.txt"))).Reason);
        Assert.Equal(UpdateReasons.TooLarge, Parse(Release(Asset("SoundpadWeb-2.48.0.exe", size: ReleaseClient.MaxExeBytes + 1), Asset("SoundpadWeb.exe"), Asset("SHA256.txt"))).Reason);
    }

    [Fact]
    public void Sha256SumsNamesBothFilesWhileSha256TxtStaysTheBareHashOlderCompanionsRead()
    {
        string a = new string('A', 64), b = new string('b', 64);
        var sums = $"{a}  SoundpadWeb.exe\n{a}  SoundpadWeb-2.48.0.exe\n";
        Assert.Equal(a.ToLowerInvariant(), Sha256Text.ParseFor(sums, "SoundpadWeb-2.48.0.exe"));
        Assert.Equal(a.ToLowerInvariant(), Sha256Text.ParseFor(sums.Replace("\n", "\r\n"), "SoundpadWeb.exe"));
        Assert.Equal(b, Sha256Text.ParseFor($"{a}  SoundpadWeb.exe\n{b} *SoundpadWeb-2.48.0.exe", "SoundpadWeb-2.48.0.exe")); // sha256sum's binary marker
        Assert.Equal(a.ToLowerInvariant(), Sha256Text.ParseFor(a + "\r\n", "SoundpadWeb-2.48.0.exe")); // a bare hash is taken as it is
        Assert.Null(Sha256Text.ParseFor($"{a}  SoundpadWeb.exe\n", "SoundpadWeb-2.48.0.exe")); // its name is missing
        Assert.Null(Sha256Text.ParseFor($"{a}  SoundpadWeb-2.48.0.exe\n{b}  SoundpadWeb-2.48.0.exe\n", "SoundpadWeb-2.48.0.exe")); // listed twice, differently
        Assert.Null(Sha256Text.ParseFor($"{a}  SoundpadWeb-2.48.0.exe\nnot a line\n", "SoundpadWeb-2.48.0.exe"));
        Assert.Null(Sha256Text.ParseFor($"{a[..63]}  SoundpadWeb-2.48.0.exe\n", "SoundpadWeb-2.48.0.exe"));
        Assert.Null(Sha256Text.ParseFor("", "SoundpadWeb.exe"));
        // Companions 2.44-2.47 read SHA256.txt with Parse, which accepts the bare hash only: the release never puts names in it.
        Assert.Null(Sha256Text.Parse(sums));
        Assert.Equal(a.ToLowerInvariant(), Sha256Text.Parse(a + "\r\n")); // what Set-Content writes in the release workflow
    }

    [Fact]
    public void TheVersionedDownloadIsCheckedAgainstItsOwnLine()
    {
        var path = Path.Combine(folder, ".SoundpadWeb-2.47.0.x.download");
        File.Copy(typeof(Bridge).Assembly.Location, path);
        long size = new FileInfo(path).Length;
        var hash = UpdateVerifier.Hash(path);
        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion!.Split('+')[0];
        var info = new ReleaseInfo(version, "v" + version, "https://github.com/x", new("https://github.com/x"), size, null, new("https://github.com/y"), UpdateNames.Versioned(version));
        UpdateVerifier.Verify(path, info, size, null, $"{hash}  SoundpadWeb.exe\n{hash}  {UpdateNames.Versioned(version)}\n");
        UpdateVerifier.Verify(path, info, size, null, hash); // the bare SHA256.txt
        var wrong = Assert.Throws<UpdateRefusedException>(() => UpdateVerifier.Verify(path, info, size, null, $"{hash}  SoundpadWeb.exe\n{new string('0', 64)}  {UpdateNames.Versioned(version)}\n"));
        Assert.Equal(UpdateReasons.HashMismatch, wrong.Reason);
        var missing = Assert.Throws<UpdateRefusedException>(() => UpdateVerifier.Verify(path, info, size, null, $"{hash}  SoundpadWeb.exe\n"));
        Assert.Equal(UpdateReasons.HashMismatch, missing.Reason);
    }

    [Theory]
    [InlineData(@"C:\App\SoundpadWeb.exe", @"C:\App\SoundpadWeb.exe")] // in place, so shortcuts keep working
    [InlineData(@"C:\App\SoundpadWeb-2.47.0.exe", @"C:\App\SoundpadWeb-2.48.0.exe")]
    [InlineData(@"C:\App\SoundpadWeb 2.3.0.exe", @"C:\App\SoundpadWeb 2.48.0.exe")] // the host's own spelling keeps its space
    [InlineData(@"C:\App\soundpadweb-2.47.0.EXE", @"C:\App\soundpadweb-2.48.0.exe")]
    [InlineData(@"C:\App\SoundpadWeb-2.48.0.exe", @"C:\App\SoundpadWeb-2.48.0.exe")]
    [InlineData(@"C:\App\SoundpadWeb (1).exe", @"C:\App\SoundpadWeb (1).exe")] // any other name: in place
    [InlineData(@"C:\App\SoundpadWeb-2.47.exe", @"C:\App\SoundpadWeb-2.47.exe")]
    [InlineData(@"C:\App\SoundpadWeb-2.47.0-beta.exe", @"C:\App\SoundpadWeb-2.47.0-beta.exe")]
    [InlineData(@"C:\App\SoundpadWeb-2.47.0.previous.exe", @"C:\App\SoundpadWeb-2.47.0.previous.exe")]
    [InlineData(@"C:\App\MySoundpad.exe", @"C:\App\MySoundpad.exe")]
    public void TheNewVersionGoesToTheNewVersionedNameOnlyFromAVersionedName(string running, string target) =>
        Assert.Equal(target, UpdateNames.Target(running, "2.48.0"));

    [Fact]
    public void OnlyThePlainAndVersionedNamesUpdateThemselves()
    {
        foreach (var name in new[] { "SoundpadWeb.exe", "soundpadweb.EXE", "SoundpadWeb-2.48.0.exe", "SoundpadWeb 2.3.0.exe" }) Assert.True(UpdateNames.Updatable(name), name);
        foreach (var name in new[] { "SoundpadWeb.Harness.exe", "SoundpadWeb (1).exe", "SoundpadWeb-2.48.exe", "SoundpadWeb_2.48.0.exe", "SoundpadWeb-2.48.0.previous.exe", "SoundpadWeb.previous.exe", "SoundpadWeb.failed-2.48.0.exe", "dotnet.exe", "SoundpadWeb-2.48.0.exe.download" })
            Assert.False(UpdateNames.Updatable(name), name);
        Assert.Equal("SoundpadWeb-2.48.0.exe", UpdateNames.Versioned("2.48.0"));
        Assert.Equal("SoundpadWeb-2.48.0.exe", UpdateNames.ForMessages(@"C:\App\SoundpadWeb-2.48.0.exe"));
        Assert.Equal("\"SoundpadWeb 2.48.0.exe\"", UpdateNames.ForMessages(@"C:\App\SoundpadWeb 2.48.0.exe"));
        Assert.Equal("SoundpadWeb.exe", UpdateNames.ForMessages(@"C:\App\SoundpadWeb.Harness.exe"));
        Assert.Equal("SoundpadWeb.exe", UpdateNames.ForMessages(null));
    }

    [Fact]
    public void StalePreviousListsOnlyOlderVersionedPreviousExes()
    {
        foreach (var name in new[] { "SoundpadWeb-2.46.0.previous.exe", "SoundpadWeb 2.45.0.previous.exe", "SoundpadWeb-2.47.0.previous.exe", "SoundpadWeb.previous.exe", "SoundpadWeb-2.47.0.exe", "SoundpadWeb-x.previous.exe" })
            File.WriteAllText(Path.Combine(folder, name), "x");
        var stale = UpdateNames.StalePrevious(folder, Path.Combine(folder, "SoundpadWeb-2.47.0.previous.exe")).Select(f => Path.GetFileName(f)!).Order().ToArray();
        Assert.Equal(["SoundpadWeb 2.45.0.previous.exe", "SoundpadWeb-2.46.0.previous.exe"], stale);
        Assert.Empty(UpdateNames.StalePrevious(Path.Combine(folder, "missing"), "x"));
    }

    [Fact]
    public async Task TheUpdaterDownloadsTheVersionedAssetAndNeverReplacesAFileWithTheNewName()
    {
        var exe = Path.Combine(folder, "SoundpadWeb-1.0.0.exe");
        File.WriteAllText(exe, "the running version");
        var image = File.ReadAllBytes(typeof(Bridge).Assembly.Location);
        var product = FileVersionInfo.GetVersionInfo(typeof(Bridge).Assembly.Location).ProductVersion!.Split('+')[0];
        var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        var source = UpdateSource.ForTest("http://127.0.0.1:9/", out _)!;
        string Url(string name) => $"http://127.0.0.1:9/releases/download/v{product}/{name}";
        var versioned = UpdateNames.Versioned(product);
        var handler = new CannedHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/releases/latest" => CannedHandler.Json($$"""{"tag_name":"v{{product}}","assets":[{"name":"SoundpadWeb.exe","size":{{image.Length}},"browser_download_url":"{{Url("SoundpadWeb.exe")}}"},{"name":"{{versioned}}","size":{{image.Length}},"browser_download_url":"{{Url(versioned)}}"},{"name":"SHA256.txt","browser_download_url":"{{Url("SHA256.txt")}}"},{"name":"SHA256SUMS.txt","browser_download_url":"{{Url("SHA256SUMS.txt")}}"}]}"""),
            var p when p.EndsWith("/SHA256SUMS.txt") => CannedHandler.Json($"{hash}  SoundpadWeb.exe\n{hash}  {versioned}\n"),
            var p when p.EndsWith("/SHA256.txt") => CannedHandler.Json(hash + "\r\n"),
            var p when p.EndsWith("/" + versioned) => CannedHandler.Bytes(image),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
        });
        using var updater = new Updater(new UpdateOptions
        {
            Current = "1.0.0", Source = source, ExePath = exe, DataDir = folder, Handler = handler, LocationProblem = _ => null,
            Probe = (_, _) => Task.FromResult((0, "Copyright (c) 2017 Leppsoft")),
        });
        Assert.True(await updater.Check());
        Assert.True(await updater.Download(product));
        Assert.Equal("ready", updater.Status.State);
        Assert.True(updater.StillVerified());
        Assert.Contains("GET " + Url(versioned), handler.Requests);
        Assert.Contains("GET " + Url("SHA256SUMS.txt"), handler.Requests);
        Assert.DoesNotContain("GET " + Url("SoundpadWeb.exe"), handler.Requests);
        File.Delete(updater.DownloadPath!);
        // The new version's name is already taken beside the running EXE: refused before anything is downloaded.
        File.WriteAllText(Path.Combine(folder, versioned), "the host's own copy");
        using var taken = new Updater(new UpdateOptions { Current = "1.0.0", Source = source, ExePath = exe, DataDir = folder, Handler = handler, LocationProblem = _ => null, Probe = (_, _) => Task.FromResult((0, "2017 Leppsoft")) });
        Assert.True(await taken.Check());
        int before = handler.Requests.Count;
        Assert.False(await taken.Download(product));
        Assert.Equal(("failed", UpdateReasons.NameTaken), (taken.Status.State, taken.Status.Reason));
        Assert.Contains(versioned, taken.Status.Error);
        Assert.Equal(before, handler.Requests.Count);
        Assert.Equal("the host's own copy", File.ReadAllText(Path.Combine(folder, versioned)));
    }
}
