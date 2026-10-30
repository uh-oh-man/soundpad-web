using Windows.Media.Control;
using Windows.Storage.Streams;
namespace SoundpadWeb;

public record SpotifyState(bool Available, string? Title = null, string? Artist = null, string? Status = null, long? PositionMs = null, long? DurationMs = null, bool CanToggle = false, bool CanNext = false, bool CanPrevious = false, string? Error = null, bool Enabled = true, string? Artwork = null, string? SourceId = null);
public sealed class SpotifyControl
{
    private readonly SemaphoreSlim gate = new(1);
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
        if (!await gate.WaitAsync(0)) return new(false, Error: "Spotify is busy. Try again.");
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var session = manager.GetSessions().Where(s => IsSpotifySource(s.SourceAppUserModelId)).OrderByDescending(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).FirstOrDefault();
            if (session == null) return new(false, Error: "No supported Spotify desktop media session is available.");
            var info = session.GetPlaybackInfo(); var controls = info.Controls; bool accepted = true;
            if (action == "toggle") accepted = controls.IsPlayPauseToggleEnabled && await session.TryTogglePlayPauseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action == "next") accepted = controls.IsNextEnabled && await session.TrySkipNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action == "previous") accepted = controls.IsPreviousEnabled && await session.TrySkipPreviousAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action != "status") throw new InvalidDataException("Unsupported Spotify action.");
            if (!accepted) return new(false, Error: "Spotify did not permit this action.");
            var properties = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); var timeline = session.GetTimelineProperties();
            return new(true, properties.Title, string.IsNullOrWhiteSpace(properties.Artist) ? properties.AlbumArtist : properties.Artist, session.GetPlaybackInfo().PlaybackStatus.ToString(), (long)timeline.Position.TotalMilliseconds, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds, controls.IsPlayPauseToggleEnabled, controls.IsNextEnabled, controls.IsPreviousEnabled, Artwork: await Artwork(properties, session.SourceAppUserModelId), SourceId: session.SourceAppUserModelId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, Error: "Spotify media-session access is unavailable on this Windows configuration."); }
        finally { gate.Release(); }
    }
}
