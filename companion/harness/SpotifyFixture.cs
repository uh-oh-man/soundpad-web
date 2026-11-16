namespace SoundpadWeb.Testing;

/// <summary>
/// Test-only Spotify (2.46): two tracks that Play/Pause, Next and Previous move between. The e2e harness always
/// uses it, so browser tests never read or press the PC's real Spotify. <see cref="StatusDelayMs"/> makes every
/// status read slow, as on a busy PC, so a press arrives while one is under way.
/// </summary>
public sealed class SpotifyFixture : ISpotifySession
{
    private static readonly (string Title, string Artist)[] Tracks = [("Fixture song", "Fixture artist"), ("Second fixture song", "Second fixture artist")];
    private int track;
    private bool playing;
    public int StatusDelayMs { get; set; }
    /// <summary>Presses that reached Spotify, in order ("toggle", "next", "previous").</summary>
    public List<string> Presses { get; } = [];
    public async Task<SpotifyState> Request(string action)
    {
        switch (action)
        {
            case "status": if (StatusDelayMs > 0) await Task.Delay(StatusDelayMs); break;
            case "toggle": playing = !playing; break;
            case "next": track = (track + 1) % Tracks.Length; break;
            case "previous": track = (track + Tracks.Length - 1) % Tracks.Length; break;
            default: throw new InvalidDataException("Unsupported Spotify action.");
        }
        if (action != "status") lock (Presses) Presses.Add(action);
        var (title, artist) = Tracks[track];
        return new(true, title, artist, playing ? "Playing" : "Paused", 20000, 180000, true, true, true, SourceId: "Spotify.exe");
    }
}
