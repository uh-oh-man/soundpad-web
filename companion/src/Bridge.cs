using System.Text.Json;
namespace SoundpadWeb;

public sealed partial class Bridge(ISoundpad pipe, StateStore store) : IDisposable
{
    private ISoundpad Pipe => pipe;
    private StateStore Store => store;
    private readonly NativeStatistics nativeStatistics = new(pipe is SoundpadPipe { IsProduction: true } ? null : Path.Combine(store.DirectoryPath, "vendor-fixture"));
    private readonly Waveforms waveformService = new(store.DirectoryPath);
    private Dictionary<string, string>? iconCache;
    // The "native gate": one holder at a time for the parsed library, the playback record's samples, the state
    // on disk and every sequence of Soundpad requests that must not be interleaved with a play (2.35: by lane, see
    // PriorityGate). Sequences that stay atomic under it:
    //  - a play: read the sound list, look the sound up, DoPlaySound(index). Indexes are positions, and a managed
    //    batch that removes entries (Temp category removal, Undo) shifts them, so no batch may run in between;
    //  - a library refresh (sound list and categories must describe the same moment);
    //  - every managed-audio batch (read the catalog, change it, read it again to verify).
    // Stop and Panic do NOT take the gate: DoStopSound carries no index and is harmless between any two requests of
    // the sequences above, so it waits only for the one pipe request already in flight (see StopNow).
    private readonly PriorityGate gate = new();
    private int waiting;
    private DiskState disk = store.Load();
    private ParsedLibrary parsed = new(new("empty", [], []), [], []);
    private bool recoveryPending = true;
    private bool available;
    private string? error = "Waiting for Soundpad. Open it normally; the companion will reconnect.";
    private Playback playback = new();
    private long revision;
    public event Action<Snapshot>? Changed;
    public event Action<Waveform> WaveformChanged { add => waveformService.Changed += value; remove => waveformService.Changed -= value; }
    public event Action? CommandAccepted;
    public void Dispose()
    {
        // Playback statistics still waiting for their deferred write go to disk now (2.35).
        Interlocked.Exchange(ref disposed, 1);
        if (Volatile.Read(ref stateDirty) != 0) { try { Task.Run(FlushState).Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* the last durable save stands */ } }
        hotkey.Dispose(); waveformService.Dispose(); transcriber.Dispose(); mimicCapture.Dispose(); pcm.Dispose();
    }
    public Snapshot Snapshot => new(1, revision, available, error, parsed.Public, playback, disk.Counter, disk.Permissions, 2,
        Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Convert.FromBase64String(disk.IdentityKey), System.Text.Encoding.UTF8.GetBytes("workspace-host-v2"))).ToLowerInvariant(), disk.StatisticsVersion, typeof(Bridge).Assembly.GetName().Version?.ToString(3), managed?.Public.Revision ?? 0, MimicSnapshot, playbackSuspended);
    public string? Website => disk.Website;
    public void SetWebsite(string site) { disk.Website = site; store.Save(disk); }
    private void Publish() { Interlocked.Increment(ref revision); Changed?.Invoke(Snapshot); }
    /// <summary>The sound list the current mapping was parsed from; a play only needs a full refresh when it differs.</summary>
    private string? lastSoundlist, lastPlainCategories, lastIconCategories;
    private long iconsDueAt;
    private async Task RefreshInner()
    {
        try
        {
            var all = await pipe.Request("GetSoundlist()");
            // Categories with icons are slow on big libraries (about 0.35 s and 1.4 MB on the owner's Soundpad), and a
            // play waits while they load. Read them without icons (about 35 ms) and fetch icons again only when the
            // categories changed, or every 5 minutes so a changed icon still shows (2.16.1).
            var plain = await pipe.Request("GetCategories(true, false)");
            if (plain != lastPlainCategories || lastIconCategories == null || Environment.TickCount64 >= iconsDueAt)
            {
                lastIconCategories = await pipe.Request("GetCategories(true, true)");
                lastPlainCategories = plain; iconsDueAt = Environment.TickCount64 + 300000;
            }
            var cats = lastIconCategories;
            if (cats.StartsWith("R-404")) throw new InvalidDataException("This Soundpad version does not support categories. Update Soundpad.");
            // The app's exact-playback slots are hidden from the library the website sees (2.37).
            var next = HideSlots(LibraryParser.Parse(all, cats, disk, iconCache ??= store.ReadIcons(disk), nativeStatistics));
            parsed = next with { Public = next.Public with { Sounds = next.Public.Sounds.Select(s => managed?.Annotation(s.Id) is { } info ? s with { Name = info.Kind == "native" ? s.Name : info.Name, Icon = s.Icon ?? iconCache?.GetValueOrDefault(info.WorkspaceId) ?? info.Aliases.Select(id=>iconCache?.GetValueOrDefault(id)).FirstOrDefault(icon=>icon != null), Temporary = info.Temporary, UploadedBy = info.Uploader, AssetId = info.AssetId, Orientation = info.Orientation, AudioKind = info.Kind, InternalVariant = info.InternalVariant, AliasOf = info.AliasOf, WorkspaceId = info.WorkspaceId, Aliases = info.Aliases } : s).ToArray() } }; parsed = parsed with { Public = parsed.Public with { Version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Json.Write(parsed.Public)))).ToLowerInvariant()[..24] } }; available = true; error = null; lastSoundlist = all;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or System.Xml.XmlException or ArgumentException)
        {
            available = false; SlotLease? free; lock (playbackSync) { playbackVersion++; playback = new(); free = DropSliceLocked(); } Free(free);
            error = ex is InvalidDataException ? ex.Message : "Soundpad is unavailable. Open it normally; reconnection is automatic.";
        }
    }
    public async Task Poll(bool library)
    {
        // Soundpad's own statistics files are read before the gate is taken (2.35), so a play never waits for them.
        if (library) await Task.Run(nativeStatistics.Refresh);
        // Skipped while the gate stays busy, as before. It waits a moment first (2.35): the deferred state save
        // holds the gate for an instant, and that should not cost a whole poll.
        if (!await gate.EnterWithin(PriorityGate.Lane.Background, 150)) return;
        try
        {
            if (library || !available) await RefreshInner();
            if (available)
            {
                try
                {
                    await SamplePlayback();
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { available = false; SlotLease? free; lock (playbackSync) { playbackVersion++; playback = new(); free = DropSliceLocked(); } Free(free); error = "Soundpad disconnected; reconnecting automatically."; }
                // Soundpad refused the seek or stop at the end of a region (2.35). This used to end the companion;
                // the next poll simply tries again.
                catch (InvalidDataException) { }
            }
            Publish();
        }
        finally { gate.Release(); }
        await RecoverAudioIfDue(library);
        await RegisterSlotsIfDue();
    }
    public Task<Reply> Execute(Command c, string actor, Participant? participant = null, string? requester = null, string? audience = null)
    {
        // Before anything else, and without waiting: the playback intent is reserved in the order commands arrive
        // here, never by client clocks (2.35). Everything after this may wait; this may not.
        Reservation reserved;
        try { reserved = ReserveIntent(c, actor, requester, audience); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Task.FromException<Reply>(ex); } // as from an async method: never thrown at the caller
        if (reserved.Quiet != null) return Task.FromResult(reserved.Quiet);
        // A command id seen before gets the first one's answer: it acts, and counts, once.
        if (reserved.Repeated) return reserved.Intent!.Answer.Task;
        var work = Dispatch(c, actor, participant, requester, audience, reserved.Intent);
        return reserved.Intent == null ? work : Answered(reserved.Intent, c, work);
    }
    private static async Task<Reply> Answered(Intent intent, Command c, Task<Reply> work)
    {
        try { var reply = await work; intent.Answer.TrySetResult(reply); return reply; }
        catch { intent.Answer.TrySetResult(new("ack", c.Id, false, "Companion operation failed. Check Soundpad and local storage.")); throw; }
    }
    private async Task<Reply> Dispatch(Command c, string actor, Participant? participant, string? requester, string? audience, Intent? intent)
    {
        if (IsMimic(c.Op)) return await ExecuteMimic(c, actor, participant, requester, audience, intent);
        if (c.Op == "trimAudio") return await TrimAudio(c, actor, participant, requester, audience, intent: intent);
        if (c.Op == "trimPreview") return await TrimAudio(c, actor, participant, requester, audience, preview: true);
        if (c.Op == "endSession" && actor == "host") { StopMimic(); Publish(); }
        var query = await Query(c, actor); if (query != null) return query;
        if (c.Op == "stop") return await ExecuteStop(c, actor, requester, audience, intent);
        if (c.Op == "panic") return await ExecutePanic(c, actor, intent);
        if (c.Op is "reverse" or "audioPlay" or "keepAudio" or "removeAudio" or "retryAudio" or "retryCleanup" or "cleanupCategories" or "clearTemp" or "beginSession" or "endSession" || (c.Op is "play" or "startAt" or "region" && c.SoundId != null && managed?.Annotation(c.SoundId) is { Kind: not "native" })) return await ExecuteAudio(c, actor, participant, intent, requester, audience);
        return await ExecutePlain(c, actor, participant, requester, audience, intent);
    }
    /// <summary>The guest rules of the plain commands; throws the refusal the guest is shown.</summary>
    private void RequirePlainPermission(Command c, string actor, string? requester, string? audience)
    {
        if (actor == "host") return;
        if (audioAudience != null && (!sessionAccepting || audience != audioAudience) || requester != null && revokedAudioPeers.ContainsKey(requester)) throw new InvalidDataException("This guest session authorization changed.");
        if (c.Op is not ("play" or "stop" or "counter" or "seek" or "pause" or "startAt" or "region" or "statistics" or "reverse")) throw new InvalidDataException("Host permission required.");
        if ((c.Op is "play" or "stop" or "seek" or "pause" or "startAt" or "region" or "reverse") && !disk.Permissions.RemotePlayback) throw new InvalidDataException("Remote playback is disabled.");
        if ((c.Op is "seek" or "pause" or "startAt" or "region") && !disk.Permissions.GuestSeeking) throw new InvalidDataException("Guest seeking is disabled.");
        if (c.Op == "counter" && !disk.Permissions.GuestCounter) throw new InvalidDataException("Guest counter changes are disabled.");
    }
    /// <summary>
    /// Writes the state durably before returning, for everything that must be on disk before its answer (the
    /// counter, permissions, icons, cleared statistics, the Spotify setting). If the write fails, the committed
    /// state is read back so the change that could not be saved does not stay in memory: never acknowledge a
    /// non-durable counter. Refusals that changed nothing no longer re-read the file (2.35).
    /// </summary>
    private void SaveNow(bool pruneIcons = false)
    {
        try { store.Save(disk, pruneIcons); Interlocked.Exchange(ref stateDirty, 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            disk = store.Load(); iconCache = null;
            throw new IOException("The state could not be saved.", ex);
        }
    }
    private void RememberReceipt(Command c)
    {
        // Retain receipts longer than every command's acceptance window, across process restarts.
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
        foreach (var id in disk.Receipts.Where(p => p.Value < cutoff).Select(p => p.Key).ToArray()) disk.Receipts.Remove(id);
        disk.Receipts[c.Id] = c.At;
    }
    private async Task<Reply> ExecutePlain(Command c, string actor, Participant? participant, string? requester, string? audience, Intent? intent)
    {
        bool start = c.Op is "play" or "startAt" or "region", control = c.Op is "seek" or "pause";
        // Playback is never refused as busy (2.35): a play that a newer one replaced leaves the queue at once, so
        // the queue cannot grow. The limit stays for everything else.
        bool counted = !start && !control;
        if (counted && Interlocked.Increment(ref waiting) > 24) { Interlocked.Decrement(ref waiting); return new("ack", c.Id, false, "Busy. Try again."); }
        ExactPlan? exact = null;
        try
        {
            // A cue start or region is rendered into a slot before the gate is taken (2.37), so nothing waits for it;
            // overtaken meanwhile, it plays nothing and is answered quietly.
            if (start && intent != null)
            {
                var prepared = await PrepareFor(c, c.SoundId == null ? null : parsed.Paths.GetValueOrDefault(c.SoundId), intent);
                if (prepared.Withdrawn) return Superseded(c, "newer");
                exact = prepared.Plan;
            }
            if (intent != null) { if (!await EnterFor(gate, intent)) return Superseded(c, "newer"); }
            else if (control) { if (!await EnterControl(c)) return Superseded(c, "stale"); }
            else await gate.EnterAsync(start ? PriorityGate.Lane.Urgent : PriorityGate.Lane.Normal);
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Playback commands were checked for age when they arrived (ReserveIntent), not after waiting here.
                if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || counted && (c.At < now - 30000 || c.At > now + 30000)) throw new InvalidDataException("Invalid or expired command.");
                RequirePlainPermission(c, actor, requester, audience);
                if (disk.Receipts.ContainsKey(c.Id)) return new("ack", c.Id, true);
                object? data = null; bool icons = false;
                switch (c.Op)
                {
                    case "play":
                    case "startAt":
                    case "region":
                        if (c.SoundId == null) throw new InvalidDataException("Soundpad is unavailable. Open it normally; reconnection is automatic.");
                        if (intent == null) throw new InvalidDataException("Invalid or expired command.");
                        // Only the newest intent may start audio. One that a newer command replaced while it waited
                        // is answered quietly, without touching Soundpad, statistics or state (2.35).
                        var found = await FreshIndex(c.SoundId, intent);
                        if (found.Result == Lookup.Withdrawn) return Superseded(c, "newer");
                        if (found.Result == Lookup.Missing) throw new InvalidDataException("That sound is no longer in Soundpad. Refresh and choose it again.");
                        var epoch = await StartPlayback(c, actor, participant, found.Index, intent, exact);
                        if (epoch == null) return Superseded(c, "newer");
                        data = new { played = true, epoch };
                        break;
                    case "seek":
                    case "pause": if (!await ControlPlayback(c)) return Superseded(c, "stale"); break;
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
                        var iconIdentity = managed?.Annotation(c.SoundId);
                        var iconId = iconIdentity?.WorkspaceId ?? c.SoundId;
                        if (string.IsNullOrEmpty(c.Image)) { foreach(var key in (iconIdentity?.Aliases ?? [c.SoundId]).Append(iconId).Distinct()) { disk.Icons.Remove(key); disk.IconFiles.Remove(key); } }
                        else
                        {
                            if (disk.IconFiles.Count >= 1024 && !disk.IconFiles.ContainsKey(iconId)) throw new InvalidDataException("Custom icon limit is 1024 sounds.");
                            var normalizedIcon = IconData.Normalize(c.Image);
                            var existingIcons = iconCache ??= store.ReadIcons(disk);
                            if (existingIcons.Where(p => p.Key != iconId).Sum(p => (long)p.Value.Length) + normalizedIcon.Length > 4 * 1024 * 1024) throw new InvalidDataException("Custom icons exceed the 4 MiB shared metadata budget. Remove an unused icon and try again.");
                            disk.IconFiles[iconId] = store.WriteIcon(iconId, normalizedIcon); disk.Icons.Remove(iconId);
                        }
                        iconCache = null; icons = true; await RefreshInner(); break;
                    case "revokePeer":
                        if (c.OwnerId == null || c.OwnerId.Length > 128) throw new InvalidDataException("Invalid guest identity."); revokedAudioPeers[c.OwnerId] = 1; break;
                    case "refresh": await RefreshInner(); break;
                    default: throw new InvalidDataException("Unsupported command.");
                }
                RememberReceipt(c);
                // Playback never waits for the disk (2.35): its receipt, statistics and activity are in memory now
                // and written by SaveLater. Everything else is durable before its answer, as before; the icon
                // folder is scanned for obsolete files only when an icon changed.
                if (start) SaveLater(); else if (counted) SaveNow(icons);
                Publish(); CommandAccepted?.Invoke();
                return new("ack", c.Id, true, Data: data);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ArgumentException or FormatException or System.Xml.XmlException)
            {
                Publish();
                return new("ack", c.Id, false, ex is InvalidDataException ? ex.Message : "Companion operation failed. Check Soundpad and local storage.");
            }
            finally { gate.Release(); }
        }
        finally { Discard(exact); if (counted) Interlocked.Decrement(ref waiting); }
    }
}
