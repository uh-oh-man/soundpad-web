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
/// <para>
/// While a press waits (2.47): the same person pressing the same button again joins that press instead of queueing
/// another (two queued Play/Pause presses played and paused again, so the button seemed to do nothing), and whether
/// they may press at all is asked once more when it is their turn (the host may have turned guest Spotify off
/// meanwhile). A single press still waits for a slow status read (up to <see cref="PressWait"/>): on a busy PC a read
/// takes seconds, and dropping the press brought back the 2.46 "button does nothing".
/// </para>
/// </summary>
public sealed class SpotifyGate(ISpotifySession session)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Task<SpotifyState>> waiting = new(StringComparer.Ordinal);
    private SpotifyState? last;
    public static readonly TimeSpan StatusWait = TimeSpan.FromMilliseconds(250), PressWait = TimeSpan.FromSeconds(5);
    /// <param name="who">Who pressed (a press of the same action by the same person while theirs waits joins it); null for none.</param>
    /// <param name="refusal">Asked once it is the press's turn: why it may not be pressed now (thrown as <see cref="InvalidDataException"/>), or null.</param>
    public Task<SpotifyState> Request(string action, string? who = null, Func<string?>? refusal = null)
    {
        if (action == "status" || who == null) return Run(action, null, null, refusal);
        string key = who + "|" + action;
        var press = new TaskCompletionSource<SpotifyState>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (waiting)
        {
            if (waiting.TryGetValue(key, out var queued)) return queued;
            waiting[key] = press.Task;
        }
        _ = Complete(press, Run(action, key, press.Task, refusal));
        return press.Task;
    }
    /// <summary>The press stops waiting: only its own entry goes (a newer press of the same button may be waiting by now).</summary>
    private void Forget(string? key, Task? mine)
    {
        if (key == null) return;
        lock (waiting) if (waiting.TryGetValue(key, out var current) && current == mine) waiting.Remove(key);
    }
    private static async Task Complete(TaskCompletionSource<SpotifyState> press, Task<SpotifyState> run)
    {
        try { press.TrySetResult(await run); }
        catch (Exception ex) { press.TrySetException(ex); }
    }
    private async Task<SpotifyState> Run(string action, string? key, Task? mine, Func<string?>? refusal)
    {
        bool status = action == "status", entered = false;
        try
        {
            entered = await gate.WaitAsync(status ? StatusWait : PressWait);
            // Its turn (or too late): a press of the same button after this point is a new press.
            Forget(key, mine);
            if (!entered)
                return status && last is { } known ? known : new(false, Error: "Spotify was busy, so that press was not sent. Press again.");
            if (refusal?.Invoke() is { } refused) throw new InvalidDataException(refused);
            var state = await session.Request(action);
            if (state.Available) last = state;
            return state;
        }
        finally
        {
            Forget(key, mine);
            if (entered) gate.Release();
        }
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
