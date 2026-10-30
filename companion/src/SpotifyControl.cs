using Windows.Media.Control;
namespace SoundpadWeb;

public record SpotifyState(bool Available, string? Title = null, string? Artist = null, string? Status = null, long? PositionMs = null, long? DurationMs = null, bool CanToggle = false, bool CanNext = false, bool CanPrevious = false, string? Error = null, bool Enabled = true);
public sealed class SpotifyControl
{
    private readonly SemaphoreSlim gate = new(1);
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    public async Task<SpotifyState> Request(string action)
    {
        if (!await gate.WaitAsync(0)) return new(false, Error: "Spotify is busy. Try again.");
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var session = manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
            if (session == null) return new(false, Error: "No supported Spotify desktop media session is available.");
            var info = session.GetPlaybackInfo(); var controls = info.Controls; bool accepted = true;
            if (action == "toggle") accepted = controls.IsPlayPauseToggleEnabled && await session.TryTogglePlayPauseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action == "next") accepted = controls.IsNextEnabled && await session.TrySkipNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action == "previous") accepted = controls.IsPreviousEnabled && await session.TrySkipPreviousAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            else if (action != "status") throw new InvalidDataException("Unsupported Spotify action.");
            if (!accepted) return new(false, Error: "Spotify did not permit this action.");
            var properties = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); var timeline = session.GetTimelineProperties();
            return new(true, properties.Title, properties.Artist, session.GetPlaybackInfo().PlaybackStatus.ToString(), (long)timeline.Position.TotalMilliseconds, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds, controls.IsPlayPauseToggleEnabled, controls.IsNextEnabled, controls.IsPreviousEnabled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, Error: "Spotify media-session access is unavailable on this Windows configuration."); }
        finally { gate.Release(); }
    }
}
