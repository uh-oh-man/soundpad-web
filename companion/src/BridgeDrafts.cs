using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
namespace SoundpadWeb;

// Drafts, Quick Mimic and private preview for guests (2.40). See Drafts.cs and reports/OVERHAUL-PLAN.md, "Rolling
// recording and clip editing". Capture opens a draft for the person who captured it instead of importing at once;
// Keep in Temp makes the clip (the 2.39 import), Play into game makes it and plays it.
public sealed partial class Bridge
{
    public const int MaxDraftsPerPerson = 50;
    /// <summary>
    /// Each guest's drafts may keep at most this share (percent) of the recording history's pin budget, and all guests'
    /// drafts together at most <see cref="GuestsPinPercent"/>: the rest is the host's, so guests' drafts never stop the
    /// host's Capture (2.40).
    /// </summary>
    public const int GuestPinPercent = 25, GuestsPinPercent = 50;
    /// <summary>A guest's drafts go once they have been out of the session this long (a reload or a short drop keeps them).</summary>
    public const int AwayGuestDraftMinutes = 10;
    /// <summary>Longest private preview (guests and host alike) and longest draft: the longest clip.</summary>
    public const int PreviewMaxMs = MimicWindowMs;
    /// <summary>Guest previews are sent at this rate (mono 16-bit, about 1 MB for 30 s) over the guest's connection.</summary>
    public const int GuestPreviewRate = 16000;
    /// <summary>Guest previews per minute, per guest.</summary>
    public const int GuestPreviewsPerMinute = 12;
    private readonly Dictionary<string, Draft> drafts = [];
    private long draftsRevision;
    private readonly OwnReplayLog ownReplays = new();
    private RateWindow previewRate = new(GuestPreviewsPerMinute, TimeSpan.FromMinutes(1));
    private readonly ConcurrentDictionary<string, byte> previewing = new();
    private readonly byte[] draftTagKey = RandomNumberGenerator.GetBytes(32);
    /// <summary>Guests in the session (participant ids from the owner page's session summary) and since when others have been away. Guarded by <see cref="drafts"/>.</summary>
    private HashSet<string> guestsHere = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> guestsAway = new(StringComparer.Ordinal);
    /// <summary>Test seam: the guests whose drafts are waiting to be let go because they left.</summary>
    public IReadOnlyCollection<string> GuestsAway { get { lock (drafts) return [.. guestsAway.Keys]; } }

    /// <summary>Test seam: another preview limit (a clock the test controls).</summary>
    internal void UsePreviewLimit(RateWindow limit) => previewRate = limit;
    /// <summary>Own-replay spans of the current history (tests; the timeline reply carries them too).</summary>
    public HistoryRange[] OwnReplays => history is { } h ? ownReplays.Ranges(h.SessionId) : [];

    private static string PersonKey(string actor, Participant? participant, string? requester) => actor == "host" ? "host" : participant?.Id ?? requester ?? "guest";
    private static bool IsDraftOp(string op) => op is "draftCreate" or "draftList" or "draftUpdate" or "draftDiscard" or "draftCopy" or "draftKeep" or "draftPlay";
    private void DraftsChanged() { Interlocked.Increment(ref draftsRevision); Publish(); }
    private string Tag(string owner) => Convert.ToHexString(HMACSHA256.HashData(draftTagKey, Encoding.UTF8.GetBytes(owner)))[..8].ToLowerInvariant();
    private static DraftView View(Draft d) => new(d.Id, d.Start, d.End, d.Name, d.Created, d.Updated, d.SessionId, d.ClipAssetId != null && d.ClipRange == new HistoryRange(d.Start, d.End));

    // ---- Own replay ------------------------------------------------------------------------------------------

    /// <summary>A play reached Soundpad (StartPlayback): what follows in the history is the companion's own replay.</summary>
    private void NoteOwnReplay(string? soundId, long? lengthMs)
    {
        var h = history;
        if (h == null || !mimicCapture.Running) return;
        try
        {
            long length = lengthMs ?? parsed.Public.Sounds.FirstOrDefault(s => s.Id == soundId)?.DurationMs ?? 30000;
            ownReplays.Started(h.SessionId, h.LiveFrame, Math.Clamp(length, 0, 600000) * h.Rate / 1000, h.Rate / 2, soundId);
        }
        catch (HistoryException) { }
    }
    private void NoteOwnStop()
    {
        var h = history;
        if (h == null) return;
        try { ownReplays.Stopped(h.SessionId, h.LiveFrame, h.Rate / 4); } catch (HistoryException) { }
    }

