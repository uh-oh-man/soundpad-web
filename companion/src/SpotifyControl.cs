using Windows.Media.Control;
using Windows.Storage.Streams;
namespace SoundpadWeb;

public record SpotifyState(bool Available, string? Title = null, string? Artist = null, string? Status = null, long? PositionMs = null, long? DurationMs = null, bool CanToggle = false, bool CanNext = false, bool CanPrevious = false, string? Error = null, bool Enabled = true, string? Artwork = null, string? SourceId = null);
/// <summary>One Spotify media session: reads its state and presses its transport. Calls are never concurrent (SpotifyGate).</summary>
public interface ISpotifySession
{
    /// <summary>"status", "toggle", "next" or "previous"; the state after it.</summary>
    Task<SpotifyState> Request(string action);
}

/// <summary>
/// Serializes Spotify requests (2.46). The host and every guest poll the status every 3 s, so a press often arrived
/// while a status read was under way, and was answered "Spotify is busy" without pressing anything: for a guest,
/// whose press also waits on the network, the button seemed not to work. A press now waits its turn. A status read
/// that finds another request under way answers with the last state rather than waiting or reporting busy.
/// </summary>
public sealed class SpotifyGate(ISpotifySession session)
{
    private readonly SemaphoreSlim gate = new(1);
    private SpotifyState? last;
    public static readonly TimeSpan StatusWait = TimeSpan.FromMilliseconds(250), PressWait = TimeSpan.FromSeconds(5);
    public async Task<SpotifyState> Request(string action)
    {
        bool status = action == "status";
        if (!await gate.WaitAsync(status ? StatusWait : PressWait))
            return status && last is { } known ? known : new(false, Error: "Spotify is busy. Try again.");
        try
        {
            var state = await session.Request(action);
            if (state.Available) last = state;
            return state;
        }
        finally { gate.Release(); }
    }
}

public sealed class SpotifyControl : ISpotifySession
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private string? artworkKey, artwork;
    public static bool IsSpotifySource(string source) => source.Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase) || source.Equals("Spotify", StringComparison.OrdinalIgnoreCase) || (source.StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase) && source.EndsWith("!Spotify", StringComparison.OrdinalIgnoreCase));
    private async Task<string?> Artwork(GlobalSystemMediaTransportControlsSessionMediaProperties properties, string source)
    {
        var key = source + "\n" + properties.Title + "\n" + properties.Artist + "\n" + properties.AlbumArtist + "\n" + properties.AlbumTitle;
        if (artworkKey == key && artwork != null) return artwork;
        artworkKey = key; artwork = null;
        try
        {
            if (properties.Thumbnail == null) return null;
            using var stream = await properties.Thumbnail.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            if (stream.Size is 0 or > 2097152) return null;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var count = await reader.LoadAsync((uint)stream.Size).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var bytes = new byte[count]; reader.ReadBytes(bytes);
            using var memory = new MemoryStream(bytes); using var image = System.Drawing.Image.FromStream(memory, true, true);
            if (image.Width > 4096 || image.Height > 4096) return null;
            using var thumbnail = new System.Drawing.Bitmap(image, new System.Drawing.Size(128, 128)); using var output = new MemoryStream();
            thumbnail.Save(output, System.Drawing.Imaging.ImageFormat.Png); artwork = "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { artwork = null; }
        return artwork;
    }
    public async Task<SpotifyState> Request(string action)
    {
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var session = manager.GetSessions().Where(s => IsSpotifySource(s.SourceAppUserModelId)).OrderByDescending(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).FirstOrDefault();
            if (session == null) return new(false, Error: "No supported Spotify desktop media session is available.");
            var info = session.GetPlaybackInfo(); var controls = info.Controls; bool accepted = true;
            var before = info.PlaybackStatus;
            if (action == "toggle") accepted = controls.IsPlayPauseToggleEnabled && await session.TryTogglePlayPauseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action == "next") accepted = controls.IsNextEnabled && await session.TrySkipNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action == "previous") accepted = controls.IsPreviousEnabled && await session.TrySkipPreviousAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action != "status") throw new InvalidDataException("Unsupported Spotify action.");
            if (!accepted) return new(false, Error: "Spotify did not permit this action.");
            // Spotify reports the new play state a moment after it accepts the toggle (2.46): answer with that, so
            // the Play/Pause icon changes with the press instead of at the next poll.
            if (action == "toggle") for (int i = 0; i < 10 && session.GetPlaybackInfo().PlaybackStatus == before; i++) await Task.Delay(50);
            var properties = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); var timeline = session.GetTimelineProperties();
            return new(true, properties.Title, string.IsNullOrWhiteSpace(properties.Artist) ? properties.AlbumArtist : properties.Artist, session.GetPlaybackInfo().PlaybackStatus.ToString(), (long)timeline.Position.TotalMilliseconds, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds, controls.IsPlayPauseToggleEnabled, controls.IsNextEnabled, controls.IsPreviousEnabled, Artwork: await Artwork(properties, session.SourceAppUserModelId), SourceId: session.SourceAppUserModelId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, Error: "Spotify media-session access is unavailable on this Windows configuration."); }
    }
}
