using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
namespace SoundpadWeb;

public sealed partial class Bridge
{
    private ManagedAudioStore? managed;
    private readonly object managerInitialization = new();
    // One managed-audio job at a time (journal and file work). A play that needs preparation waits here in the
    // urgent lane and leaves the queue at once when a newer intent arrives (2.35).
    private readonly PriorityGate audioGate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<Reply>>> audioRequests = new();
    private readonly ConcurrentDictionary<string, byte> revokedAudioPeers = new();
    private long nextAudioRecovery;
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
                    // Each batch is atomic under the gate (see the gate's note in Bridge.cs) and waits in the
                    // background lane, so a play that is ready goes first.
                    await gate.EnterAsync(PriorityGate.Lane.Background); try { await work(); } finally { gate.Release(); }
                });
                managed.Changed += Publish; return managed;
            }
        }
    }
    private bool GuestStillAllowed(string actor, string? requester, string? audience) => actor == "host" ||
        (disk.Permissions.RemotePlayback && (audioAudience == null || sessionAccepting && audience == audioAudience) && (requester == null || !revokedAudioPeers.ContainsKey(requester)));
    private void AudioPermission(Command command, string actor, string? requester, string? audience)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (command.V is not (1 or 2) || !Guid.TryParseExact(command.Id, "D", out _) || Math.Abs(now - command.At) > 90000) throw new InvalidDataException("Invalid or expired audio command.");
        if (actor != "host" && command.Op == "removeAudio")
        {
            // Undo for guests (2.32): a guest may take back only temporary audio they added themselves (an
            // upload, edited copy or Mimic clip), while the host still allows guests to add audio.
            if (requester == null || !disk.Permissions.GuestUploads || (audioAudience != null && (!sessionAccepting || audience != audioAudience)) || revokedAudioPeers.ContainsKey(requester))
                throw new InvalidDataException("The host has not allowed guests to add or remove audio.");
            var asset = Audio.AssetById(command.AssetId ?? "");
            if (asset.UploaderId != requester || asset.State == "permanent" || asset.Promotion != null || asset.Variants.Any(v => v.Permanent || v.LegacyApproval))
                throw new InvalidDataException("Guests can remove only temporary audio they added themselves.");
            return;
        }
        if (actor != "host")
        {
            if (command.Op is not ("reverse" or "audioPlay" or "play" or "startAt" or "region")) throw new InvalidDataException("Host permission required.");
            if (!GuestStillAllowed(actor, requester, audience)) throw new InvalidDataException("Guest playback permission or session authorization changed.");
            if (command.Op is "startAt" or "region" && !disk.Permissions.GuestSeeking) throw new InvalidDataException("Guest seeking is disabled.");
        }
    }
    private Task<Reply> ExecuteAudio(Command c, string actor, Participant? participant, Intent? intent, string? requester, string? audience)
    {
        if (c.Op == "endSession" && actor == "host") sessionAccepting = false;
        // Plays of managed sounds have their own path (2.35): never refused as busy, and a repeated command id is
        // answered from its intent (see Execute), so they need no entry here.
        if (StartOperation(c.Op)) return PlayManaged(c, actor, participant, intent, requester, audience);
        if (audioRequests.Count >= 64) foreach (var item in audioRequests.Where(e => e.Value.IsValueCreated && e.Value.Value.IsCompleted)) audioRequests.TryRemove(item.Key, out _);
        if (audioRequests.Count >= 128) return Task.FromResult(new Reply("ack", c.Id, false, "Audio processing is busy."));
        return audioRequests.GetOrAdd(actor + ":" + requester + ":" + c.Id, _ => new(() => RunAudio(c, actor, requester, audience))).Value;
    }
    /// <summary>
    /// A play, cue start or region of a Temp or kept sound, "play this source" (audioPlay) or reverse (2.35).
    /// When the variant is already prepared it takes the same short path as a library sound: the sound-list
    /// check and DoPlaySound under the gate, with no wait for other audio work, no library refresh and no file
    /// hashing (see <see cref="ManagedAudioStore.Ready"/> for what is checked instead). Otherwise it is prepared
    /// first, one job at a time; while it waits for its turn a newer intent releases it with the quiet
    /// superseded answer. Preparation that has begun finishes (its result is kept) and then plays only if its
    /// intent is still the newest.
    /// </summary>
    private async Task<Reply> PlayManaged(Command c, string actor, Participant? participant, Intent? intent, string? requester, string? audience)
    {
        try
        {
            AudioPermission(c, actor, requester, audience);
            if (intent == null) throw new InvalidDataException("Guest playback permission or session authorization changed.");
            if (!sessionAccepting) throw new InvalidDataException("Hosting has ended.");
            if (ReadyFor(c) is { } ready && await PlayReady(c, actor, participant, intent, ready) is { } played) return played;
            string assetId, state; string? soundId;
            if (!await EnterFor(audioGate, intent)) return Superseded(c, "newer");
            try
            {
                AudioPermission(c, actor, requester, audience);
                if (!sessionAccepting) throw new InvalidDataException("Hosting has ended.");
                AudioAsset asset; string orientation;
                if (c.Op == "audioPlay") { asset = Audio.Public.Assets.Any(a => a.Id == c.AssetId) ? Audio.AssetById(c.AssetId!) : throw new InvalidDataException("Unknown source."); orientation = c.Orientation ?? "normal"; }
                else
                {
                    if (c.SoundId == null) throw new InvalidDataException("Choose a sound.");
                    var existing = Audio.Resolve(c.SoundId);
                    if (existing is { } known) { asset = known.Asset; orientation = c.Op == "reverse" ? known.Variant.Orientation == "normal" ? "reversed" : "normal" : known.Variant.Orientation; }
                    else
                    {
                        string path, name;
                        await gate.EnterAsync(PriorityGate.Lane.Normal);
                        try
                        {
                            await RefreshInner();
                            // The id comes from the file path and the mapping was just re-read, so a change elsewhere in the library
                            // (a category, an icon) is no reason to refuse (2.15.9).
                            if (!available || !parsed.Paths.TryGetValue(c.SoundId, out path!)) throw new InvalidDataException("That sound is no longer in Soundpad. Refresh and choose it again.");
                            name = parsed.Public.Sounds.Single(s => s.Id == c.SoundId).Name;
                        }
                        finally { gate.Release(); }
                        var source = await Audio.NativeSource(c.SoundId, path, name, Person(actor, participant).Name); asset = source.Asset; orientation = c.Op == "reverse" ? "reversed" : "normal";
                    }
                }
                var prepared = await Audio.Prepare(asset.Id, orientation, actor == "host" || disk.Permissions.GuestTransforms);
                assetId = asset.Id; state = asset.State; soundId = prepared.Variant.Native?.SoundId;
            }
            finally { audioGate.Release(); }
            // In the urgent lane while it may still play; superseded meanwhile, only the bookkeeping is left.
            if (!IsNewest(intent) || !await EnterFor(gate, intent)) await gate.EnterAsync(PriorityGate.Lane.Normal);
            try
            {
                if (disk.Receipts.ContainsKey(c.Id)) return new("ack", c.Id, true);
                await RefreshInner();
                if (!available || soundId == null || !parsed.Indexes.TryGetValue(soundId, out int index)) throw new InvalidDataException("The prepared native entry is unavailable. Its source is preserved in Temp.");
                string? epoch = null; bool allowed = sessionAccepting && GuestStillAllowed(actor, requester, audience) && (actor == "host" || !playbackSuspended && (c.Op is not ("startAt" or "region") || disk.Permissions.GuestSeeking));
                if (IsNewest(intent) && allowed)
                    epoch = await StartPlayback(c with { Op = c.Op is "startAt" or "region" ? c.Op : "play", SoundId = soundId }, actor, participant, index, intent);
                RememberReceipt(c); SaveLater(); Publish(); CommandAccepted?.Invoke();
                // Prepared but not played: the quiet answer, with what was prepared. ("suspended" also covers a
                // guest whose permission to play ended while the sound was being prepared.)
                return new("ack", c.Id, true, Data: epoch != null ? new { assetId, state, played = true, epoch } : new { assetId, state, played = false, superseded = true, reason = allowed ? "newer" : "suspended" });
            }
            finally { gate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new("ack", c.Id, false, ex is InvalidDataException ? ex.Message : "Managed audio could not finish. Open Temp to inspect preserved recovery state."); }
    }
    /// <summary>The prepared variant this command would play, or null when it has to be prepared first.</summary>
    private ReadyAudio? ReadyFor(Command c)
    {
        var audio = Audio;
        if (c.Op == "audioPlay") return c.AssetId == null ? null : audio.Ready(c.AssetId, c.Orientation ?? "normal");
        if (c.SoundId == null || audio.Annotation(c.SoundId) is not { } known) return null;
        return audio.Ready(known.AssetId, c.Op == "reverse" ? known.Orientation == "normal" ? "reversed" : "normal" : known.Orientation);
    }
    /// <summary>
    /// The short path for a prepared variant. Null when Soundpad turned out not to list it any more (removed by
    /// hand, say): the caller then prepares it the long way, which imports it again.
    /// </summary>
    private async Task<Reply?> PlayReady(Command c, string actor, Participant? participant, Intent intent, ReadyAudio ready)
    {
        if (!await EnterFor(gate, intent)) return Superseded(c, "newer");
        try
        {
            if (disk.Receipts.ContainsKey(c.Id)) return new("ack", c.Id, true);
            var found = await FreshIndex(ready.SoundId, intent);
            if (found.Result == Lookup.Withdrawn) return Superseded(c, "newer");
            if (found.Result == Lookup.Missing) return null;
            var epoch = await StartPlayback(c with { Op = c.Op is "startAt" or "region" ? c.Op : "play", SoundId = ready.SoundId }, actor, participant, found.Index, intent);
            if (epoch == null) return Superseded(c, "newer");
            RememberReceipt(c); SaveLater(); Publish(); CommandAccepted?.Invoke();
            return new("ack", c.Id, true, Data: new { assetId = ready.AssetId, state = ready.State, played = true, epoch });
        }
        finally { gate.Release(); }
    }
    /// <summary>
    /// "... and play" for a copy, clip or import that was just made (2.35): its intent was reserved when the
    /// command arrived, and the new sound starts only if that intent is still the newest and the person may still
    /// play. The answer is the save's own data plus <c>played</c> (and the epoch when it did play).
    /// </summary>
    private async Task<Reply> Autoplay(Reply saved, Command c, string actor, Participant? participant, string? requester, string? audience, Intent? intent)
    {
        if (!saved.Ok || Field(saved, "assetId") is not string assetId) return saved;
        var state = Field(saved, "state"); string? epoch = null;
        if (intent != null && IsNewest(intent) && sessionAccepting && GuestStillAllowed(actor, requester, audience) && (actor == "host" || !playbackSuspended))
        {
            // The same path as audioPlay, under the intent reserved on arrival.
            var play = await PlayManaged(c with { Op = "audioPlay", At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AssetId = assetId, Orientation = "normal", SoundId = null, PositionMs = null, EndMs = null }, actor, participant, intent, requester, audience);
            if (play.Ok) epoch = Field(play, "epoch");
        }
        return saved with { Data = epoch != null ? new { assetId, state, played = true, epoch } : new { assetId, state, played = false } };
    }
    private static string? Field(Reply reply, string name) => reply.Data?.GetType().GetProperty(name)?.GetValue(reply.Data) as string;
    private async Task<Reply> RunAudio(Command c, string actor, string? requester, string? audience)
    {
        bool entered = false;
        try
        {
            AudioPermission(c, actor, requester, audience);
            if (Interlocked.Increment(ref audioWaiting) > 2 && c.Op is not ("endSession" or "beginSession")) { Interlocked.Decrement(ref audioWaiting); return new("ack", c.Id, false, "Audio preparation is busy. Playback controls remain available; retry shortly."); }
            entered = true; await audioGate.EnterAsync(PriorityGate.Lane.Normal);
            try
            {
                AudioPermission(c, actor, requester, audience);
                await gate.EnterAsync(PriorityGate.Lane.Normal); try { if (disk.Receipts.ContainsKey(c.Id)) return new("ack", c.Id, true); } finally { gate.Release(); }
                if (c.Op == "beginSession")
                {
                    if (!Audio.Public.Blocked) Audio.BeginSession(); sessionAccepting = true; audioAudience = c.AudienceId; revokedAudioPeers.Clear();
                }
                else if (c.Op == "endSession") { if (!Audio.Public.Blocked) { Audio.RetireSession(); await Audio.Cleanup(); } }
                else if (c.Op == "retryCleanup") await Audio.Recover();
                else if (c.Op == "clearTemp")
                {
                    if (actor != "host") throw new InvalidDataException("Only the host can clear temporary audio.");
                    // Cleanup never removes anything while audio plays; stop it first if it's a Temp sound. The stop
                    // goes through the same arbitration as Stop (2.35): no start asked for earlier plays after it.
                    await StopTemporaryPlayback();
                    int cleared = await Audio.ClearTemporary();
                    Log($"Cleared {cleared} temporary audio file(s) and the Temp category");
                    await gate.EnterAsync(PriorityGate.Lane.Normal); try { await RefreshInner(); SaveAudioReceipt(c); Publish(); } finally { gate.Release(); }
                    return new("ack", c.Id, true, Data: new { cleared, pending = Audio.Public.Assets.Count(a => a.State == "cleanup-pending") });
                }
                else if (c.Op == "cleanupCategories")
                {
                    if (actor != "host") throw new InvalidDataException("Only the host can change Soundpad's categories.");
                    int removed = await Audio.RemoveDuplicateCategories();
                    Log($"Removed {removed} duplicate Temp categories from Soundpad");
                    await Audio.Recover();
                    await gate.EnterAsync(PriorityGate.Lane.Normal); try { await RefreshInner(); SaveAudioReceipt(c); Publish(); } finally { gate.Release(); }
                    return new("ack", c.Id, true, Data: new { removed });
                }
                else if (c.Op == "removeAudio")
                {
                    var assetId = c.AssetId ?? throw new InvalidDataException("Choose a managed source.");
                    // Undo (2.30; guests 2.32, for their own audio): "stopIfPlaying" stops the sound first only when it is this source that
                    // the companion itself started (Save & play, Mimic & play). Soundpad can't say what is
                    // playing, but the companion knows what it last played. Anything else keeps playing, and
                    // Remove then refuses as before.
                    if (c.Action == "stopIfPlaying" && (actor == "host" || disk.Permissions.RemotePlayback) && playback.SoundId is string current
                        && parsed.Public.Sounds.FirstOrDefault(s => s.Id == current)?.AssetId == assetId
                        && await pipe.Request("GetPlayStatus()") != "STOPPED")
                    {
                        // Through the same arbitration as Stop (2.35): the intent is taken first, so no start asked
                        // for earlier can play this source after it was stopped, and the playback record follows.
                        await StopNow(ReserveInternal(true));
                        for (int i = 0; i < 20 && await pipe.Request("GetPlayStatus()") != "STOPPED"; i++) await Task.Delay(100);
                    }
                    await Audio.Remove(assetId);
                }
                else if (c.Op == "retryAudio") await Audio.RetryAsset(c.AssetId ?? throw new InvalidDataException("Choose a managed source."));
                else if (c.Op == "keepAudio") await Audio.Promote(c.AssetId ?? throw new InvalidDataException("Open Temp to confirm a permanent destination."), c.VariantId ?? throw new InvalidDataException("Choose the normal or reversed variant."), c.DestinationDirectory, c.CategoryId, c.CategoryName, c.Confirmed);
                else throw new InvalidDataException("Unsupported command.");
                await gate.EnterAsync(PriorityGate.Lane.Normal); try { await RefreshInner(); SaveAudioReceipt(c); Publish(); } finally { gate.Release(); }
                return new("ack", c.Id, true);
            }
            finally { audioGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new("ack", c.Id, false, ex is InvalidDataException ? ex.Message : "Managed audio could not finish. Open Temp to inspect preserved recovery state."); }
        finally { if (entered) Interlocked.Decrement(ref audioWaiting); }
    }
    /// <summary>The caller holds the gate. Managed-audio changes are durable before their answer, as before.</summary>
    private void SaveAudioReceipt(Command c) { RememberReceipt(c); SaveNow(); }
    public async Task<Reply> Upload(byte[] bytes, string name, string by, bool host = false, string owner = "anonymous", string? audience = null, bool mimic = false)
    {
        if (!sessionAccepting || (!host && (!(mimic ? disk.Permissions.GuestMimic : disk.Permissions.GuestUploads) || (audioAudience != null && audience != audioAudience) || revokedAudioPeers.ContainsKey(owner)))) return new("ack", "upload", false, "Guest uploads are disabled or this session ended.");
        if (Interlocked.Increment(ref audioWaiting) > 2) { Interlocked.Decrement(ref audioWaiting); return new("ack", "upload", false, "Audio processing is busy; retry shortly."); }
        await audioGate.EnterAsync(PriorityGate.Lane.Normal);
        try
        {
            if (!sessionAccepting || (!host && (!(mimic ? disk.Permissions.GuestMimic : disk.Permissions.GuestUploads) || (audioAudience != null && audience != audioAudience) || revokedAudioPeers.ContainsKey(owner)))) throw new InvalidDataException("Upload authorization changed.");
            var asset = await Audio.Upload(bytes, name, by, owner, host);
            await gate.EnterAsync(PriorityGate.Lane.Normal); try { await RefreshInner(); Publish(); } finally { gate.Release(); }
            return new("ack", "upload", true, Data: new { assetId = asset.Id, state = asset.State });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new("ack", "upload", false, ex is InvalidDataException ? ex.Message : "Upload processing failed. The staged recovery record is available in Temp."); }
        finally { audioGate.Release(); Interlocked.Decrement(ref audioWaiting); }
    }
    public async Task CleanupSession()
    {
        // Nothing asked for before this point may start any more (it sends no Stop itself).
        StopMimic(); sessionAccepting = false; ReserveInternal(false);
        try
        {
            await audioGate.EnterAsync(PriorityGate.Lane.Normal);
            try { if (!Audio.Public.Blocked) { Audio.RetireSession(); await Audio.Cleanup(); } recoveryPending = false; }
            finally { audioGate.Release(); }
        }
        // Statistics of the last plays reach the disk before a clean exit (2.35).
        finally { await FlushState(); }
    }
    private async Task RecoverAudioIfDue(bool force)
    {
        if (!recoveryPending && (managed == null || !managed.Public.Assets.Any(a => a.State is "waiting-soundpad" or "promotion-pending" or "cleanup-pending"))) return;
        if (!force && Environment.TickCount64 < nextAudioRecovery || !audioGate.TryEnter()) return;
        try
        {
            nextAudioRecovery = Environment.TickCount64 + 5000;
            if (Audio.Public.Blocked) return;
            await Audio.Recover(recoveryPending); recoveryPending = false;
            await gate.EnterAsync(PriorityGate.Lane.Background); try { await RefreshInner(); Publish(); } finally { gate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { audioGate.Release(); }
    }
}
