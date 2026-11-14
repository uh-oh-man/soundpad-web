using System.Text.Json;
using System.Text.Json.Nodes;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Host style sync (2.43, gate uiSync): ui.json and the uiPrefsGet / uiPrefsSet commands.</summary>
public sealed class UiPrefsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "soundpad-ui-prefs-" + Guid.NewGuid().ToString("N"));
    public UiPrefsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => TestFolders.Delete(directory);
    private string UiPath => Path.Combine(directory, "ui.json");
    private static Command Cmd(string op, string? prefs = null) =>
        new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op) { UiPrefs = prefs == null ? null : JsonDocument.Parse(prefs).RootElement.Clone() };
    private static JsonObject Prefs(Reply reply) => (JsonObject)JsonNode.Parse(Json.Write(reply.Data))!["prefs"]!;
    private static bool Saved(Reply reply) => JsonNode.Parse(Json.Write(reply.Data))!["saved"]!.GetValue<bool>();
    private const string Draft = "{\"sessionId\":\"s1\",\"draftId\":\"0123456789abcdef0123456789abcdef\"}";

    [Fact]
    public async Task DefaultsUntilSavedThenRoundTripsAndRaisesTheRevision()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        var first = await b.Execute(Cmd("uiPrefsGet"), "host");
        Assert.True(first.Ok);
        Assert.False(Saved(first));
        Assert.Equal("sidebar", Prefs(first)["navStyle"]!.GetValue<string>());
        Assert.Equal("red-blue", Prefs(first)["theme"]!.GetValue<string>());
        Assert.Null(Prefs(first)["activeDraft"]);
        Assert.False(File.Exists(UiPath));
        var before = b.Snapshot.UiRevision;
        var set = await b.Execute(Cmd("uiPrefsSet", "{\"navStyle\":\"tabs\",\"theme\":\"ocean\",\"activeDraft\":" + Draft + "}"), "host");
        Assert.True(set.Ok, set.Error);
        Assert.Equal(before + 1, b.Snapshot.UiRevision);
        Assert.Equal("tabs", Prefs(set)["navStyle"]!.GetValue<string>());
        Assert.True(File.Exists(UiPath));
        // A restarted companion reads the file back; the revision is in memory only.
        using var restarted = new Bridge(pipe, new StateStore(directory));
        var again = await restarted.Execute(Cmd("uiPrefsGet"), "host");
        Assert.True(Saved(again));
        Assert.Equal("ocean", Prefs(again)["theme"]!.GetValue<string>());
        Assert.Equal("0123456789abcdef0123456789abcdef", Prefs(again)["activeDraft"]!["draftId"]!.GetValue<string>());
        Assert.Equal("system", Prefs(again)["motion"]!.GetValue<string>());
        // Clearing the draft stores null; state.json never carries any of this.
        Assert.True((await restarted.Execute(Cmd("uiPrefsSet", "{\"activeDraft\":null}"), "host")).Ok);
        Assert.Null(Prefs(await restarted.Execute(Cmd("uiPrefsGet"), "host"))["activeDraft"]);
        Assert.DoesNotContain("navStyle", File.ReadAllText(Path.Combine(directory, "state.json")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"uiRevision\":", Json.Write(restarted.Snapshot));
    }

    [Theory]
    [InlineData("{\"navStyle\":\"rail\"}")]
    [InlineData("{\"theme\":1}")]
    [InlineData("{\"motion\":null}")]
    [InlineData("{\"headerAnimation\":\"fast\"}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"activeDraft\":{\"sessionId\":\"s\",\"draftId\":\"XYZ\"}}")]
    [InlineData("{\"activeDraft\":{\"sessionId\":\"s\",\"draftId\":\"0123456789abcdef0123456789abcdef\",\"extra\":1}}")]
    [InlineData("[]")]
    public async Task RefusesInvalidSettingsAndChangesNothing(string input)
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        var reply = await b.Execute(Cmd("uiPrefsSet", input), "host");
        Assert.False(reply.Ok);
        Assert.False(File.Exists(UiPath));
        Assert.Equal(0, b.Snapshot.UiRevision);
    }

    [Fact]
    public async Task GuestsCannotReadOrChangeTheHostsStyle()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        var get = await b.Execute(Cmd("uiPrefsGet"), "guest");
        var set = await b.Execute(Cmd("uiPrefsSet", "{\"theme\":\"purple\"}"), "guest");
        Assert.False(get.Ok); Assert.False(set.Ok);
        Assert.Equal("Host permission required.", set.Error);
        Assert.False(File.Exists(UiPath));
    }

    [Fact]
    public async Task UnknownKeysAreKeptAndInvalidKnownValuesFallBack()
    {
        // A newer site's settings survive this version's write; a known field this version cannot read takes its default.
        File.WriteAllText(UiPath, "{\"version\":1,\"theme\":\"neon\",\"navStyle\":\"tabs\",\"futureSetting\":{\"a\":[1,2]}}");
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        var get = await b.Execute(Cmd("uiPrefsGet"), "host");
        Assert.Equal("red-blue", Prefs(get)["theme"]!.GetValue<string>());
        Assert.Equal("tabs", Prefs(get)["navStyle"]!.GetValue<string>());
        Assert.True((await b.Execute(Cmd("uiPrefsSet", "{\"contrast\":\"high\"}"), "host")).Ok);
        var written = JsonNode.Parse(File.ReadAllText(UiPath))!;
        Assert.Equal(2, written["futureSetting"]!["a"]!.AsArray().Count);
        Assert.Equal("high", written["contrast"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[1,2]")]
    [InlineData("")]
    // A key written twice (hand-edited): JsonObject throws ArgumentException when it is enumerated (2.43 review).
    [InlineData("{\"version\":1,\"theme\":\"ocean\",\"theme\":\"purple\"}")]
    public async Task ACorruptFileGivesTheDefaultsAndIsNotDeleted(string content)
    {
        File.WriteAllText(UiPath, content);
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        var get = await b.Execute(Cmd("uiPrefsGet"), "host");
        Assert.True(get.Ok);
        Assert.False(Saved(get));
        Assert.Equal("sidebar", Prefs(get)["navStyle"]!.GetValue<string>());
        Assert.Equal(content, File.ReadAllText(UiPath));
    }

    [Fact]
    public async Task AFileOverTheSizeLimitIsIgnoredAndNeverWrittenThatLarge()
    {
        File.WriteAllText(UiPath, "{\"version\":1,\"pad\":\"" + new string('x', UiPrefsStore.MaxBytes) + "\"}");
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        Assert.False(Saved(await b.Execute(Cmd("uiPrefsGet"), "host")));
        Assert.True((await b.Execute(Cmd("uiPrefsSet", "{\"theme\":\"purple\"}"), "host")).Ok);
        Assert.True(new FileInfo(UiPath).Length <= UiPrefsStore.MaxBytes);
    }
}