    // ---- Drafts ------------------------------------------------------------------------------------------------

    /// <summary>Every draft goes (a history Discard, quitting): their pins go with the history they pinned.</summary>
    private void ForgetDrafts(HistoryStore? pinnedIn)
    {
        Draft[] gone;
        lock (drafts) { gone = [.. drafts.Values]; drafts.Clear(); }
        if (pinnedIn != null) foreach (var d in gone) { try { pinnedIn.Unpin(d.PinKey); } catch (Exception ex) when (ex is HistoryException or InvalidOperationException) { } }
        if (gone.Length > 0) Interlocked.Increment(ref draftsRevision);
    }

    /// <summary>Lets go of the drafts <paramref name="which"/> picks, and their pins. True when any went.</summary>
    private bool ReleaseDrafts(Func<Draft, bool> which)
    {
        var h = history; Draft[] gone;
        lock (drafts) { gone = [.. drafts.Values.Where(which)]; foreach (var d in gone) drafts.Remove(d.Id); }
        if (gone.Length == 0) return false;
        if (h != null) foreach (var d in gone) { try { h.Unpin(d.PinKey); } catch (Exception ex) when (ex is HistoryException or InvalidOperationException) { } }
        DraftsChanged();
        return true;
    }

    /// <summary>The host disconnected or turned this guest away (2.40): their drafts go with them.</summary>
    private void ReleaseDraftsOf(string participantId)
    {
        lock (drafts) { guestsAway.Remove(participantId); guestsHere.Remove(participantId); }
        ReleaseDrafts(d => d.Owner == participantId);
    }

    /// <summary>A new invitation, End hosting or quitting (2.40): every guest's drafts go; the host's stay.</summary>
    private void ReleaseGuestDrafts()
    {
        lock (drafts) { guestsHere = new(StringComparer.Ordinal); guestsAway.Clear(); }
        ReleaseDrafts(d => d.Owner != "host");
    }

    /// <summary>
    /// Who is in the session now (2.40): the participant ids of the owner page's session summary. A guest who is no
    /// longer listed keeps their drafts through a reload or a short drop, and loses them once they have been away
    /// <see cref="AwayGuestDraftMinutes"/>, so drafts nobody will come back for do not hold the recording history.
    /// </summary>
    public void NotePresent(IEnumerable<string> participantIds)
    {
        long now = utcNow();
        var here = new HashSet<string>(participantIds.Where(id => id is { Length: > 0 and <= 128 }).Take(256), StringComparer.Ordinal);
        lock (drafts)
        {
            foreach (var id in guestsHere) if (!here.Contains(id) && guestsAway.Count < MaxGuestJoins) guestsAway.TryAdd(id, now);
            foreach (var id in here) guestsAway.Remove(id);
            guestsHere = here;
        }
        ReleaseAwayGuests();
    }
    private void ReleaseAwayGuests()
    {
        long now = utcNow(); HashSet<string> gone;
        lock (drafts)
        {
            if (guestsAway.Count == 0) return;
            gone = new(guestsAway.Where(g => now - g.Value >= AwayGuestDraftMinutes * 60000L).Select(g => g.Key), StringComparer.Ordinal);
            foreach (var id in gone) guestsAway.Remove(id);
        }
        if (gone.Count > 0 && ReleaseDrafts(d => gone.Contains(d.Owner))) Log("Let go of the drafts of guests who left a while ago");
    }

