namespace SoundpadWeb;

public sealed partial class Bridge
{
    private readonly SpotifyControl spotify = new();
    private long sampledTick;
    private long seekSettlesUntil;
    private long? regionStart;
    private async Task<Reply?> Query(Command c, string actor)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || c.At < now - 30000 || c.At > now + 30000) return new("ack", c.Id, false, "Invalid or expired command.");
        if (c.Op == "audioAssets") return new("ack", c.Id, true, Data: new { manager = Audio.Public, locations = actor == "host" ? Audio.HostLocations() : null });
        if (c.Op == "waveform")
        {
            var catalog = parsed; if (c.SoundId == null || !catalog.Paths.TryGetValue(c.SoundId, out var path)) return new("ack", c.Id, false, "Unknown sound.");
            return new("ack", c.Id, true, Data: waveformService.GetSnapshot(c.SoundId, path, playback.SoundId == c.SoundId ? 0 : 10));
        }
        if (c.Op == "spotify" && c.Action is not ("enable" or "disable")) { if (actor != "host" && c.Action is not (null or "status") && !disk.Permissions.GuestSpotify) return new("ack", c.Id, false, "Guest Spotify controls are disabled."); return new("ack", c.Id, true, Data: disk.SpotifyEnabled ? await spotify.Request(c.Action ?? "status") : new SpotifyState(false, Error: "Spotify integration is disabled.", Enabled: false)); }
        return null;
    }
    private async Task Success(string command) { if (!(await Pipe.Request(command)).StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("Soundpad rejected this action or its API does not support it."); }
    /// <summary>
    /// Reads Soundpad's play status, position and duration into the playback record. The caller holds the gate.
    /// With <paramref name="starting"/> (the start that is taking this sample) it stops early as soon as that
    /// start should yield, so a newer play never waits for an older play's sampling (2.35).
    /// </summary>
    private async Task SamplePlayback(Intent? starting = null)
    {
        long seen; lock (playbackSync) seen = playbackVersion;
        var status = await Pipe.Request("GetPlayStatus()"); long? position = null, duration = null;
        if (status is "PLAYING" or "PAUSED" or "SEEKING")
        {
            NoteAudio();
            if (starting != null && ShouldYield(starting)) return;
            if (long.TryParse(await Pipe.Request("GetPlaybackPositionInMs()"), out var p) && p >= 0) position = p;
            if (starting != null && ShouldYield(starting)) return;
            if (long.TryParse(await Pipe.Request("GetPlaybackDurationInMs()"), out var d) && d > 0) duration = d;
        }
        long? loopTo = null; bool ended = false; ExactSlice? replay = null; SlotLease? free = null;
        lock (playbackSync)
        {
            // A Stop, Panic or start replaced the record while this sample was on its way (Stop does not wait for
            // the gate): what was read describes the playback before it, so it is dropped (2.35).
            if (seen != playbackVersion) return;
            bool known = playback.SoundId != null; long elapsed = playback.Status == "PLAYING" ? Environment.TickCount64 - sampledTick : 0;
            var cut = known ? slice : null; bool sliceEnded = false;
            if (cut != null)
            {
                // Soundpad reports the slot file (2.37): the sound's position is the slot's plus where the slice
                // starts, and the slot's own length stands for the sound's while it is the slice's length.
                long? raw = position; long now = Environment.TickCount64;
                if (position.HasValue) position += cut.OffsetMs;
                // The end clock stands still while the slice is paused, from the website or in Soundpad (review, 2.37).
                if (status == "PAUSED") cut.Pause(now); else if (status == "PLAYING") cut.Resume(now);
                bool atEnd = cut.PausedTick == null && now - cut.StartedTick >= cut.SliceMs - 60;
                if (duration is long slotMs && Math.Abs(slotMs - cut.SliceMs) <= 150)
                {
                    duration = cut.SourceMs;
                    if (status == "PLAYING") { cut.SeenPlaying = true; if (raw is long r && r >= cut.SliceMs - cut.NearMs) cut.NearEnd = true; }
                }
                // The slot ended while it was being read (its length is gone with it): it is at its end, and the next
                // look sees it stopped. Not a sign that another sound took over. Only when the length is missing: a
                // different length is another sound, which ends the record's identity like any other (review, 2.37).
                // A different length right at the end, after a reading near it, can be the same torn read: Soundpad
                // answered "playing" and then, the slot gone, a length that is not the slot's (the test Soundpad does;
                // a slow CI runner hit it, 2.37.0). One such reading is tolerated; the next look decides: stopped is the
                // slot's natural end, the other length again is another sound, which then ends the identity.
                else if (atEnd && status != "STOPPED" && (duration == null || cut.NearEnd && !cut.EndSuspect))
                {
                    if (duration != null) cut.EndSuspect = true;
                    position = cut.OffsetMs + cut.SliceMs; duration = cut.SourceMs; cut.NearEnd = true;
                }
                // Stopped after the slice's own length, and a reading saw it close to its end: the slot played to its
                // end, which is exactly the end of the region (or of the sound). A repeating region plays the slot
                // again; no seek is involved. A stop without that reading was the person stopping it in Soundpad.
                sliceEnded = status == "STOPPED" && atEnd && cut.NearEnd;
                if (sliceEnded && cut.Region && playback.Repeat) replay = cut;
            }
            if (replay == null)
            {
                // RC can report the pre-seek position or a transient stop while an accepted seek settles.
                // Trust only our short, epoch-bound transition window; a changed duration still invalidates identity.
                bool sameDuration = playback.DurationMs is not > 0 || duration is not > 0 || Math.Abs(playback.DurationMs.Value - duration.Value) <= 150;
                bool positionJump = known && position.HasValue && playback.PositionMs.HasValue && Math.Abs(position.Value - (playback.PositionMs.Value + elapsed)) > 1200;
                // A slice involves no seek: once its slot was seen playing, a stop is real (review, 2.37).
                bool sliceStopped = cut != null && cut.SeenPlaying && status == "STOPPED";
                if (known && !sliceEnded && !sliceStopped && Environment.TickCount64 < seekSettlesUntil && sameDuration &&
                    (status is "STOPPED" or "SEEKING" || status.StartsWith("R-") || positionJump || !position.HasValue))
                {
                    status = playback.Status == "PAUSED" ? "PAUSED" : "SEEKING";
                    position = playback.PositionMs; duration ??= playback.DurationMs;
                }
                // No vendor current-ID getter exists. Clear on contradictory observations; same-duration
                // native replacement cannot always be detected and is documented as a vendor limitation.
                if (status == "STOPPED" || status.StartsWith("R-") ||
                    (known && playback.DurationMs is > 0 && duration is > 0 && Math.Abs(playback.DurationMs.Value - duration.Value) > 150) ||
                    (known && position.HasValue && playback.PositionMs.HasValue && Math.Abs(position.Value - (playback.PositionMs.Value + elapsed)) > 1200)) known = false;
                playback = playback with
                {
                    Status = status is "PLAYING" or "PAUSED" or "SEEKING" or "STOPPED" ? status : "UNKNOWN",
                    PositionMs = position,
                    DurationMs = duration,
                    SoundId = known ? playback.SoundId : null,
                    Epoch = known ? playback.Epoch : null,
                    Requester = known ? playback.Requester : null,
                    IdentitySource = known ? "web" : "unknown",
                    SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RegionEndMs = known ? playback.RegionEndMs : null,
                    Repeat = known && playback.Repeat
                };
                sampledTick = Environment.TickCount64;
                if (!known) free = DropSliceLocked();
                // A slice ends by itself, exactly; only a sound played the old way has its region end found here.
                if (cut == null && playback.Status == "PLAYING" && playback.RegionEndMs is long end && position >= end)
                {
                    if (playback.Repeat && regionStart.HasValue) loopTo = regionStart.Value; else ended = true;
                }
            }
        }
        Free(free);
        if (replay != null) { await ReplaySlice(replay, seen); return; }
        if (loopTo is long from)
        {
            await Success($"DoSeekMs({from})");
            lock (playbackSync) { if (seen == playbackVersion) { seekSettlesUntil = Environment.TickCount64 + 1500; playback = playback with { PositionMs = from, PositionRevision = playback.PositionRevision + 1 }; sampledTick = Environment.TickCount64; } }
        }
        else if (ended)
        {
            await Success("DoStopSound()");
            lock (playbackSync) { if (seen == playbackVersion) { playbackVersion++; playback = new("STOPPED", SampledAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); regionStart = null; } }
        }
    }
    private static Participant Person(string actor, Participant? input)
    {
        var name = new string((input?.Name ?? (actor == "host" ? "Host" : "Guest")).Where(c => !char.IsControl(c)).Take(40).ToArray()).Trim();
        var id = Guid.TryParse(input?.Id, out var guid) ? guid.ToString() : actor == "host" ? "host" : "anonymous"; return new(id, string.IsNullOrEmpty(name) ? "Guest" : name);
    }
    /// <summary>
    /// Starts a sound at its current index. The caller holds the gate and has just looked the index up.
    /// Returns the new playback's epoch, or null when a newer intent replaced this one before the play reached
    /// Soundpad (nothing is counted or recorded then). Once Soundpad has accepted the play it counts as played;
    /// the sampling and cue seek that follow are cut short if a newer intent is about to take over (2.35).
    /// </summary>
    private async Task<string?> StartPlayback(Command c, string actor, Participant? participant, int index, Intent intent, ExactPlan? exact = null)
    {
        if (c.Op != "play" && (c.PositionMs is null or < 0 or > 1800000 || (c.Op == "region" && (c.EndMs is null or > 1800000 || c.EndMs <= c.PositionMs)))) throw new InvalidDataException("Invalid cue or region boundaries.");
        var statisticsId = managed?.Annotation(c.SoundId!)?.WorkspaceId ?? c.SoundId!;
        if (disk.Statistics.Count >= 50000 && !disk.Statistics.ContainsKey(statisticsId)) throw new InvalidDataException("Statistics storage limit reached.");
        if (!IsNewest(intent)) return null;
        // A cue start or region prepared as an exact slice plays its slot (2.37), while the slot is still listed
        // and still holds that slice; otherwise the sound itself, then the seek below, as before.
        int? slotIndex = exact == null || c.Op == "play" ? null : SlotIndexOf(exact.Lease, exact.Generation);
        // The pipe checks once more, immediately before it writes: a play replaced while it queued there is not sent.
        string answer;
        try { answer = await Pipe.Request($"DoPlaySound({slotIndex ?? index})", intent.Withdrawn.Token); }
        catch (OperationCanceledException) when (intent.Withdrawn.IsCancellationRequested) { return null; }
        if (!answer.StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("Soundpad rejected this action or its API does not support it.");
        NoteAudio();
        // Its sound comes back into the recording history: Latest phrase skips it (2.40). A slice lasts its slice;
        // a cue start the rest of the sound.
        NoteOwnReplay(c.SoundId, slotIndex != null ? (long?)exact!.SliceMs : parsed.Public.Sounds.FirstOrDefault(s => s.Id == c.SoundId)?.DurationMs is long whole ? Math.Max(0, whole - (c.Op == "play" ? 0 : c.PositionMs ?? 0)) : null);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var person = Person(actor, participant); var epoch = Guid.NewGuid().ToString();
        SlotLease? free;
        lock (playbackSync)
        {
            // A Stop or Panic that arrived while the play was on the wire was sent after it and has already
            // recorded the stop: that record stands, and this play (heard for an instant at most) is not counted.
            if (playbackOwner > intent.Revision) return null;
            playbackOwner = intent.Revision; playbackVersion++;
            // Whatever slice played before is replaced: its slot is free again (measured 2.36: at once).
            free = DropSliceLocked();
            if (slotIndex != null) { slice = new ExactSlice(exact!, c.Op == "region", intent); exact!.Taken = true; }
            // A slice's record is about the ORIGINAL sound: its id and duration, the position where the slice starts.
            playback = slotIndex == null ? new("PLAYING", 0, null, c.SoundId, epoch, person.Name, now, "web")
                : new("PLAYING", exact!.OffsetMs, exact.SourceMs, c.SoundId, epoch, person.Name, now, "web", c.Op == "region" ? Math.Min(c.EndMs!.Value, exact.SourceMs) : null, c.Op == "region" && c.Repeat);
            sampledTick = Environment.TickCount64; regionStart = null; seekSettlesUntil = Environment.TickCount64 + 1500;
        }
        Free(free);
        try
        {
            await SamplePlayback(intent);
            if (slotIndex != null) { Interlocked.Increment(ref exactStarts); WakeAfter(exact!.SliceMs); }
            else if (c.Op != "play" && exactEnabled) Interlocked.Increment(ref exactFallbacks);
            if (c.Op != "play" && slotIndex == null && !ShouldYield(intent))
            {
                for (int attempt = 0; playback.DurationMs is not > 0 && attempt < 3 && !ShouldYield(intent); attempt++) { await Task.Delay(50); await SamplePlayback(intent); }
                Playback current; bool mine;
                lock (playbackSync) { current = playback; mine = playbackOwner == intent.Revision; }
                if (mine && !ShouldYield(intent))
                {
                    if (current.DurationMs is not > 0) throw new InvalidDataException("Soundpad did not provide a duration for seeking.");
                    long start = Math.Clamp(c.PositionMs!.Value, 0, current.DurationMs.Value - 1), length = current.DurationMs.Value;
                    await Success($"DoSeekMs({start})");
                    lock (playbackSync)
                    {
                        if (playbackOwner == intent.Revision)
                        {
                            seekSettlesUntil = Environment.TickCount64 + 1500; playback = playback with { PositionMs = start, PositionRevision = playback.PositionRevision + 1, SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RegionEndMs = c.Op == "region" ? Math.Min(c.EndMs!.Value, length) : null, Repeat = c.Op == "region" && c.Repeat };
                            sampledTick = Environment.TickCount64; regionStart = c.Op == "region" ? start : null;
                        }
                    }
                }
            }
            var stats = disk.Statistics.GetValueOrDefault(statisticsId) ?? new SoundStatistics(); stats.Total++; stats.LastPlayedAt = now;
            var key = stats.Participants.Count < 32 || stats.Participants.ContainsKey(person.Id) ? person.Id : "other";
            stats.Participants[key] = new(key == "other" ? "Other participants" : person.Name, (stats.Participants.GetValueOrDefault(key)?.Count ?? 0) + 1);
            disk.Statistics[statisticsId] = stats; disk.StatisticsVersion++;
            disk.Activity.Add(new(c.Id, c.SoundId!, now, person.Id, person.Name, actor == "host" ? "host" : "guest")); if (disk.Activity.Count > 100) disk.Activity.RemoveRange(0, disk.Activity.Count - 100);
            return epoch;
        }
        catch
        {
            // The play started but its cue could not be set: stop it rather than leave it playing from the top.
            // No newer play can have started meanwhile (this holds the gate), so the stop hits only this sound.
            try { await Success("DoStopSound()"); } catch (Exception) { }
            lock (playbackSync) { if (playbackOwner == intent.Revision) { playbackVersion++; playback = new("STOPPED"); free = DropSliceLocked(); } }
            Free(free);
            throw;
        }
    }
    /// <summary>
    /// Seek or Pause for one playback (its sound and epoch). The caller holds the gate. False when that playback is
    /// no longer the current one, or is about to be replaced: the control is stale and is answered quietly (2.35).
    /// </summary>
    private async Task<bool> ControlPlayback(Command c)
    {
        if (!available || playback.SoundId == null || c.SoundId != playback.SoundId || c.Epoch != playback.Epoch) return false;
        await SamplePlayback();
        var current = playback;
        if (current.SoundId != c.SoundId || current.Epoch != c.Epoch || current.Status is not ("PLAYING" or "PAUSED" or "SEEKING") || AboutToChange()) return false;
        if (c.Op == "pause")
        {
            await Success("DoTogglePause()");
            ExactSlice? resumed = null; long played = 0;
            lock (playbackSync)
            {
                if (playback.Epoch == c.Epoch)
                {
                    bool pausing = playback.Status != "PAUSED"; long now = Environment.TickCount64;
                    playback = playback with { Status = pausing ? "PAUSED" : "PLAYING", SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }; sampledTick = now;
                    // A slice's end clock stands still while it is paused (review, 2.37).
                    if (slice is { } cut)
                    {
                        if (pausing) cut.Pause(now);
                        else { cut.Resume(now); resumed = cut; played = now - cut.StartedTick; }
                    }
                }
            }
            if (resumed != null) WakeAfter(resumed.SliceMs, played);
        }
        else
        {
            if (c.PositionMs is null or < 0 || current.DurationMs is not > 0) throw new InvalidDataException("Invalid seek target or unknown duration.");
            var target = Math.Min(c.PositionMs.Value, current.DurationMs.Value - 1);
            // A slice cannot be sought past its own range: a new slice starts from there instead (2.37).
            ExactSlice? cut; lock (playbackSync) cut = slice;
            if (cut != null) return await SeekSlice(c, cut, target, current.Status == "PAUSED");
            await Success($"DoSeekMs({target})");
            lock (playbackSync) { if (playback.Epoch == c.Epoch) { seekSettlesUntil = Environment.TickCount64 + 1500; playback = playback with { PositionMs = target, PositionRevision = playback.PositionRevision + 1, RegionEndMs = null, Repeat = false, SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }; regionStart = null; sampledTick = Environment.TickCount64; } }
        }
        return true;
    }
    private string[] StatisticsKeys(string soundId)
    {
        var annotation = managed?.Annotation(soundId);
        return annotation == null ? [soundId] : annotation.Aliases.Append(annotation.WorkspaceId).Append(soundId).Distinct().ToArray();
    }
    private SoundStatistics CombinedStatistics(string soundId)
    {
        var result = new SoundStatistics();
        foreach (var key in StatisticsKeys(soundId))
        {
            if (!disk.Statistics.TryGetValue(key, out var value)) continue;
            result.Total += value.Total; result.LastPlayedAt = Math.Max(result.LastPlayedAt ?? 0, value.LastPlayedAt ?? 0);
            foreach (var (id, person) in value.Participants) result.Participants[id] = new(person.Name, (result.Participants.GetValueOrDefault(id)?.Count ?? 0) + person.Count);
        }
        return result;
    }
    private Reply StatisticsReply(Command c) => new("ack", c.Id, true, Data: c.SoundId == null
        ? new { totals = disk.Statistics.Keys.Concat(parsed.Public.Sounds.Select(s=>s.Id)).Distinct().ToDictionary(id=>id, id=> { var stats=CombinedStatistics(id); return new {stats.Total,stats.LastPlayedAt}; }), recent = disk.Activity.TakeLast(20).ToArray() }
        : new { statistics = CombinedStatistics(c.SoundId), recent = disk.Activity.Where(a => StatisticsKeys(c.SoundId).Contains(a.SoundId)).TakeLast(20).ToArray() });
}
