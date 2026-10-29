using System.Text.Json;
namespace SoundpadWeb;

public sealed class Bridge(ISoundpad pipe, StateStore store)
{
    private readonly SemaphoreSlim gate = new(1);
    private int waiting;
    private DiskState disk = store.Load();
    private ParsedLibrary parsed = new(new("empty", [], []), [], []);
    private bool available;
    private string? error = "Waiting for Soundpad. Open it normally; the companion will reconnect.";
    private Playback playback = new();
    private long revision;
    public event Action<Snapshot>? Changed;
    public Snapshot Snapshot => new(1, revision, available, error, parsed.Public, playback, disk.Counter, disk.Permissions);
    public string? Website => disk.Website;
    public void SetWebsite(string site) { disk.Website = site; store.Save(disk); }
    private void Publish() { revision++; Changed?.Invoke(Snapshot); }
    private async Task RefreshInner()
    {
        try
        {
            var all = await pipe.Request("GetSoundlist()");
            var cats = await pipe.Request("GetCategories(true, true)");
            if (cats.StartsWith("R-404")) throw new InvalidDataException("This Soundpad version does not support categories. Update Soundpad.");
            var next = LibraryParser.Parse(all, cats, disk);
            parsed = next; available = true; error = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or System.Xml.XmlException or ArgumentException)
        {
            available = false; playback = new();
            error = ex is InvalidDataException ? ex.Message : "Soundpad is unavailable. Open it normally; reconnection is automatic.";
        }
    }
    public async Task Poll(bool library)
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (library || !available) await RefreshInner();
            if (available)
            {
                try
                {
                    var status = await pipe.Request("GetPlayStatus()");
                    long? position = null, duration = null;
                    if (status is "PLAYING" or "PAUSED" or "SEEKING")
                    {
                        if (long.TryParse(await pipe.Request("GetPlaybackPositionInMs()"), out long p) && p >= 0) position = p;
                        if (long.TryParse(await pipe.Request("GetPlaybackDurationInMs()"), out long d) && d > 0) duration = d;
                    }
                    playback = new(status is "PLAYING" or "PAUSED" or "SEEKING" or "STOPPED" ? status : "UNKNOWN", position, duration);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { available = false; playback = new(); error = "Soundpad disconnected; reconnecting automatically."; }
            }
            Publish();
        }
        finally { gate.Release(); }
    }
    public async Task<Reply> Execute(Command c, string actor)
    {
        if (Interlocked.Increment(ref waiting) > 24) { Interlocked.Decrement(ref waiting); return new("ack", c.Id, false, "Busy. Try again."); }
        await gate.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ttl = c.Op == "play" ? 5000 : 30000;
            if (c.V != 1 || !Guid.TryParseExact(c.Id, "D", out _) || c.At < now - ttl || c.At > now + ttl) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && c.Op is not ("play" or "stop" or "counter")) throw new InvalidDataException("Host permission required.");
            if (!host && (c.Op is "play" or "stop") && !disk.Permissions.RemotePlayback) throw new InvalidDataException("Remote playback is disabled.");
            if (!host && c.Op == "counter" && !disk.Permissions.GuestCounter) throw new InvalidDataException("Guest counter changes are disabled.");
            if (disk.Receipts.ContainsKey(c.Id)) return new("ack", c.Id, true);
            switch (c.Op)
            {
                case "play":
                    if (!available || c.LibraryVersion != parsed.Public.Version || c.SoundId == null || !parsed.Indexes.ContainsKey(c.SoundId)) throw new InvalidDataException("Library changed or Soundpad unavailable. Refresh and try again.");
                    // Re-read global mapping immediately before play. Never trust a cached index after reordering.
                    var expected = parsed.Public.Version;
                    await RefreshInner();
                    if (!available || expected != parsed.Public.Version || !parsed.Indexes.TryGetValue(c.SoundId, out int index)) throw new InvalidDataException("Soundpad library changed. Try the sound again.");
                    var result = await pipe.Request($"DoPlaySound({index})");
                    if (!result.StartsWith("R-200")) throw new InvalidDataException("Soundpad rejected playback.");
                    break;
                case "stop":
                    if (!(await pipe.Request("DoStopSound()")).StartsWith("R-200")) throw new InvalidDataException("Soundpad rejected Stop.");
                    playback = new("STOPPED"); break;
                case "counter":
                    if (c.Delta is not (-1 or 1)) throw new InvalidDataException("Counter delta must be +1 or -1.");
                    disk.Counter = (int)Math.Clamp((long)disk.Counter + c.Delta.Value, 0, int.MaxValue); break;
                case "reset": disk.Counter = 0; break;
                case "permissions": disk.Permissions = c.Permissions ?? throw new InvalidDataException("Missing permissions."); break;
                case "icon":
                    if (c.SoundId == null || !parsed.Indexes.ContainsKey(c.SoundId)) throw new InvalidDataException("Unknown sound.");
                    if (string.IsNullOrEmpty(c.Image)) disk.Icons.Remove(c.SoundId);
                    else
                    {
                        if (disk.Icons.Count >= 256 && !disk.Icons.ContainsKey(c.SoundId)) throw new InvalidDataException("Custom icon limit is 256 sounds.");
                        disk.Icons[c.SoundId] = IconData.Normalize(c.Image);
                    }
                    await RefreshInner(); break;
                case "refresh": await RefreshInner(); break;
                default: throw new InvalidDataException("Unsupported command.");
            }
            // Retain receipts longer than every command's acceptance window, across process restarts.
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
            foreach (var id in disk.Receipts.Where(p => p.Value < cutoff).Select(p => p.Key).ToArray()) disk.Receipts.Remove(id);
            disk.Receipts[c.Id] = c.At;
            store.Save(disk); Publish();
            return new("ack", c.Id, true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ArgumentException or System.Xml.XmlException)
        {
            // Reload committed state if an atomic disk write failed. Never acknowledge a non-durable counter.
            disk = store.Load();
            return new("ack", c.Id, false, ex is InvalidDataException ? ex.Message : "Companion operation failed. Check Soundpad and local storage.");
        }
        finally { gate.Release(); Interlocked.Decrement(ref waiting); }
    }
}