    /// <summary>
    /// Room for a guest's draft over [start, end) (2.40), counted in the whole segments pins keep: at most
    /// <see cref="GuestPinPercent"/> of the pin budget for one guest and <see cref="GuestsPinPercent"/> for all guests
    /// together, so the rest stays for the host. The caller holds <see cref="drafts"/>.
    /// </summary>
    private void RequireGuestPinRoom(HistoryStore h, string owner, string? replacing, long start, long end)
    {
        if (owner == "host") return;
        long segment = Math.Max(1, h.SegmentFrames);
        HashSet<long> theirs = [], guests = [];
        void Add(HashSet<long> set, long a, long z) { for (long i = a / segment; i <= (z - 1) / segment; i++) set.Add(i); }
        foreach (var d in drafts.Values)
        {
            if (d.Owner == "host" || d.Id == replacing || d.SessionId != h.SessionId) continue;
            Add(guests, d.Start, d.End);
            if (d.Owner == owner) Add(theirs, d.Start, d.End);
        }
        Add(theirs, start, end); Add(guests, start, end);
        long bytes = segment * 2;
        if (theirs.Count * bytes > h.PinBudgetBytes * GuestPinPercent / 100)
            throw new InvalidDataException("Your drafts already keep as much recording history as a guest may. Keep or discard one of your drafts first.");
        if (guests.Count * bytes > h.PinBudgetBytes * GuestsPinPercent / 100)
            throw new InvalidDataException("Guests' drafts already keep as much recording history as guests may. Keep or discard one of your drafts first, or ask the host.");
    }

    /// <summary>
    /// The name of a draft as <paramref name="access"/> may see it (2.40): a name its owner typed as it is; one made from
    /// the transcript only when the reader may see all the audio it was made from, otherwise one made from the words of
    /// <paramref name="part"/> the reader may see (or none). Also the range such a name comes from.
    /// </summary>
    private (string? Name, bool Typed, HistoryRange From) NameFor(HistoryStore h, HistoryAccess access, Draft d, HistoryRange part)
    {
        if (d.NameTyped) return (d.Name, true, default);
        if (d.Name != null && access.Allows(d.NameRange.StartFrame, d.NameRange.EndFrame)) return (d.Name, false, d.NameRange);
        return (WordsOf(h, access, part.StartFrame, part.EndFrame), false, part);
    }

    private Draft OwnDraft(Command c, string owner)
    {
        lock (drafts)
            return c.DraftId is { } id && drafts.TryGetValue(id, out var d) && d.Owner == owner ? d : throw new InvalidDataException("That draft is gone. It may have been kept or discarded.");
    }

    private static void RequireDraftLength(HistoryStore h, long start, long end)
    {
        if (start < 0 || (end - start) * 1000 < (long)h.Rate * MimicMinimumMs || (end - start) * 1000 > (long)h.Rate * PreviewMaxMs)
            throw new InvalidDataException($"A draft holds between {MimicMinimumMs} ms and {PreviewMaxMs / 1000} seconds of audio.");
    }

    /// <summary>What part of [start, end) <paramref name="access"/> may see: the largest visible piece, or null.</summary>
    private static HistoryRange? Visible(HistoryAccess access, long start, long end)
    {
        if (access.Allows(start, end)) return new(start, end);
        HistoryRange? best = null;
        foreach (var r in access.Allowed)
        {
            long a = Math.Max(start, r.StartFrame), z = Math.Min(end, r.EndFrame);
            if (a < z && (best == null || z - a > best.Value.Frames)) best = new(a, z);
        }
        return best;
    }

