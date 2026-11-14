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

    private Draft NewDraft(HistoryStore h, HistoryAccess access, string owner, string ownerName, long start, long end, string? name)
    {
        RequireDraftLength(h, start, end);
        long now = utcNow();
        var draft = new Draft { Id = Guid.NewGuid().ToString("N"), Owner = owner, OwnerName = ownerName, SessionId = h.SessionId, Start = start, End = end, Name = CleanName(name), Created = now, Updated = now };
        lock (drafts)
        {
            if (drafts.Values.Count(d => d.Owner == owner) >= MaxDraftsPerPerson) throw new InvalidDataException($"You have {MaxDraftsPerPerson} drafts. Keep or discard some first.");
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
        switch (c.Op)
        {
            case "draftList":
                {
                    Draft[] all; lock (drafts) all = [.. drafts.Values.Where(d => d.SessionId == h.SessionId)];
                    var mine = all.Where(d => d.Owner == owner).OrderByDescending(d => d.Updated).Select(View).ToArray();
                    var others = new List<DraftBand>();
                    foreach (var d in all.Where(d => d.Owner != owner).OrderByDescending(d => d.Updated))
                        // A guest sees other people's selections only where they may see the history itself.
                        if (Visible(access, d.Start, d.End) is { } part) others.Add(new(d.Id, d.OwnerName, Tag(d.Owner), part.StartFrame, part.EndFrame, d.Name));
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
                    string? name = CleanName(c.Name) ?? WordsOf(h, access, range.StartFrame, range.EndFrame);
                    var draft = NewDraft(h, access, owner, person.Name, range.StartFrame, range.EndFrame, name);
                    Log($"{person.Name} captured a draft ({range.Frames / (double)h.Rate:0.0} s)");
                    DraftsChanged();
                    return new("ack", c.Id, true, Data: View(draft));
                }
            case "draftCopy":
                {
                    Draft source;
                    lock (drafts) source = c.DraftId is { } id && drafts.TryGetValue(id, out var d) && d.SessionId == h.SessionId ? d : throw new InvalidDataException("That selection is gone.");
                    var part = Visible(access, source.Start, source.End) ?? throw new InvalidDataException("That selection is from before you joined. Ask the host to share it.");
                    var copy = NewDraft(h, access, owner, person.Name, part.StartFrame, part.EndFrame, source.Name);
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
                        if (start != draft.Start || end != draft.End) h.ReplacePin(access, draft.PinKey, start, end);
                        draft.Start = start; draft.End = end;
                        if (c.Name != null) draft.Name = CleanName(c.Name);
                        draft.Updated = utcNow();
                    }
                    DraftsChanged();
                    return new("ack", c.Id, true, Data: View(draft));
                }
            case "draftDiscard":
                {
                    var draft = OwnDraft(c, owner);
                    lock (drafts) drafts.Remove(draft.Id);
                    h.Unpin(draft.PinKey);
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
                    bool close = c.Op == "draftKeep" && c.Action == "close";
                    if (close) { lock (drafts) drafts.Remove(draft.Id); h.Unpin(draft.PinKey); }
                    DraftsChanged();
                    var result = c.Op == "draftPlay" ? await Autoplay(saved, c with { Autoplay = true }, actor, participant, requester, audience, intent) : saved;
                    return result with { Id = c.Id, Data = Merge(result.Data, draft.Id, close) };
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
    private Reply Preview(Command c, string actor, Participant? participant, string? requester)
    {
        bool host = actor == "host";
        string key = PersonKey(actor, participant, requester);
        HistoryStore h; long start, stop;
        if (c.DraftId != null)
        {
            h = CurrentHistory() ?? throw new InvalidDataException("Mimic has not recorded anything yet.");
            var draft = OwnDraft(c, key);
            (start, stop) = (draft.Start, draft.End);
        }
        else (h, start, stop) = Selection(c);
        if (stop - start < (long)h.Rate * MimicMinimumMs / 1000) throw new InvalidDataException("Mimic had not started listening yet at that moment.");
        var access = AccessFor(actor, participant, requester, h);
        if (host) return new("ack", c.Id, true, Data: new { wav = Convert.ToBase64String(MimicRing.Wav(h.Read(access, start, (int)(stop - start)), h.Rate)) });
        if (!previewing.TryAdd(key, 0)) throw new InvalidDataException("Your last preview is still being made.");
        try
        {
            if (!previewRate.TryTake(key)) throw new InvalidDataException("Too many previews. Wait a minute and try again.");
            var samples = h.Read(access, start, (int)(stop - start));
            var small = h.Rate > GuestPreviewRate ? MimicTranscriber.Resample(samples, h.Rate, GuestPreviewRate) : samples;
            return new("ack", c.Id, true, Data: new { wav = Convert.ToBase64String(MimicRing.Wav(small, Math.Min(h.Rate, GuestPreviewRate))) });
        }
        finally { previewing.TryRemove(key, out _); }
    }
}
