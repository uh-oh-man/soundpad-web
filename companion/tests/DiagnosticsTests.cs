using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Diagnostics (2.45, gate diagnostics): the host's Activity kept in the companion, and the companion.log tail.</summary>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "soundpad-diagnostics-" + Guid.NewGuid().ToString("N"));
    public DiagnosticsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => TestFolders.Delete(directory);
    private static Command Cmd(string op, string? body = null) =>
        new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op) { Body = body == null ? null : JsonDocument.Parse(body).RootElement.Clone() };
    private static JsonNode Data(Reply reply) => JsonNode.Parse(Json.Write(reply.Data))!;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Entry(string id, string text, long at, string kind = "info", string source = "notice", string? details = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["id"] = id, ["kind"] = kind, ["source"] = source, ["text"] = text, ["at"] = at, ["details"] = details }.Where(p => p.Value != null).ToDictionary());
    private static string Add(params string[] entries) => "{\"entries\":[" + string.Join(",", entries) + "]}";

    [Fact]
    public async Task ActivityIsKeptNewestFirstWithDetailsAndSurvivesAPageReload()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        long t = Now;
        var added = await b.Execute(Cmd("activityAdd", Add(Entry("a1", "Copied", t - 2000, "success"), Entry("a2", "Soundpad refused", t - 1000, "error", details: "Command: play\nError: Soundpad refused"))), "host");
        Assert.True(added.Ok, added.Error);
        Assert.Equal(2, Data(added)["added"]!.GetValue<int>());
        // Sent again (a reload sends what it has not seen acknowledged): kept once.
        Assert.Equal(0, Data(await b.Execute(Cmd("activityAdd", Add(Entry("a1", "Copied", t - 2000, "success"))), "host"))["added"]!.GetValue<int>());
        var list = (JsonArray)Data(await b.Execute(Cmd("activityList"), "host"))["entries"]!;
        Assert.Equal(["a2", "a1"], list.Select(e => e!["id"]!.GetValue<string>()));
        Assert.Equal("Command: play\nError: Soundpad refused", list[0]!["details"]!.GetValue<string>());
        Assert.Null(list[1]!["details"]);
        // Nothing of it is written to disk: state.json is unchanged and no new file appears.
        Assert.DoesNotContain("Soundpad refused", string.Join("", Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(f => { try { return File.ReadAllText(f); } catch (IOException) { return ""; } })));
    }

    [Fact]
    public async Task KeepsTheNewest200AndOneCopyOfASharedEventTwoPagesReport()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        long t = Now - 300_000;
        for (int batch = 0; batch < 11; batch++)
        {
            var entries = Enumerable.Range(0, 20).Select(i => Entry($"n{batch * 20 + i}", $"Notice {batch * 20 + i}", t + (batch * 20 + i) * 100)).ToArray();
            Assert.True((await b.Execute(Cmd("activityAdd", Add(entries)), "host")).Ok);
        }
        var list = (JsonArray)Data(await b.Execute(Cmd("activityList"), "host"))["entries"]!;
        Assert.Equal(Bridge.ActivityLimit, list.Count);
        Assert.Equal("n219", list[0]!["id"]!.GetValue<string>());
        Assert.Equal("n20", list[^1]!["id"]!.GetValue<string>());
        // The companion window and a browser view both saw Alex join: one line.
        long joined = Now;
        Assert.True((await b.Execute(Cmd("activityAdd", Add(Entry("w1", "Alex joined", joined, source: "join"))), "host")).Ok);
        Assert.Equal(0, Data(await b.Execute(Cmd("activityAdd", Add(Entry("v1", "Alex joined", joined + 300, source: "join"))), "host"))["added"]!.GetValue<int>());
        // A later join of the same name is news again.
        Assert.Equal(1, Data(await b.Execute(Cmd("activityAdd", Add(Entry("v2", "Alex joined", joined + 60_000, source: "join"))), "host"))["added"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"entries\":[]}")]
    [InlineData("{\"entries\":{}}")]
    [InlineData("{\"entries\":[1]}")]
    [InlineData("{\"entries\":[{\"id\":\"x\",\"kind\":\"warn\",\"source\":\"notice\",\"text\":\"t\",\"at\":NOW}]}")]
    [InlineData("{\"entries\":[{\"id\":\"x\",\"kind\":\"info\",\"source\":\"kick\",\"text\":\"t\",\"at\":NOW}]}")]
    [InlineData("{\"entries\":[{\"id\":\"x y\",\"kind\":\"info\",\"source\":\"notice\",\"text\":\"t\",\"at\":NOW}]}")]
    [InlineData("{\"entries\":[{\"id\":\"x\",\"kind\":\"info\",\"source\":\"notice\",\"text\":\"\",\"at\":NOW}]}")]
    [InlineData("{\"entries\":[{\"id\":\"x\",\"kind\":\"info\",\"source\":\"notice\",\"text\":\"t\",\"at\":1}]}")]
    [InlineData("{\"entries\":[{\"id\":\"x\",\"kind\":\"info\",\"source\":\"notice\",\"text\":\"t\",\"at\":NOW,\"extra\":1}]}")]
    [InlineData("{\"entries\":[{\"id\":\"x\",\"kind\":\"info\",\"source\":\"notice\",\"text\":\"t\",\"at\":NOW,\"details\":5}]}")]
    [InlineData("{\"entries\":[{\"id\":\"ok\",\"kind\":\"info\",\"source\":\"notice\",\"text\":\"fine\",\"at\":NOW},{\"id\":\"bad\",\"kind\":\"info\",\"source\":\"notice\",\"text\":\"t\"}]}")]
    [InlineData("{\"entries\":[],\"more\":true}")]
    public async Task RefusesInvalidEntriesAndKeepsNoneOfABadBatch(string body)
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        var reply = await b.Execute(Cmd("activityAdd", body.Replace("NOW", Now.ToString())), "host");
        Assert.False(reply.Ok);
        Assert.Empty((JsonArray)Data(await b.Execute(Cmd("activityList"), "host"))["entries"]!);
    }

    [Fact]
    public async Task RefusesTooLongTextsAndTooManyEntries()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        Assert.False((await b.Execute(Cmd("activityAdd", Add(Entry("long", new string('a', Bridge.ActivityTextLimit + 1), Now))), "host")).Ok);
        Assert.False((await b.Execute(Cmd("activityAdd", Add(Entry("big", "t", Now, details: new string('d', Bridge.ActivityDetailsLimit + 1)))), "host")).Ok);
        Assert.False((await b.Execute(Cmd("activityAdd", Add(Enumerable.Range(0, Bridge.ActivityBatch + 1).Select(i => Entry($"m{i}", "t", Now)).ToArray())), "host")).Ok);
        Assert.True((await b.Execute(Cmd("activityAdd", Add(Entry("edge", new string('a', Bridge.ActivityTextLimit), Now, details: new string('d', Bridge.ActivityDetailsLimit)))), "host")).Ok);
    }

    [Fact]
    public async Task GuestsCannotReadOrAddActivityOrReadTheLog()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        File.WriteAllText(Path.Combine(directory, "companion.log"), "2026-11-15 10:00:00.000 secret line\n");
        foreach (var (op, body) in new[] { ("activityList", (string?)null), ("activityAdd", Add(Entry("g", "t", Now))), ("diagnosticsLog", null) })
        {
            var reply = await b.Execute(Cmd(op, body), "guest", requester: "peer-1");
            Assert.False(reply.Ok);
            Assert.Equal("Host permission required.", reply.Error);
        }
        Assert.Empty((JsonArray)Data(await b.Execute(Cmd("activityList"), "host"))["entries"]!);
    }

    [Fact]
    public async Task TheLogTailIsTheLastLinesOldestFirstAndCanHidePaths()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name);
        using var b = new Bridge(pipe, new StateStore(directory));
        var none = Data(await b.Execute(Cmd("diagnosticsLog"), "host"));
        Assert.False(none["exists"]!.GetValue<bool>());
        Assert.Empty((JsonArray)none["lines"]!);
        // The rotated file comes first when the current one is short.
        File.WriteAllText(Path.Combine(directory, "companion.1.log"), "old 1\r\nold 2\r\n");
        var log = new StringBuilder();
        for (int i = 1; i <= 300; i++) log.Append($"2026-11-15 10:00:{i % 60:00}.000 line {i} C:\\Users\\Someone Else\\Music\\horn {i}.wav\r\n");
        File.WriteAllText(Path.Combine(directory, "companion.log"), log.ToString());
        var tail = Data(await b.Execute(Cmd("diagnosticsLog", "{\"lines\":3}"), "host"));
        var lines = ((JsonArray)tail["lines"]!).Select(l => l!.GetValue<string>()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("line 300 C:\\Users\\Someone Else\\Music\\horn 300.wav", lines[2]);
        Assert.Contains("line 298", lines[0]);
        Assert.Equal(Path.Combine(directory, "companion.log"), tail["file"]!.GetValue<string>());
        var hidden = Data(await b.Execute(Cmd("diagnosticsLog", "{\"lines\":2,\"redact\":true}"), "host"));
        var redacted = ((JsonArray)hidden["lines"]!).Select(l => l!.GetValue<string>()).ToArray();
        Assert.EndsWith("line 300 …\\horn 300.wav", redacted[1]);
        Assert.DoesNotContain("Someone Else", string.Join("\n", redacted));
        Assert.Equal("companion.log", hidden["file"]!.GetValue<string>());
        Assert.True(hidden["redacted"]!.GetValue<bool>());
        // Short current file: the older file's lines come before it.
        File.WriteAllText(Path.Combine(directory, "companion.log"), "new 1\n");
        var both = ((JsonArray)Data(await b.Execute(Cmd("diagnosticsLog", "{\"lines\":3}"), "host"))["lines"]!).Select(l => l!.GetValue<string>());
        Assert.Equal(["old 1", "old 2", "new 1"], both);
        foreach (var bad in new[] { "{\"lines\":0}", "{\"lines\":501}", "{\"lines\":\"5\"}", "{\"redact\":1}", "{\"path\":\"C:\\\\Windows\\\\win.ini\"}", "[]" })
            Assert.False((await b.Execute(Cmd("diagnosticsLog", bad), "host")).Ok, bad);
    }

    [Fact]
    public void ALongLogIsReadFromItsEndOnlyAndWhileTheWriterHoldsIt()
    {
        var path = Path.Combine(directory, "companion.log");
        using (var writer = new StreamWriter(path)) for (int i = 1; i <= 20000; i++) writer.WriteLine($"entry {i} " + new string('x', 40));
        // The log writer keeps the file open for appending while Settings reads it.
        using var held = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var lines = Bridge.TailLines(path, 5);
        Assert.Equal(5, lines.Count);
        Assert.StartsWith("entry 20000 ", lines[^1]);
        Assert.StartsWith("entry 19996 ", lines[0]);
        // Asking for more than the tail holds gives whole lines only, never a cut one first.
        var many = Bridge.TailLines(path, 100000);
        Assert.All(many, l => Assert.StartsWith("entry ", l));
        Assert.True(many.Count < 20000);
    }

    [Theory]
    [InlineData("Imported C:\\Users\\Pat\\Music\\horn.wav", "Imported …\\horn.wav")]
    [InlineData("Backup to \\\\nas\\share\\backups\\one.zip failed", "Backup to …\\one.zip failed")]
    [InlineData("Moved C:\\x y\\one.wav to D:\\z\\two.wav", "Moved …\\one.wav to …\\two.wav")]
    [InlineData("Pat started the companion", "<user> started the companion")]
    [InlineData("no paths here", "no paths here")]
    public void RedactionHidesFoldersAndTheUserName(string line, string expected) =>
        Assert.Equal(expected, Bridge.RedactPaths(line, "Pat"));
}
