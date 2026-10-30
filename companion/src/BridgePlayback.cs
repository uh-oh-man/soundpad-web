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
        if (c.Op == "waveform")
        {
            var catalog = parsed; if (c.SoundId == null || !catalog.Paths.TryGetValue(c.SoundId, out var path)) return new("ack", c.Id, false, "Unknown sound.");
            return new("ack", c.Id, true, Data: await waveformService.Request(c.SoundId, path, playback.SoundId == c.SoundId ? 0 : 10));
        }
        if (c.Op == "spotify" && actor == "host" && c.Action is not ("enable" or "disable")) return new("ack", c.Id, true, Data: disk.SpotifyEnabled ? await spotify.Request(c.Action ?? "status") : new SpotifyState(false, Error: "Spotify integration is disabled.", Enabled: false));
        return null;
    }
    private async Task Success(string command) { if (!(await Pipe.Request(command)).StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("Soundpad rejected this action or its API does not support it."); }
    private async Task SamplePlayback()
    {
        var status = await Pipe.Request("GetPlayStatus()"); long? position = null, duration = null;
        if (status is "PLAYING" or "PAUSED" or "SEEKING")
        {
            if (long.TryParse(await Pipe.Request("GetPlaybackPositionInMs()"), out var p) && p >= 0) position = p;
            if (long.TryParse(await Pipe.Request("GetPlaybackDurationInMs()"), out var d) && d > 0) duration = d;
        }
        bool known = playback.SoundId != null; long elapsed = playback.Status == "PLAYING" ? Environment.TickCount64 - sampledTick : 0;
        // RC can report the pre-seek position or a transient stop while an accepted seek settles.
        // Trust only our short, epoch-bound transition window; a changed duration still invalidates identity.
        bool sameDuration = playback.DurationMs is not > 0 || duration is not > 0 || Math.Abs(playback.DurationMs.Value - duration.Value) <= 150;
        bool positionJump = known && position.HasValue && playback.PositionMs.HasValue && Math.Abs(position.Value - (playback.PositionMs.Value + elapsed)) > 1200;
        if (known && Environment.TickCount64 < seekSettlesUntil && sameDuration &&
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
        if (playback.Status == "PLAYING" && playback.RegionEndMs is long end && position >= end)
        {
            if (playback.Repeat && regionStart.HasValue) { await Success($"DoSeekMs({regionStart.Value})"); seekSettlesUntil = Environment.TickCount64 + 1500; playback = playback with { PositionMs = regionStart.Value, PositionRevision = playback.PositionRevision + 1 }; sampledTick = Environment.TickCount64; }
            else { await Success("DoStopSound()"); playback = new("STOPPED", SampledAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); regionStart = null; }
        }
    }
    private static Participant Person(string actor, Participant? input)
    {
        var name = new string((input?.Name ?? (actor == "host" ? "Host" : "Guest")).Where(c => !char.IsControl(c)).Take(40).ToArray()).Trim();
        var id = Guid.TryParse(input?.Id, out var guid) ? guid.ToString() : actor == "host" ? "host" : "anonymous"; return new(id, string.IsNullOrEmpty(name) ? "Guest" : name);
    }
    private async Task StartPlayback(Command c, string actor, Participant? participant, int index)
    {
        if (c.Op != "play" && (c.PositionMs is null or < 0 or > 1800000 || (c.Op == "region" && (c.EndMs is null or > 1800000 || c.EndMs <= c.PositionMs)))) throw new InvalidDataException("Invalid cue or region boundaries.");
        if (disk.Statistics.Count >= 50000 && !disk.Statistics.ContainsKey(c.SoundId!)) throw new InvalidDataException("Statistics storage limit reached.");
        bool started = false;
        try
        {
            await Success($"DoPlaySound({index})"); started = true; seekSettlesUntil = Environment.TickCount64 + 1500;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var person = Person(actor, participant);
            playback = new("PLAYING", 0, null, c.SoundId, Guid.NewGuid().ToString(), person.Name, now, "web"); sampledTick = Environment.TickCount64; regionStart = null;
            await SamplePlayback();
            if (c.Op != "play")
            {
                for (int attempt = 0; playback.DurationMs is not > 0 && attempt < 3; attempt++) { await Task.Delay(50); await SamplePlayback(); }
                if (playback.DurationMs is not > 0) throw new InvalidDataException("Soundpad did not provide a duration for seeking.");
                long start = Math.Clamp(c.PositionMs!.Value, 0, playback.DurationMs.Value - 1);
                await Success($"DoSeekMs({start})"); seekSettlesUntil = Environment.TickCount64 + 1500; playback = playback with { PositionMs = start, PositionRevision = playback.PositionRevision + 1, SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RegionEndMs = c.Op == "region" ? Math.Min(c.EndMs!.Value, playback.DurationMs.Value) : null, Repeat = c.Op == "region" && c.Repeat };
                sampledTick = Environment.TickCount64; regionStart = c.Op == "region" ? start : null;
            }
            var stats = disk.Statistics.GetValueOrDefault(c.SoundId!) ?? new SoundStatistics(); stats.Total++; stats.LastPlayedAt = now;
            var key = stats.Participants.Count < 32 || stats.Participants.ContainsKey(person.Id) ? person.Id : "other";
            stats.Participants[key] = new(key == "other" ? "Other participants" : person.Name, (stats.Participants.GetValueOrDefault(key)?.Count ?? 0) + 1);
            disk.Statistics[c.SoundId!] = stats; disk.StatisticsVersion++;
            disk.Activity.Add(new(c.Id, c.SoundId!, now, person.Id, person.Name, actor == "host" ? "host" : "guest")); if (disk.Activity.Count > 100) disk.Activity.RemoveRange(0, disk.Activity.Count - 100);
        }
        catch { if (started) { try { await Success("DoStopSound()"); } catch (Exception) { } playback = new("STOPPED"); } throw; }
    }
    private async Task ControlPlayback(Command c)
    {
        if (!available || playback.SoundId == null || c.SoundId != playback.SoundId || c.Epoch != playback.Epoch) throw new InvalidDataException("Playback changed. This control belongs to an older sound.");
        await SamplePlayback();
        if (playback.SoundId != c.SoundId || playback.Epoch != c.Epoch || playback.Status is not ("PLAYING" or "PAUSED" or "SEEKING")) throw new InvalidDataException("Playback identity is unknown or changed.");
        if (c.Op == "pause") { await Success("DoTogglePause()"); playback = playback with { Status = playback.Status == "PAUSED" ? "PLAYING" : "PAUSED" }; }
        else { if (c.PositionMs is null or < 0 || playback.DurationMs is not > 0) throw new InvalidDataException("Invalid seek target or unknown duration."); var target = Math.Min(c.PositionMs.Value, playback.DurationMs.Value - 1); await Success($"DoSeekMs({target})"); seekSettlesUntil = Environment.TickCount64 + 1500; playback = playback with { PositionMs = target, PositionRevision = playback.PositionRevision + 1, RegionEndMs = null, Repeat = false }; regionStart = null; }
        playback = playback with { SampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }; sampledTick = Environment.TickCount64;
    }
    private Reply StatisticsReply(Command c) => new("ack", c.Id, true, Data: c.SoundId == null
        ? new { totals = disk.Statistics.ToDictionary(x => x.Key, x => new { x.Value.Total, x.Value.LastPlayedAt }), recent = disk.Activity.TakeLast(20).ToArray() }
        : new { statistics = disk.Statistics.GetValueOrDefault(c.SoundId) ?? new SoundStatistics(), recent = disk.Activity.Where(a => a.SoundId == c.SoundId).TakeLast(20).ToArray() });
}
