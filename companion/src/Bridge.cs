using System.Text.Json;
namespace SoundpadWeb;

public sealed partial class Bridge(ISoundpad pipe, StateStore store) : IDisposable
{
    private ISoundpad Pipe => pipe;
    private StateStore Store => store;
    private readonly NativeStatistics nativeStatistics = new(pipe is SoundpadPipe { IsProduction: true } ? null : Path.Combine(store.DirectoryPath, "vendor-fixture"));
    private readonly Waveforms waveformService = new(store.DirectoryPath);
    private Dictionary<string, string>? iconCache;
    private readonly SemaphoreSlim gate = new(1);
    private int waiting;
    private DiskState disk = store.Load();
    private ParsedLibrary parsed = new(new("empty", [], []), [], []);
    private bool available;
    private string? error = "Waiting for Soundpad. Open it normally; the companion will reconnect.";
    private Playback playback = new();
    private long revision;
    public event Action<Snapshot>? Changed;
    public event Action? CommandAccepted;
    public void Dispose() => waveformService.Dispose();
    public Snapshot Snapshot => new(1, revision, available, error, parsed.Public, playback, disk.Counter, disk.Permissions, 2,
        Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Convert.FromBase64String(disk.IdentityKey), System.Text.Encoding.UTF8.GetBytes("workspace-host-v2"))).ToLowerInvariant(), disk.StatisticsVersion, typeof(Bridge).Assembly.GetName().Version?.ToString(3));
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
            var next = LibraryParser.Parse(all, cats, disk, iconCache ??= store.ReadIcons(disk), nativeStatistics);
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
            if (library) nativeStatistics.Refresh();
            if (library || !available) await RefreshInner();
            if (available)
            {
                try
                {
                    await SamplePlayback();
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { available = false; playback = new(); error = "Soundpad disconnected; reconnecting automatically."; }
            }
            Publish();
        }
        finally { gate.Release(); }
    }
    public async Task<Reply> Execute(Command c, string actor, Participant? participant = null)
    {
        var query = await Query(c, actor); if (query != null) return query;
        if (Interlocked.Increment(ref waiting) > 24) { Interlocked.Decrement(ref waiting); return new("ack", c.Id, false, "Busy. Try again."); }
        await gate.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ttl = c.Op is "play" or "seek" or "startAt" or "region" ? 5000 : 30000;
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || c.At < now - ttl || c.At > now + ttl) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && c.Op is not ("play" or "stop" or "counter" or "seek" or "pause" or "startAt" or "region" or "statistics")) throw new InvalidDataException("Host permission required.");
            if (!host && (c.Op is "play" or "stop" or "seek" or "pause" or "startAt" or "region") && !disk.Permissions.RemotePlayback) throw new InvalidDataException("Remote playback is disabled.");
            if (!host && (c.Op is "seek" or "pause" or "startAt" or "region") && !disk.Permissions.GuestSeeking) throw new InvalidDataException("Guest seeking is disabled.");
            if (!host && c.Op == "counter" && !disk.Permissions.GuestCounter) throw new InvalidDataException("Guest counter changes are disabled.");
            if (disk.Receipts.ContainsKey(c.Id)) return new("ack", c.Id, true);
            switch (c.Op)
            {
                case "play":
                case "startAt":
                case "region":
                    if (!available || c.LibraryVersion != parsed.Public.Version || c.SoundId == null || !parsed.Indexes.ContainsKey(c.SoundId)) throw new InvalidDataException("Library changed or Soundpad unavailable. Refresh and try again.");
                    // Re-read global mapping immediately before play. Never trust a cached index after reordering.
                    var expected = parsed.Public.Version;
                    await RefreshInner();
                    if (!available || expected != parsed.Public.Version || !parsed.Indexes.TryGetValue(c.SoundId, out int index)) throw new InvalidDataException("Soundpad library changed. Try the sound again.");
                    await StartPlayback(c, actor, participant, index);
                    break;
                case "seek":
                case "pause": await ControlPlayback(c); break;
                case "stop":
                    if (!(await pipe.Request("DoStopSound()")).StartsWith("R-200")) throw new InvalidDataException("Soundpad rejected Stop.");
                    playback = new("STOPPED", SampledAt: now); regionStart = null; seekSettlesUntil = 0; break;
                case "statistics": return StatisticsReply(c);
                case "clearStatistics": disk.Statistics.Clear(); disk.Activity.Clear(); disk.StatisticsVersion++; break;
                case "spotify": if (c.Action is not ("enable" or "disable")) throw new InvalidDataException("Unsupported Spotify setting."); disk.SpotifyEnabled = c.Action == "enable"; break;
                case "counter":
                    if (c.Delta is not (-1 or 1)) throw new InvalidDataException("Counter delta must be +1 or -1.");
                    disk.Counter = (int)Math.Clamp((long)disk.Counter + c.Delta.Value, 0, int.MaxValue); break;
                case "reset": disk.Counter = 0; break;
                case "permissions": disk.Permissions = c.Permissions ?? throw new InvalidDataException("Missing permissions."); break;
                case "icon":
                    if (c.SoundId == null || !parsed.Indexes.ContainsKey(c.SoundId)) throw new InvalidDataException("Unknown sound.");
                    if (string.IsNullOrEmpty(c.Image)) { disk.Icons.Remove(c.SoundId); disk.IconFiles.Remove(c.SoundId); }
                    else
                    {
                        if (disk.IconFiles.Count >= 1024 && !disk.IconFiles.ContainsKey(c.SoundId)) throw new InvalidDataException("Custom icon limit is 1024 sounds.");
                        var normalizedIcon = IconData.Normalize(c.Image);
                        var existingIcons = iconCache ??= store.ReadIcons(disk);
                        if (existingIcons.Where(p => p.Key != c.SoundId).Sum(p => (long)p.Value.Length) + normalizedIcon.Length > 4 * 1024 * 1024) throw new InvalidDataException("Custom icons exceed the 4 MiB shared metadata budget. Remove an unused icon and try again.");
                        disk.IconFiles[c.SoundId] = store.WriteIcon(c.SoundId, normalizedIcon); disk.Icons.Remove(c.SoundId);
                    }
                    iconCache = null; await RefreshInner(); break;
                case "refresh": await RefreshInner(); break;
                default: throw new InvalidDataException("Unsupported command.");
            }
            // Retain receipts longer than every command's acceptance window, across process restarts.
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
            foreach (var id in disk.Receipts.Where(p => p.Value < cutoff).Select(p => p.Key).ToArray()) disk.Receipts.Remove(id);
            disk.Receipts[c.Id] = c.At;
            store.Save(disk); Publish(); CommandAccepted?.Invoke();
            return new("ack", c.Id, true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ArgumentException or FormatException or System.Xml.XmlException)
        {
            // Reload committed state if an atomic disk write failed. Never acknowledge a non-durable counter.
            disk = store.Load(); iconCache = null; Publish();
            return new("ack", c.Id, false, ex is InvalidDataException ? ex.Message : "Companion operation failed. Check Soundpad and local storage.");
        }
        finally { gate.Release(); Interlocked.Decrement(ref waiting); }
    }
}