    private Draft NewDraft(HistoryStore h, HistoryAccess access, string owner, string ownerName, long start, long end, string? name, bool typed, HistoryRange nameFrom)
    {
        RequireDraftLength(h, start, end);
        long now = utcNow();
        var clean = CleanName(name);
        var draft = new Draft { Id = Guid.NewGuid().ToString("N"), Owner = owner, OwnerName = ownerName, SessionId = h.SessionId, Start = start, End = end, Name = clean, NameTyped = typed && clean != null, NameRange = nameFrom, Created = now, Updated = now };
        lock (drafts)
        {
            if (drafts.Values.Count(d => d.Owner == owner) >= MaxDraftsPerPerson) throw new InvalidDataException($"You have {MaxDraftsPerPerson} drafts. Keep or discard some first.");
            RequireGuestPinRoom(h, owner, null, start, end);
            // Pinned before anyone can see it: a draft never shows audio that expiry could still take.
            h.Pin(access, draft.PinKey, start, end);
            drafts[draft.Id] = draft;
        }
        return draft;
    }
    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > 100 ? clean[..100] : clean;
    }

    /// <summary>The words of [start, end) that <paramref name="access"/> may see, as a name.</summary>
    private string? WordsOf(HistoryStore h, HistoryAccess access, long start, long end)
    {
        var words = string.Join(" ", transcriber.Lines(h, access, start, end).Select(l => l.Text.Trim())).Trim();
        return words.Length > 0 ? PlainWords(words) : null;
    }

    /// <summary>The 2.40 draft commands. Null when the op is not one of them.</summary>
    private async Task<Reply?> ExecuteDrafts(Command c, string actor, Participant? participant, string? requester, string? audience, Intent? intent)
    {
        if (!IsDraftOp(c.Op)) return null;
        bool host = actor == "host";
        string owner = PersonKey(actor, participant, requester);
        var person = Person(actor, participant);
        var h = CurrentHistory() ?? throw new InvalidDataException("Mimic has not recorded anything yet.");
        if (c.SourceId != null && c.SourceId != h.SessionId) throw new InvalidDataException("Mimic restarted since you looked. Look again.");
        var access = AccessFor(actor, participant, requester, h);
        // A guest who sends a draft command is here, whatever the last session summary said.
        if (!host) lock (drafts) { guestsAway.Remove(owner); guestsHere.Add(owner); }
        ReleaseAwayGuests();
        switch (c.Op)
        {
            case "draftList":
                {
                    Draft[] all; lock (drafts) all = [.. drafts.Values.Where(d => d.SessionId == h.SessionId)];
                    var mine = all.Where(d => d.Owner == owner).OrderByDescending(d => d.Updated).Select(View).ToArray();
                    var others = new List<DraftBand>();
                    foreach (var d in all.Where(d => d.Owner != owner).OrderByDescending(d => d.Updated))
                        // A guest sees other people's selections only where they may see the history itself, and
                        // their names only from words they may see.
                        if (Visible(access, d.Start, d.End) is { } part) others.Add(new(d.Id, d.OwnerName, Tag(d.Owner), part.StartFrame, part.EndFrame, NameFor(h, access, d, part).Name));
                    return new("ack", c.Id, true, Data: new { drafts = mine, others, sessionId = h.SessionId });
                }
            case "draftCreate":
                {
                    HistoryRange range;
                    if (c.Action == "phrase")
                    {
                        if (!transcriber.Enabled) throw new InvalidDataException("Latest phrase needs subtitles. Ask the host to turn on transcripts, or use Last 10 seconds.");
                        long live = h.LiveFrame;
                        var words = transcriber.WordsBetween(h, access, Math.Max(0, live - (long)QuickMimic.SearchSeconds * h.Rate), live).Select(w => w.Word);
                        var found = QuickMimic.LatestPhrase(words, live, h.Rate, (a, z) => ownReplays.Covers(h.SessionId, a, z), (long)PreviewMaxMs * h.Rate / 1000)
                            ?? throw new InvalidDataException("No finished phrase yet. Try again in a moment, or use Last 10 seconds.");
                        // Padding never reaches into audio this person may not see.
                        range = Visible(access, found.StartFrame, found.EndFrame) ?? throw new InvalidDataException("No finished phrase yet. Try again in a moment, or use Last 10 seconds.");
                    }
                    else if (c.Action == "recent")
                        range = QuickMimic.Recent(h.Timeline(access).Available, h.LiveFrame, h.Rate) ?? throw new InvalidDataException("Nothing has been recorded since you joined yet.");
                    else
                    {
                        var (_, s, e) = Selection(c);
                        range = new(s, e);
                    }
                    // A typed name, or the words of the range this person may see (2.40: the page sends only a typed one).
                    string? typed = CleanName(c.Name);
                    string? name = typed ?? WordsOf(h, access, range.StartFrame, range.EndFrame);
                    var draft = NewDraft(h, access, owner, person.Name, range.StartFrame, range.EndFrame, name, typed != null, range);
                    Log($"{person.Name} captured a draft ({range.Frames / (double)h.Rate:0.0} s)");
                    DraftsChanged();
                    return new("ack", c.Id, true, Data: View(draft));
                }
            case "draftCopy":
                {
                    Draft source;
                    lock (drafts) source = c.DraftId is { } id && drafts.TryGetValue(id, out var d) && d.SessionId == h.SessionId ? d : throw new InvalidDataException("That selection is gone.");
                    var part = Visible(access, source.Start, source.End) ?? throw new InvalidDataException("That selection is from before you joined. Ask the host to share it.");
                    // Named from what the copier may see, never from words of the original they may not.
                    var (name, typed, from) = NameFor(h, access, source, part);
                    var copy = NewDraft(h, access, owner, person.Name, part.StartFrame, part.EndFrame, name, typed, from);
                    DraftsChanged();
                    return new("ack", c.Id, true, Data: View(copy));
                }
            case "draftUpdate":
                {
                    var draft = OwnDraft(c, owner);
                    long start = c.StartFrame ?? draft.Start, end = c.EndFrame ?? draft.End;
                    if (c.EndFrame == null && c.StartFrame != null || c.StartFrame == null && c.EndFrame != null) throw new InvalidDataException("Send both edges of the draft.");
                    RequireDraftLength(h, start, end);
                    lock (drafts)
                    {
                        if (!drafts.ContainsKey(draft.Id)) throw new InvalidDataException("That draft is gone. It may have been kept or discarded.");
                        // Moved or extended into the surrounding history: the new range is pinned before the old one is let go.
                        if (start != draft.Start || end != draft.End)
                        {
                            RequireGuestPinRoom(h, owner, draft.Id, start, end);
                            h.ReplacePin(access, draft.PinKey, start, end);
                        }
                        draft.Start = start; draft.End = end;
                        if (c.Name != null) { draft.Name = CleanName(c.Name); draft.NameTyped = draft.Name != null; }
                        draft.Updated = utcNow();
                    }
                    DraftsChanged();
                    return new("ack", c.Id, true, Data: View(draft));
                }
            case "draftDiscard":
                {
                    // The host may let go of anyone's draft (2.40), such as one a guest left behind; a guest only their own.
                    Draft draft;
                    lock (drafts) draft = c.DraftId is { } id && drafts.TryGetValue(id, out var d) && (d.Owner == owner || host) ? d : throw new InvalidDataException("That draft is gone. It may have been kept or discarded.");
                    bool removed; lock (drafts) removed = drafts.Remove(draft.Id);
                    if (removed) h.Unpin(draft.PinKey);
                    if (removed && draft.Owner != owner) Log($"The host discarded {draft.OwnerName}'s draft");
                    DraftsChanged();
                    return new("ack", c.Id, true);
                }
            case "draftKeep":
            case "draftPlay":
                {
                    var draft = OwnDraft(c, owner);
                    var range = new HistoryRange(draft.Start, draft.End);
                    Reply saved;
                    // The clip made from this draft before, while its range is unchanged and it is still in Temp: kept, or played again.
                    var existing = draft.ClipAssetId is { } clip && draft.ClipRange == range ? Audio.Public.Assets.FirstOrDefault(a => a.Id == clip && a.HasTemporary) : null;
                    if (existing != null) saved = new("ack", c.Id, true, Data: new { assetId = existing.Id, state = existing.State });
                    else
                    {
                        saved = (await SaveMimic(h, access, draft.Start, draft.End, draft.Name ?? WordsOf(h, access, draft.Start, draft.End), person.Name, host, requester ?? participant?.Id ?? actor, audience, owner)) with { Id = c.Id };
                        if (saved.Ok && Field(saved, "assetId") is string assetId) lock (drafts) { draft.ClipAssetId = assetId; draft.ClipRange = range; }
                    }
                    if (!saved.Ok) return saved;
                    // "Keep in voice library" (2.41): kept permanently at once, as a library recording with this stretch of the
                    // history as its provenance and its words copied from the transcript. A refusal leaves the clip in Temp
                    // and the draft open.
                    bool library = c.Op == "draftKeep" && c.Action == "library";
                    string? recordingId = null;
                    if (library && Field(saved, "assetId") is string keptId)
                    {
                        var source = new LibrarySource { Kind = "history", SessionId = h.SessionId, StartFrame = draft.Start, EndFrame = draft.End, Rate = h.Rate };
                        try { recordingId = (await KeepIntoLibrary(keptId, actor, participant, requester, audience, source, WordsFromHistory(source)))?.Id; }
                        catch (InvalidDataException ex) { DraftsChanged(); return new("ack", c.Id, false, ex.Message + " The clip is in Temp."); }
                    }
                    bool close = c.Op == "draftKeep" && c.Action is "close" or "library";
                    if (close) { lock (drafts) drafts.Remove(draft.Id); h.Unpin(draft.PinKey); }
                    DraftsChanged();
                    var result = c.Op == "draftPlay" ? await Autoplay(saved, c with { Autoplay = true }, actor, participant, requester, audience, intent) : saved;
                    var merged = Merge(result.Data, draft.Id, close);
                    return result with { Id = c.Id, Data = library ? new { assetId = Field(saved, "assetId"), state = "permanent", played = false, draftId = draft.Id, closed = close, recordingId } : merged };
                }
        }
        return null;
    }
    private static object Merge(object? data, string draftId, bool closed)
    {
        var assetId = data?.GetType().GetProperty("assetId")?.GetValue(data) as string;
        var state = data?.GetType().GetProperty("state")?.GetValue(data) as string;
        var played = data?.GetType().GetProperty("played")?.GetValue(data) as bool?;
        var epoch = data?.GetType().GetProperty("epoch")?.GetValue(data) as string;
        return new { assetId, state, played = played ?? false, epoch, draftId, closed };
    }

    // ---- Private preview -------------------------------------------------------------------------------------------

    /// <summary>
    /// Private preview (host 2.12; guests 2.40): a selected range or one's own draft, scoped by what the person may
    /// see, at most <see cref="PreviewMaxMs"/>, as a small WAV answered to that person only (the host over loopback, a
    /// guest through the host page's reply to them). It never reaches Soundpad or anyone else, and the website offers
    /// no download of it. Guests are limited per minute and to one at a time.
    /// </summary>
    private async Task<Reply> Preview(Command c, string actor, Participant? participant, string? requester)
    {
        bool host = actor == "host";
        string key = PersonKey(actor, participant, requester);
        HistoryStore h; long start, stop;
        if (c.DraftId != null)
        {
            h = CurrentHistory() ?? throw new InvalidDataException("Mimic has not recorded anything yet.");
            // 2.41: like a selection, a draft is heard only on the recording it was made on.
            if (c.SourceId != null && c.SourceId != h.SessionId) throw new InvalidDataException("Mimic restarted since you looked. Look again.");
            var draft = OwnDraft(c, key);
            if (draft.SessionId != h.SessionId) throw new InvalidDataException("That draft is from a recording that is gone.");
            (start, stop) = (draft.Start, draft.End);
        }
        else (h, start, stop) = Selection(c);
        if (stop - start < (long)h.Rate * MimicMinimumMs / 1000) throw new InvalidDataException("Mimic had not started listening yet at that moment.");
        var access = AccessFor(actor, participant, requester, h);
        // Reading and encoding run on the audio worker (2.41).
        if (host) return new("ack", c.Id, true, Data: new { wav = Convert.ToBase64String(await Heavy("mimicPreview", () => MimicRing.Wav(h.Read(access, start, (int)(stop - start)), h.Rate))) });
        if (!previewing.TryAdd(key, 0)) throw new InvalidDataException("Your last preview is still being made.");
        try
        {
            if (!previewRate.TryTake(key)) throw new InvalidDataException("Too many previews. Wait a minute and try again.");
            var wav = await Heavy("mimicPreview", () =>
            {
                var samples = h.Read(access, start, (int)(stop - start));
                var small = h.Rate > GuestPreviewRate ? MimicTranscriber.Resample(samples, h.Rate, GuestPreviewRate) : samples;
                return MimicRing.Wav(small, Math.Min(h.Rate, GuestPreviewRate));
            });
            return new("ack", c.Id, true, Data: new { wav = Convert.ToBase64String(wav) });
        }
        finally { previewing.TryRemove(key, out _); }
    }
}
