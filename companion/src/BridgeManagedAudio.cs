using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
namespace SoundpadWeb;

public sealed partial class Bridge
{
    private ManagedAudioStore? managed;
    private readonly object managerInitialization = new();
    private readonly SemaphoreSlim audioGate = new(1);
    private readonly ConcurrentDictionary<string, Lazy<Task<Reply>>> audioRequests = new();
    private readonly ConcurrentDictionary<string, (long Revision, long At)> playbackIntents = new();
    private readonly ConcurrentDictionary<string, byte> revokedAudioPeers = new();
    private long playRevision, nextAudioRecovery;
    private int audioWaiting;
    private volatile bool sessionAccepting;
    private volatile string? audioAudience;
    private ManagedAudioStore Audio
    {
        get
        {
            lock (managerInitialization)
            {
                if (managed != null) return managed;
                managed = new(store.DirectoryPath, pipe, path => Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(disk.IdentityKey), Encoding.UTF8.GetBytes(path.Replace('/', '\\').ToUpperInvariant() + "\n0"))).ToLowerInvariant(), async work =>
                {
                    await gate.WaitAsync(); try { await work(); } finally { gate.Release(); }
                });
                managed.Changed += Publish; return managed;
            }
        }
    }
    private static bool PlaybackOperation(string op) => op is "play" or "startAt" or "region" or "reverse" or "audioPlay" or "stop" or "pause" or "seek";
    private long ReservePlaybackIntent(Command command, string actor, string? requester, string? audience)
    {
        if (!PlaybackOperation(command.Op) && command.Op != "endSession") return 0;
        if (actor != "host" && (!GuestStillAllowed(actor, requester, audience) || !disk.Permissions.RemotePlayback || command.Op == "endSession" || (command.Op is "seek" or "pause" or "startAt" or "region") && !disk.Permissions.GuestSeeking)) return 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var entry in playbackIntents.Where(e => now - e.Value.At > 120000)) playbackIntents.TryRemove(entry.Key, out _);
        return playbackIntents.GetOrAdd(command.Id, _ => (Interlocked.Increment(ref playRevision), now)).Revision;
    }
    private bool GuestStillAllowed(string actor, string? requester, string? audience) => actor == "host" ||
        (disk.Permissions.RemotePlayback && (audioAudience == null || sessionAccepting && audience == audioAudience) && (requester == null || !revokedAudioPeers.ContainsKey(requester)));
    private void AudioPermission(Command command, string actor, string? requester, string? audience)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (command.V is not (1 or 2) || !Guid.TryParseExact(command.Id, "D", out _) || Math.Abs(now - command.At) > 90000) throw new InvalidDataException("Invalid or expired audio command.");
        if (actor != "host")
        {
            if (command.Op is not ("reverse" or "audioPlay" or "play" or "startAt" or "region")) throw new InvalidDataException("Host permission required.");
            if (!GuestStillAllowed(actor, requester, audience)) throw new InvalidDataException("Guest playback permission or session authorization changed.");
            if (command.Op is "startAt" or "region" && !disk.Permissions.GuestSeeking) throw new InvalidDataException("Guest seeking is disabled.");
        }
    }
    private Task<Reply> ExecuteAudio(Command c, string actor, Participant? participant, long intent, string? requester, string? audience)
    {
        if (c.Op == "endSession" && actor == "host") sessionAccepting = false;
        if (audioRequests.Count >= 64) foreach (var item in audioRequests.Where(e => e.Value.IsValueCreated && e.Value.Value.IsCompleted)) audioRequests.TryRemove(item.Key, out _);
        if (audioRequests.Count >= 128) return Task.FromResult(new Reply("ack", c.Id, false, "Audio processing is busy."));
        return audioRequests.GetOrAdd(actor + ":" + requester + ":" + c.Id, _ => new(() => RunAudio(c, actor, participant, intent, requester, audience))).Value;
    }
    private async Task<Reply> RunAudio(Command c, string actor, Participant? participant, long intent, string? requester, string? audience)
    {
        bool entered = false;
        try
        {
            AudioPermission(c, actor, requester, audience);
            if (Interlocked.Increment(ref audioWaiting) > 2 && c.Op is not ("endSession" or "beginSession")) { Interlocked.Decrement(ref audioWaiting); return new("ack", c.Id, false, "Audio preparation is busy. Playback controls remain available; retry shortly."); }
            entered = true; await audioGate.WaitAsync();
            try
            {
                AudioPermission(c, actor, requester, audience);
                await gate.WaitAsync(); try { if (disk.Receipts.ContainsKey(c.Id)) return new("ack", c.Id, true); } finally { gate.Release(); }
                if (c.Op == "beginSession")
                {
                    if (!Audio.Public.Blocked) Audio.BeginSession(); sessionAccepting = true; audioAudience = c.AudienceId; revokedAudioPeers.Clear();
                }
                else if (c.Op == "endSession") { if (!Audio.Public.Blocked) { Audio.RetireSession(); await Audio.Cleanup(); } }
                else if (c.Op == "retryCleanup") await Audio.Recover();
                else if (c.Op == "removeAudio") await Audio.Remove(c.AssetId ?? throw new InvalidDataException("Choose a managed source."));
                else if (c.Op == "retryAudio") await Audio.RetryAsset(c.AssetId ?? throw new InvalidDataException("Choose a managed source."));
                else if (c.Op == "keepAudio") await Audio.Promote(c.AssetId ?? throw new InvalidDataException("Open Temp to confirm a permanent destination."), c.VariantId ?? throw new InvalidDataException("Choose the normal or reversed variant."), c.DestinationDirectory, c.CategoryId, c.CategoryName, c.Confirmed);
                else
                {
                    if (!sessionAccepting) throw new InvalidDataException("Hosting has ended.");
                    AudioAsset asset; string orientation;
                    if (c.Op == "audioPlay") { asset = Audio.Public.Assets.Any(a => a.Id == c.AssetId) ? await FindAsset(c.AssetId!) : throw new InvalidDataException("Unknown source."); orientation = c.Orientation ?? "normal"; }
                    else
                    {
                        if (c.SoundId == null) throw new InvalidDataException("Choose a sound.");
                        var existing = Audio.Resolve(c.SoundId);
                        if (existing is { } known) { asset = known.Asset; orientation = c.Op == "reverse" ? known.Variant.Orientation == "normal" ? "reversed" : "normal" : known.Variant.Orientation; }
                        else
                        {
                            string path, name;
                            await gate.WaitAsync();
                            try
                            {
                                await RefreshInner();
                                if (!available || c.LibraryVersion != parsed.Public.Version || !parsed.Paths.TryGetValue(c.SoundId, out path!)) throw new InvalidDataException("The native library changed. Refresh and try again.");
                                name = parsed.Public.Sounds.Single(s => s.Id == c.SoundId).Name;
                            }
                            finally { gate.Release(); }
                            var source = await Audio.NativeSource(c.SoundId, path, name, Person(actor, participant).Name); asset = source.Asset; orientation = c.Op == "reverse" ? "reversed" : "normal";
                        }
                    }
                    var prepared = await Audio.Prepare(asset.Id, orientation, actor == "host" || disk.Permissions.GuestTransforms);
                    await gate.WaitAsync();
                    try
                    {
                        await RefreshInner();
                        var id = prepared.Variant.Native?.SoundId;
                        if (!available || id == null || !parsed.Indexes.TryGetValue(id, out int index)) throw new InvalidDataException("The prepared native entry is unavailable. Its source is preserved in Temp.");
                        if (intent == Volatile.Read(ref playRevision) && sessionAccepting && GuestStillAllowed(actor, requester, audience) && (actor == "host" || c.Op is not ("startAt" or "region") || disk.Permissions.GuestSeeking)) await StartPlayback(c with { Op = c.Op is "startAt" or "region" ? c.Op : "play", SoundId = id }, actor, participant, index);
                        SaveAudioReceipt(c); Publish(); CommandAccepted?.Invoke();
                        return new("ack", c.Id, true, Data: new { assetId = asset.Id, state = asset.State, played = playback.SoundId == id && intent == Volatile.Read(ref playRevision) });
                    }
                    finally { gate.Release(); }
                }
                await gate.WaitAsync(); try { await RefreshInner(); SaveAudioReceipt(c); Publish(); } finally { gate.Release(); }
                return new("ack", c.Id, true);
            }
            finally { audioGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new("ack", c.Id, false, ex is InvalidDataException ? ex.Message : "Managed audio could not finish. Open Temp to inspect preserved recovery state."); }
        finally { if (entered) Interlocked.Decrement(ref audioWaiting); }
    }
    private void SaveAudioReceipt(Command c)
    {
        var cutoff=DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
        foreach(var id in disk.Receipts.Where(p=>p.Value<cutoff).Select(p=>p.Key).ToArray()) disk.Receipts.Remove(id);
        disk.Receipts[c.Id]=c.At;store.Save(disk);
    }
    private Task<AudioAsset> FindAsset(string id) => Task.FromResult(Audio.AssetById(id));
    public async Task<Reply> Upload(byte[] bytes, string name, string by, bool host = false, string owner = "anonymous", string? audience = null, bool mimic = false)
    {
        if (!sessionAccepting || (!host && (!(mimic ? disk.Permissions.GuestMimic : disk.Permissions.GuestUploads) || (audioAudience != null && audience != audioAudience) || revokedAudioPeers.ContainsKey(owner)))) return new("ack", "upload", false, "Guest uploads are disabled or this session ended.");
        if (Interlocked.Increment(ref audioWaiting) > 2) { Interlocked.Decrement(ref audioWaiting); return new("ack", "upload", false, "Audio processing is busy; retry shortly."); }
        await audioGate.WaitAsync();
        try
        {
            if (!sessionAccepting || (!host && (!(mimic ? disk.Permissions.GuestMimic : disk.Permissions.GuestUploads) || (audioAudience != null && audience != audioAudience) || revokedAudioPeers.ContainsKey(owner)))) throw new InvalidDataException("Upload authorization changed.");
            var asset = await Audio.Upload(bytes, name, by, owner, host);
            await gate.WaitAsync(); try { await RefreshInner(); Publish(); } finally { gate.Release(); }
            return new("ack", "upload", true, Data: new { assetId = asset.Id, state = asset.State });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new("ack", "upload", false, ex is InvalidDataException ? ex.Message : "Upload processing failed. The staged recovery record is available in Temp."); }
        finally { audioGate.Release(); Interlocked.Decrement(ref audioWaiting); }
    }
    public async Task CleanupSession()
    {
        mimicCapture.Stop(); transcriber.Disable(); sessionAccepting = false; Interlocked.Increment(ref playRevision); await audioGate.WaitAsync();
        try { if (!Audio.Public.Blocked) { Audio.RetireSession(); await Audio.Cleanup(); } recoveryPending = false; }
        finally { audioGate.Release(); }
    }
    private async Task RecoverAudioIfDue(bool force)
    {
        if (!recoveryPending && (managed == null || !managed.Public.Assets.Any(a => a.State is "waiting-soundpad" or "promotion-pending" or "cleanup-pending"))) return;
        if (!force && Environment.TickCount64 < nextAudioRecovery || !await audioGate.WaitAsync(0)) return;
        try
        {
            nextAudioRecovery = Environment.TickCount64 + 5000;
            if (Audio.Public.Blocked) return;
            await Audio.Recover(recoveryPending); recoveryPending = false;
            await gate.WaitAsync(); try { await RefreshInner(); Publish(); } finally { gate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { audioGate.Release(); }
    }
}
