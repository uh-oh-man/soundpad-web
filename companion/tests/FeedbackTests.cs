using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Feedback (2.45): notes from the header's Feedback button land in one file with a fixed name.</summary>
public sealed class FeedbackTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-feedback-" + Guid.NewGuid().ToString("N"));
    public FeedbackTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static readonly Participant Alice = new("11111111-1111-1111-1111-111111111111", "Alice");
    private static Command Note(string kind, string text, string? where = "Soundboard") =>
        new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "feedbackSend",
            Body: JsonSerializer.SerializeToElement(new { kind, text, where, version = "2.45.0" }));

    private async Task<(MockSoundpad, SoundpadPipe, Bridge, string)> Start()
    {
        var mock = new MockSoundpad(); var pipe = new SoundpadPipe(mock.Name);
        var data = Path.Combine(folder, "data"); Directory.CreateDirectory(data);
        var bridge = new Bridge(pipe, new StateStore(data)) { FeedbackFolder = Path.Combine(folder, "Desktop") };
        await bridge.Poll(true);
        return (mock, pipe, bridge, bridge.FeedbackPath);
    }

    [Fact]
    public async Task NotesFromTheHostAndAGuestGoIntoOneFileWithAFixedName()
    {
        var (mock, pipe, bridge, path) = await Start();
        await using var _m = mock; using var _p = pipe; using var _b = bridge;
        Assert.Equal("Soundpad Web feedback.md", Path.GetFileName(path));
        var guest = await bridge.Execute(Note("idea", "Make the Kick button bigger\nand louder"), "guest", Alice, "peer-alice");
        Assert.True(guest.Ok, guest.Error);
        // A guest is never told where the host keeps the file.
        Assert.DoesNotContain(folder, JsonSerializer.Serialize(guest.Data), StringComparison.OrdinalIgnoreCase);
        var host = await bridge.Execute(Note("bug", "Search missed a sound"), "host");
        Assert.True(host.Ok, host.Error);
        var text = File.ReadAllText(path);
        Assert.StartsWith("# Soundpad Web feedback", text);
        Assert.Contains("Idea · Alice (guest)", text);
        Assert.Contains("> Make the Kick button bigger\n> and louder", text);
        Assert.Contains("Bug · Host", text);
        Assert.Contains("Where: Soundboard", text);
        // Appended, never rewritten: the first note is still first.
        Assert.True(text.IndexOf("Alice", StringComparison.Ordinal) < text.IndexOf("Search missed", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task EmptyTooLongUnknownAndFloodingNotesAreRefused()
    {
        var (mock, pipe, bridge, path) = await Start();
        await using var _m = mock; using var _p = pipe; using var _b = bridge;
        Assert.False((await bridge.Execute(Note("idea", "   "), "guest", Alice, "peer-alice")).Ok);
        Assert.False((await bridge.Execute(Note("rant", "hi"), "guest", Alice, "peer-alice")).Ok);
        Assert.False((await bridge.Execute(Note("idea", new string('x', Bridge.FeedbackMaxText + 1)), "guest", Alice, "peer-alice")).Ok);
        Assert.False(File.Exists(path));
        for (int i = 0; i < 10; i++) Assert.True((await bridge.Execute(Note("opinion", $"note {i}"), "guest", Alice, "peer-alice")).Ok);
        var flood = await bridge.Execute(Note("opinion", "one too many"), "guest", Alice, "peer-alice");
        Assert.False(flood.Ok);
        Assert.Contains("Wait a few minutes", flood.Error);
        // The host is never rate limited for their own notes.
        Assert.True((await bridge.Execute(Note("opinion", "host note"), "host")).Ok);
    }

    [Fact]
    public async Task AFullFileStopsTakingNotesInsteadOfGrowing()
    {
        var (mock, pipe, bridge, path) = await Start();
        await using var _m = mock; using var _p = pipe; using var _b = bridge;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var f = new FileStream(path, FileMode.Create)) f.SetLength(Bridge.FeedbackMaxFileBytes + 1);
        var reply = await bridge.Execute(Note("bug", "too late"), "guest", Alice, "peer-alice");
        Assert.False(reply.Ok);
        Assert.Equal(Bridge.FeedbackMaxFileBytes + 1, new FileInfo(path).Length);
    }
}
