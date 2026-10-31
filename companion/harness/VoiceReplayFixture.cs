using System.Text.Json;
namespace SoundpadWeb.Testing;

/// <summary>A Voice Replay Studio data folder with two clips, so tests never read the real library.</summary>
public static class VoiceReplayFixture
{
    public static VoiceReplayLibrary Create(string root)
    {
        var clips = Path.Combine(root, "Clips"); Directory.CreateDirectory(clips);
        string hello = Path.Combine(clips, "HELLO_THERE.wav"), later = Path.Combine(clips, "SEE_YOU_LATER.wav");
        AudioFixtures.Wave(hello, 1200); AudioFixtures.Wave(later, 1800);
        File.WriteAllText(Path.Combine(root, "library.json"), JsonSerializer.Serialize(new object[]
        {
            new { Id = Guid.NewGuid(), AudioPath = hello, Title = "Fixture hello there", Transcript = "hello there from voice replay", CapturedAt = DateTimeOffset.Now.AddMinutes(-5), Duration = 1.2 },
            new { Id = Guid.NewGuid(), AudioPath = later, Title = "Fixture see you later", Transcript = "see you later", CapturedAt = DateTimeOffset.Now.AddHours(-2), Duration = 1.8 },
        }));
        return new VoiceReplayLibrary(root);
    }
}
