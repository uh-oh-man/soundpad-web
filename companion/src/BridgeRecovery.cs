namespace SoundpadWeb;

/// <summary>One part of what a crashed launch left (2.45): how many, how long (when known) and how many bytes.</summary>
public sealed record RecoveryPart(int Count, long? Ms, long Bytes);
/// <summary>
/// What "Recover previous session / Discard" covers (2.45, sessionRecover): the Temp clips, the recording history and
/// the host's drafts a launch that did not close normally left. A part is null when there is none of it.
/// </summary>
public sealed record SessionRecoveryView(bool Pending, RecoveryPart? Temp, RecoveryPart? History, RecoveryPart? Drafts);

// One choice after a crash (2.45): Recover previous session keeps the Temp clips, the recovered recording history and
// the host's drafts (drafts.v1.json, see DraftStore.cs); Discard removes exactly those and never anything kept.
// Until 2.44 these were two separate questions (Temp, 2.40; history, 2.41) and drafts were lost. tempRecover and
// historyDiscard still answer for older pages. Also the guests' storage action history (budgetActions).
public sealed partial class Bridge
{
    private static bool IsRecoveryOp(string op) => op is "sessionRecover" or "budgetActions";

    /// <summary>What is waiting for the host's Recover previous session or Discard right now.</summary>
    public SessionRecoveryView SessionRecovery()
    {
        RecoveryPart? temp = null, recovered = null, kept = null;
        int clips = tempRecovery;
        if (clips > 0) temp = new(clips, null, LeftoverBytes());
        long ms = Interlocked.Read(ref recoveredMs);
        var h = history;
        if (ms > 0 && h != null) recovered = new(1, ms, ms * h.Rate / 1000 * 2);
        Draft[] pending; lock (drafts) pending = [.. recoveredDrafts];
        if (pending.Length > 0 && h != null)
        {
            long frames = pending.Sum(d => d.End - d.Start);
            kept = new(pending.Length, frames * 1000 / h.Rate, frames * 2);
        }
        return new(temp != null || recovered != null || kept != null, temp, recovered, kept);
    }

    /// <summary>True while any part of a crash's Recover previous session / Discard is still unanswered (cheap; no sizes).</summary>
    private bool RecoveryPendingNow
    {
        get
        {
            if (tempRecovery > 0) return true;
            if (Interlocked.Read(ref recoveredMs) > 0 && history != null) return true;
            lock (drafts) return recoveredDrafts.Count > 0 && history != null;
        }
    }

    /// <summary>The size of exactly the temporary files the crashed launch left.</summary>
    private long LeftoverBytes()
    {
        long bytes = 0;
        foreach (var f in tempLeftovers)
        {
            try { bytes += Audio.AssetById(f.AssetId).Variants.Where(v => f.VariantIds.Contains(v.Id)).Sum(v => v.Bytes); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { }
        }
        return bytes;
    }

    /// <summary>The 2.45 recovery and storage-history commands. Null when the op is not one of them.</summary>
    private async Task<Reply?> ExecuteRecovery(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        if (!IsRecoveryOp(c.Op)) return null;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && (audioAudience != null && (!sessionAccepting || audience != audioAudience) || requester != null && revokedAudioPeers.ContainsKey(requester))) throw new InvalidDataException("This guest session authorization changed.");
            if (c.Op == "budgetActions")
            {
                // The host may read any guest's; a guest only their own, whatever guestId they send.
                string guest = host ? c.GuestId is { Length: > 0 and <= 128 } id ? id : throw new InvalidDataException("Choose a guest.") : PersonKey(actor, participant, requester);
                return new("ack", c.Id, true, Data: new { guestId = guest, actions = budgets.Actions(guest) });
            }
            if (!host) throw new InvalidDataException("Only the host can recover or discard the previous session.");
            switch (c.Action)
            {
                case null or "status": return new("ack", c.Id, true, Data: SessionRecovery());
                case "recover":
                    {
                        var before = SessionRecovery();
                        if (tempRecovery > 0) await DecideTempRecovery("recover");
                        // The recovered history stays as it is (it already is the current history); the choice is made.
                        lock (historyGate) recoveredUntil = null;
                        Interlocked.Exchange(ref recoveredMs, 0);
                        int drafted = RestoreRecoveredDrafts();
                        Log($"Recovered the previous session: {before.Temp?.Count ?? 0} clip(s) in Temp, {(before.History?.Ms ?? 0) / 1000} s of recording history, {drafted} draft(s)");
                        Publish();
                        return new("ack", c.Id, true, Data: before);
                    }
                case "discard":
                    {
                        var before = SessionRecovery();
                        if (tempRecovery > 0) await DecideTempRecovery("discard");
                        int dropped = DropRecoveredDrafts();
                        // Only what the crash recovered goes; what was recorded since stays (as historyDiscard, 2.41).
                        bool discarded = DiscardRecovered();
                        Interlocked.Exchange(ref recoveredMs, 0);
                        Log($"Discarded the previous session: its Temp clips, {(discarded ? "its recording history" : "no recording history")} and {dropped} draft(s); anything kept stays");
                        Publish();
                        return new("ack", c.Id, true, Data: before);
                    }
                default: throw new InvalidDataException("Recover or discard the previous session.");
            }
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
    }
}
