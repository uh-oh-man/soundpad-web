using System.Collections.Concurrent;
using System.Security.Cryptography;
namespace SoundpadWeb;

// Sentences, the speaking palette, recorded performances and performance sets (2.42). See SentenceModel.cs,
// SentenceComposer.cs and SentenceRender.cs. Projects are the companion's (like drafts): every change is a command,
// so the matching runs where the library is. Play into game renders the revision it was asked at into one of the
// exact-playback slot files and plays it through the newest-intent arbitration; Preview is private to the asker.
public sealed partial class Bridge
{
    public const int MaxSentencesPerPerson = 200;
    /// <summary>A recorded performance: at most this long, and this many taps.</summary>
    public const int MaxPerformanceMs = 600000, MaxPerformanceTaps = 400;
    /// <summary>Palette taps in Preview mode per minute, per guest (a performance taps quickly).</summary>
    public const int GuestPaletteTapsPerMinute = 90;
    private SentenceStore? sentenceStore;
    private readonly object sentenceInit = new();
    private long sentencesRevision;
    private volatile bool slotsForSentences;
    private RateWindow paletteRate = new(GuestPaletteTapsPerMinute, TimeSpan.FromMinutes(1));
    /// <summary>
    /// Changes to the sentences file per minute, per guest (review, 2.42): every change rewrites the whole file, like the
    /// voice library's (2.41). Room for typing, which saves at most about three times a second.
    /// </summary>
    public const int GuestSentenceWritesPerMinute = 240;
    private RateWindow sentenceWriteRate = new(GuestSentenceWritesPerMinute, TimeSpan.FromMinutes(1));
    /// <summary>Test seam: another limit for guests' sentence changes.</summary>
    internal void UseSentenceWriteLimit(RateWindow limit) => sentenceWriteRate = limit;
    /// <summary>Room in the shared lists that guests' changes leave for the host's own (review, 2.42).</summary>
    public const int HostReserveSentences = 400, HostReserveNamed = 1000;
    /// <summary>A guest's sentence nobody kept is removed once untouched this long (review, 2.42). Since 2.45 the host also sees and can clear them.</summary>
    public const long GuestUnkeptMs = 14L * 24 * 3600 * 1000;
    /// <summary>One sentence or palette render for the game at a time (review, 2.42), like exact slices' render slot.</summary>
    private readonly SemaphoreSlim sentenceRenderSlot = new(1, 1);
    /// <summary>Guests whose sentence Keep is under way: one at a time each.</summary>
    private readonly ConcurrentDictionary<string, byte> keepingSentence = new(StringComparer.Ordinal);
    /// <summary>Slot registration failed for a reason other than a missing quiet moment: no new try (and no wait) before this.</summary>
    private long slotsFailedUntil;
    private readonly ConcurrentDictionary<string, (long Length, long Modified, string Hash)> fileHashes = new(StringComparer.OrdinalIgnoreCase);

    private SentenceStore SentenceData
    {
        get
        {
            lock (sentenceInit) return sentenceStore ??= new SentenceStore(store.DirectoryPath, () => utcNow());
        }
    }
    /// <summary>Test seam: the sentence store.</summary>
    public SentenceStore SentenceStoreForTests => SentenceData;
    /// <summary>Test seam: awaited after a Play or Keep took its snapshot and before it renders (to edit meanwhile).</summary>
    internal Func<Task>? SentenceRenderHold { get; set; }
    private void SentencesChanged() { Interlocked.Increment(ref sentencesRevision); Publish(); }

    private static bool IsSentenceOp(string op) => op.StartsWith("sentence", StringComparison.Ordinal) || op.StartsWith("palette", StringComparison.Ordinal) || op is "performance" or "performanceSets";

    // ---- The library as sentence material -------------------------------------------------------------------------

    private static bool AlteredTake(LibraryRecording r) => r.Source.Kind == "copy";

    /// <summary>The voice library's recorded words and the named pieces, as the composer sees them.</summary>
    private sealed class LibraryCorpus(VoiceLibrary library, WordIndex index, SentenceStore sentences) : ISentenceCorpus
    {
        private readonly Dictionary<string, LibraryRecording?> recordings = new(StringComparer.Ordinal);
        private LibraryRecording? Get(string id) { if (!recordings.TryGetValue(id, out var r)) recordings[id] = r = library.Get(id); return r; }
        private bool Allowed(string key, TakeFilter filter) => VoiceLibrary.IsRecordingKey(key) && Get(VoiceLibrary.RecordingOf(key)) is { } r && filter.Allows(r.VoiceId, AlteredTake(r));

        public IReadOnlyList<CorpusTake> Find(string[] tokens, TakeFilter filter, int max)
        {
            var result = new List<CorpusTake>();
            if (tokens.Length == 0 || tokens.Length > WordIndex.MaxQueryTokens) return result;
            foreach (var (key, position) in index.Find(tokens, key => Allowed(key, filter), max))
            {
                var r = Get(VoiceLibrary.RecordingOf(key)); if (r == null) continue;
                var words = library.DisplayWords(r.Id);
                int last = position + tokens.Length - 1;
                if (last >= words.Length) continue;
                long start = words[position].StartMs, end = words[last].EndMs;
                long gap = last + 1 < words.Length ? Math.Max(0, words[last + 1].StartMs - end) : 250;
                int uncertain = 0; for (int i = position; i <= last; i++) if (words[i].Uncertain) uncertain++;
                double quality = 1 - 0.5 * uncertain / tokens.Length + (r.Favorite ? 0.1 : 0) + (r.Reviewed ? 0.05 : 0);
                result.Add(new(r.Id, r.VoiceId, position, tokens.Length, start, end, gap, string.Join(' ', words.Skip(position).Take(tokens.Length).Select(w => w.Text)), quality, AlteredTake(r), r.Created));
            }
            return result;
        }

        public IReadOnlyList<CorpusNamed> Named(string name, TakeFilter filter) =>
            [.. sentences.Read(f => f.Named.Where(n => n.Name == name).Select(n => (n.Id, n.Name, n.RecordingId, n.StartMs, n.EndMs)).ToList())
                .Select(n => (n, r: Get(n.RecordingId))).Where(x => x.r != null && filter.Allows(x.r.VoiceId, AlteredTake(x.r)))
                .Select(x => new CorpusNamed(x.n.Id, x.n.Name, x.n.RecordingId, x.r!.VoiceId, x.n.StartMs, x.n.EndMs, AlteredTake(x.r)))];

        public IReadOnlyDictionary<string, int> Vocabulary(TakeFilter filter) => index.Vocabulary(key => Allowed(key, filter));
    }
    private LibraryCorpus Corpus() => new(Voices, wordIndex, SentenceData);

    // ---- Commands ------------------------------------------------------------------------------------------------

    private async Task<Reply> ExecuteSentences(Command c, string actor, Participant? participant, string? requester, string? audience, Intent? intent)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && (audioAudience != null && (!sessionAccepting || audience != audioAudience) || requester != null && revokedAudioPeers.ContainsKey(requester))) throw new InvalidDataException("This guest session authorization changed.");
            var person = Person(actor, participant); string key = PersonKey(actor, participant, requester);
            // Sentence commands run off the socket's receive loop (Offloaded), so waiting here for the library is fine.
            bool canBuild = host || GuestsMayBuild();
            var input = c.Sentence ?? new SentenceInput();
            // Every change rewrites the sentences file: a guest's pace is limited (review, 2.42).
            bool writes = c.Op is "sentenceCreate" or "sentenceText" or "sentenceEdit" or "sentenceDiscard" or "sentenceKeep" or "paletteMark"
                || c.Op == "performance" && c.Action == "stop" || c.Op == "performanceSets" && c.Action is "create" or "update" or "delete" or "activate";
            if (!host && writes && !sentenceWriteRate.TryTake(key)) throw new InvalidDataException("Too many changes to sentences. Wait a moment and try again.");
            switch (c.Op)
            {
                case "sentenceList": return Ok(c, ListSentences(key, host, canBuild));
                case "sentenceCreate": return Ok(c, CreateSentence(c, input, key, person, canBuild));
                case "sentenceGet": return Ok(c, SentenceView(c.SentenceId, key, host, canBuild));
                case "sentenceText": return Ok(c, ChangeSentence(c.SentenceId, key, host, canBuild, s => SentenceComposer.Rematch(s, Text(input.Text), Corpus())));
                case "sentenceEdit": return Ok(c, EditSentence(c, input, key, host, canBuild));
                case "sentenceAlternatives": return Ok(c, SentenceAlternatives(c.SentenceId, c.Sentence?.Pieces?.FirstOrDefault(), key, host, canBuild));
                case "sentenceDiscard": return Ok(c, c.SentenceId == null && c.GuestId != null ? ClearGuestUnkept(c.GuestId, host) : DiscardSentence(c.SentenceId, key, host));
                case "sentencePreview": return Ok(c, await PreviewSentence(c, input, actor, participant, requester, key, host, canBuild));
                case "sentenceKeep": return Ok(c, await KeepSentence(c.SentenceId, key, host, canBuild, person));
                case "sentencePlay": return await PlaySentence(c, input, actor, participant, requester, audience, intent, key, host);
                case "paletteSearch": return Ok(c, await Task.Run(() => PaletteSearch(c, input, key)));
                case "paletteTap": return await PaletteTap(c, input, actor, participant, requester, audience, intent, key, host);
                case "paletteMark": return Ok(c, MarkPiece(c, input, key, person, canBuild));
                case "performance": return Ok(c, PerformanceCommand(c, key, person, canBuild));
                case "performanceSets": return Ok(c, PerformanceSets(c, key));
            }
            throw new InvalidDataException("Unsupported command.");
        }
        catch (OperationCanceledException) when (intent?.Withdrawn.IsCancellationRequested == true) { return Superseded(c, "newer"); }
        catch (SentenceRender.MissingSourceException ex) { return new("ack", c.Id, false, ex.Message, new { pieceId = ex.PieceId }); }
        catch (SliceRangeException ex) { return new("ack", c.Id, false, ex.Message); }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new("ack", c.Id, false, "The sentences could not read or save their files: " + ex.Message); }
    }

    /// <summary>
    /// Whether the host lets guests build sentences (the library's edit setting). Until the library has loaded this
    /// loads it (review, 2.42: it used to fail as a crash); a library that cannot load is answered clearly.
    /// </summary>
    private bool GuestsMayBuild()
    {
        if (LibrarySettingsNow is { } settings) return settings.GuestLibraryEdit;
        try { return Voices.Settings.GuestLibraryEdit; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException("The voice library is still loading. Try again in a moment.");
        }
    }

    private static string Text(string? text)
    {
        text ??= "";
        if (text.Length > SentenceStore.MaxText) throw new InvalidDataException($"A sentence is at most {SentenceStore.MaxText} characters.");
        return new string([.. text.Select(ch => char.IsControl(ch) ? ' ' : ch)]);
    }

    private static SentenceProject FindSentence(SentenceFile f, string? id) => f.Sentences.FirstOrDefault(s => s.Id == id) ?? throw new InvalidDataException("That sentence is no longer there.");
    /// <summary>Who may open a sentence: its maker, everyone once it is kept, and (2.45, D10) the host, who also sees guests' unkept ones.</summary>
    private static bool CanSee(SentenceProject s, string key, bool host) => s.Owner == key || s.Kept || host;
    /// <summary>What the list shows a person: their own and the kept ones. Guests' unkept sentences are listed to the host apart (guestUnkept).</summary>
    private static bool Listed(SentenceProject s, string key) => s.Owner == key || s.Kept;
    /// <summary>Its maker may change it (while the host lets guests build); the host may change any kept sentence.</summary>
    private static bool CanChange(SentenceProject s, string key, bool host, bool canBuild) => s.Owner == key && (host || canBuild) || host && s.Kept;
    /// <summary>
    /// What a viewer sees of a sentence it may see (review, 2.42): its maker, and whoever may change it, see the live
    /// revision; everyone else sees the latest kept version, so edits nobody kept yet stay private, as with Play.
    /// </summary>
    private static SentenceProject Visible(SentenceProject s, string key, bool host, bool canBuild) =>
        s.Owner == key || CanChange(s, key, host, canBuild) || s.Versions.Count == 0 ? s : new SentenceProject
        {
            Id = s.Id, Owner = s.Owner, OwnerName = s.OwnerName, Title = s.Title, Created = s.Created, Updated = s.Versions[^1].At, Revision = s.Revision,
            Origin = s.Origin, Pinned = s.Pinned, Versions = s.Versions, Text = s.Versions[^1].Text, Pieces = s.Versions[^1].Pieces, Settings = s.Versions[^1].Settings,
        };
    /// <summary>A guest's unkept sentences untouched for <see cref="GuestUnkeptMs"/> go: the host never sees them to discard them.</summary>
    private void SweepGuestDrafts(SentenceFile f)
    {
        long cutoff = utcNow() - GuestUnkeptMs;
        f.Sentences.RemoveAll(s => !s.Kept && s.Owner != "host" && s.Updated < cutoff);
    }
    /// <summary>Room for one more unkept sentence of this person; guests leave <see cref="HostReserveSentences"/> for the host.</summary>
    private static void RequireRoom(SentenceFile f, string key, bool host)
    {
        if (f.Sentences.Count(s => s.Owner == key && !s.Kept) >= MaxSentencesPerPerson) throw new InvalidDataException($"You have {MaxSentencesPerPerson} unkept sentences. Keep or discard some first.");
        if (f.Sentences.Count >= SentenceStore.MaxSentences - (host ? 0 : HostReserveSentences)) throw new InvalidDataException("The sentence list is full.");
    }

    private object ListSentences(string key, bool host, bool canBuild) => SentenceData.Read(f => new
    {
        revision = Interlocked.Read(ref sentencesRevision),
        canBuild,
        canKeep = host || Keep.GuestKeep,
        problem = SentenceData.Problem,
        sentences = f.Sentences.Where(s => Listed(s, key)).Select(s => Visible(s, key, host, canBuild)).OrderByDescending(s => s.Updated).Select(s => new
        {
            s.Id, s.Title, text = s.Text.Length > 300 ? s.Text[..299] + "…" : s.Text, s.OwnerName, mine = s.Owner == key, s.Kept, s.Pinned, s.Updated, s.Origin,
            versions = s.Versions.Count, durationMs = s.Versions.Count > 0 ? s.Versions[^1].DurationMs : 0,
            missing = s.Pieces.Count(p => p.Kind == "missing"), canEdit = CanChange(s, key, host, canBuild),
        }).ToArray(),
        named = f.Named.OrderBy(n => n.Name, StringComparer.Ordinal).Select(n => new { n.Id, n.Name, n.RecordingId, n.StartMs, n.EndMs, n.ByName }).ToArray(),
        // 2.45 (D10): each guest's unkept sentences, for the host only (guests never see one another's), newest first.
        guestUnkept = host ? GuestUnkept(f) : null,
    });
    private static object[] GuestUnkept(SentenceFile f) => [.. f.Sentences.Where(s => !s.Kept && s.Owner != "host").GroupBy(s => s.Owner, StringComparer.Ordinal)
        .Select(g => (g.Key, List: g.OrderByDescending(s => s.Updated).ToList())).OrderByDescending(g => g.List[0].Updated).Take(512)
        .Select(g => (object)new
        {
            guestId = g.Key, ownerName = g.List[0].OwnerName, count = g.List.Count, updated = g.List[0].Updated,
            sentences = g.List.Take(MaxSentencesPerPerson).Select(s => new { s.Id, s.Title, text = s.Text.Length > 300 ? s.Text[..299] + "…" : s.Text, s.Updated, s.Origin, missing = s.Pieces.Count(p => p.Kind == "missing") }).ToArray(),
        })];

    private object CreateSentence(Command c, SentenceInput input, string key, Participant person, bool canBuild)
    {
        if (!canBuild) throw new InvalidDataException("The host has not allowed guests to build sentences.");
        var text = Text(input.Text);
        var sets = PersonSetsOf(key);
        var active = sets.Sets.First(s => s.Id == sets.Active);
        var project = new SentenceProject
        {
            Id = SentenceStore.NewId(), Owner = key, OwnerName = person.Name, Created = utcNow(), Updated = utcNow(),
            Title = Clip(c.Title ?? "", 200), Origin = input.Origin is "template" ? "template" : "typed",
            Settings = new SentenceSettings { Seed = RandomNumberGenerator.GetInt32(int.MaxValue), Chaos = Math.Clamp(input.Chaos ?? 45, 0, 100), VoiceIds = [.. (input.VoiceIds ?? [.. active.VoiceIds]).Where(SentenceStore.ValidId).Distinct().Take(256)] },
        };
        // A template's voice slots and phrase blocks (2.45) arrive as voice marks in its text: each run of words is
        // matched within its voice. Typed text is never read for marks.
        if (project.Origin == "template" && text.Contains("{@", StringComparison.Ordinal))
        {
            var (clean, runs) = SentenceComposer.VoiceMarked(text);
            SentenceComposer.Compose(project, clean, runs, Corpus());
        }
        else SentenceComposer.Rematch(project, text, Corpus());
        bool host = key == "host";
        SentenceData.Change(f =>
        {
            SweepGuestDrafts(f);
            RequireRoom(f, key, host);
            f.Sentences.Add(project); return 0;
        }, !host);
        SentencesChanged();
        return SentenceView(project.Id, key, host, canBuild);
    }
    private static string Clip(string text, int max)
    {
        var clean = new string([.. text.Where(ch => !char.IsControl(ch))]).Trim();
        return clean.Length > max ? clean[..max].TrimEnd() : clean;
    }

    /// <summary>Changes a project the asker may change; its revision rises. Returns the new view.</summary>
    private object ChangeSentence(string? id, string key, bool host, bool canBuild, Action<SentenceProject> change, object? extra = null)
    {
        SentenceData.Change(f =>
        {
            var s = FindSentence(f, id);
            if (!CanChange(s, key, host, canBuild)) throw new InvalidDataException(s.Owner == key ? "The host has not allowed guests to build sentences." : "Only its maker or the host can change this sentence.");
            change(s);
            s.Revision++; s.Updated = utcNow();
            return 0;
        }, !host);
        SentencesChanged();
        var view = SentenceView(id, key, host, canBuild);
        return extra == null ? view : new { sentence = view, result = extra };
    }

    private SentencePiece PieceOf(SentenceProject s, string? pieceId) => s.Pieces.FirstOrDefault(p => p.Id == pieceId) ?? throw new InvalidDataException("That piece is no longer in the sentence.");

    private object EditSentence(Command c, SentenceInput input, string key, bool host, bool canBuild)
    {
        var corpus = Corpus();
        var ids = input.Pieces ?? [];
        if (ids.Length > SentenceStore.MaxPieces || ids.Any(id => !SentenceStore.ValidPieceId(id))) throw new InvalidDataException("Choose pieces of the sentence.");
        object? result = null;
        var view = ChangeSentence(c.SentenceId, key, host, canBuild, s =>
        {
            SentencePiece One() => PieceOf(s, ids.FirstOrDefault());
            switch (input.Do)
            {
                case "move":
                    {
                        var p = One(); double at = input.OffsetMs ?? throw new InvalidDataException("Where should it go?");
                        if (!double.IsFinite(at) || at < 0 || at > SentenceStore.MaxTimelineMs) throw new InvalidDataException("That is outside the timeline.");
                        // Free movement: only this piece moves; overlaps are allowed (the later-starting piece cuts the earlier one).
                        p.OffsetMs = Math.Round(at, 1); p.Manual = true;
                        s.Pieces.Sort((x, y) => x.OffsetMs.CompareTo(y.OffsetMs));
                        break;
                    }
                case "edges":
                    {
                        var p = One();
                        if (p.Kind is not ("word" or "named") || p.Source == null) throw new InvalidDataException("Only recorded pieces have edges.");
                        var r = Voices.Get(p.Source.RecordingId) ?? throw new InvalidDataException("Its recording is no longer in the voice library.");
                        long start = input.StartMs ?? p.Source.StartMs, end = input.EndMs ?? p.Source.EndMs;
                        if (start < 0 || end <= start + 10 || r.DurationMs > 0 && end > r.DurationMs + 50 || end - start > 30000) throw new InvalidDataException("Those edges are outside the recording.");
                        p.Source.StartMs = start; p.Source.EndMs = end; p.Manual = true;
                        if (input.FadeInMs is double fi) p.FadeInMs = Fade(fi);
                        if (input.FadeOutMs is double fo) p.FadeOutMs = Fade(fo);
                        break;
                    }
                case "fade":
                    foreach (var p in ids.Select(id => PieceOf(s, id))) { if (input.FadeInMs is double fi) p.FadeInMs = Fade(fi); if (input.FadeOutMs is double fo) p.FadeOutMs = Fade(fo); }
                    break;
                case "gain":
                    foreach (var p in ids.Select(id => PieceOf(s, id)))
                    {
                        if (input.Balanced == true) p.Gain = null;
                        else if (input.Gain is double g && double.IsFinite(g) && g is >= 0 and <= 8) p.Gain = Math.Round(g, 3);
                        else throw new InvalidDataException("Choose a volume between 0 and 8 times.");
                    }
                    break;
                case "delete":
                    foreach (var p in ids.Select(id => PieceOf(s, id)).ToList())
                    {
                        // Delete leaves an editable pause of the same length where the piece was; deleting a pause removes it.
                        if (p.Kind == "pause") { s.Pieces.Remove(p); continue; }
                        double length = Math.Clamp(SentenceComposer.Duration(p), 10, 60000);
                        p.Kind = "pause"; p.DurationMs = Math.Round(length, 1); p.Source = null; p.Parts = null; p.Missing = null; p.Substitute = null; p.NamedId = null; p.Effect = null; p.Manual = true; p.Chosen = false;
                    }
                    break;
                case "pause":
                    {
                        var p = One();
                        if (p.Kind is not ("pause" or "missing")) throw new InvalidDataException("Only pauses and missing words have a length of their own.");
                        double length = input.DurationMs ?? 0;
                        if (!double.IsFinite(length) || length < 10 || length > 60000) throw new InvalidDataException("A pause lasts 10 ms to 60 s.");
                        p.DurationMs = Math.Round(length, 1);
                        // A hand-placed pause (a deleted piece) keeps everything else where it is; a missing word's room flows.
                        if (!p.Manual) SentenceComposer.Layout(s);
                        break;
                    }
                case "insertPause":
                    {
                        double at = input.OffsetMs ?? 0, length = input.DurationMs ?? 300;
                        if (!double.IsFinite(at) || at < 0 || at > SentenceStore.MaxTimelineMs || !double.IsFinite(length) || length < 10 || length > 60000) throw new InvalidDataException("A pause lasts 10 ms to 60 s, inside the timeline.");
                        if (s.Pieces.Count >= SentenceStore.MaxPieces) throw new InvalidDataException("This sentence has as many pieces as it can hold.");
                        var p = new SentencePiece { Id = SentenceStore.NewPieceId(), Kind = "pause", Text = "", OffsetMs = at, DurationMs = length, Manual = true };
                        s.Pieces.Add(p); result = new { pieceId = p.Id };
                        SentenceComposer.Layout(s);
                        break;
                    }
                case "missing": SentenceComposer.MakeMissing(s, One(), input.Mode ?? "gap"); break;
                case "effects":
                    {
                        var effect = input.Effect is { } e ? new PieceEffect { Reverse = e.Reverse, Speed = Math.Clamp(double.IsFinite(e.Speed) ? e.Speed : 1, 0.25, 4), KeepPitch = e.KeepPitch, Echo = e.Echo } : null;
                        if (effect is { IsNone: true }) effect = null;
                        // The selection, or the whole sentence when nothing is selected.
                        if (ids.Length == 0) s.Settings.Effect = effect;
                        else foreach (var p in ids.Select(id => PieceOf(s, id))) p.Effect = p.Kind is "pause" or "missing" ? null : SentenceStore.Copy(effect);
                        SentenceComposer.Layout(s);
                        break;
                    }
                case "settings":
                    {
                        bool recompose = false;
                        if (input.Chaos is int chaos) { if (chaos is < 0 or > 100) throw new InvalidDataException("Smooth–Chaotic goes from 0 to 100."); recompose |= chaos != s.Settings.Chaos; s.Settings.Chaos = chaos; }
                        if (input.VoiceIds is { } voices)
                        {
                            if (voices.Length > 256 || voices.Any(v => !SentenceStore.ValidId(v))) throw new InvalidDataException("Choose voices from the library.");
                            var list = voices.Distinct().ToList(); recompose |= !list.SequenceEqual(s.Settings.VoiceIds); s.Settings.VoiceIds = list;
                        }
                        if (input.Altered is bool altered) { recompose |= altered != s.Settings.Altered; s.Settings.Altered = altered; }
                        if (input.Balance is bool balance) s.Settings.Balance = balance;
                        if (input.Cleanup is bool cleanup) s.Settings.Cleanup = cleanup;
                        if (input.Timing is { } timing) { if (timing is not ("fluent" or "original")) throw new InvalidDataException("Timing is fluent or original."); s.Settings.Timing = timing; }
                        if (recompose) SentenceComposer.Recompose(s, corpus); else SentenceComposer.Layout(s);
                        break;
                    }
                case "swap": if (!SentenceComposer.Swap(s, One(), corpus, input.Alternative)) throw new InvalidDataException("There is no other take of that."); break;
                case "resolve":
                    {
                        var p = One();
                        if (p.Kind != "missing" && p.Substitute == null && p.Kind != "splice") throw new InvalidDataException("Only a missing word can take a similar word or a splice.");
                        var tokens = SentenceComposer.Tokens(s.Text);
                        string word = p.TokenStart is int t && t < tokens.Count ? tokens[t].Key : WordIndex.Token(p.Text);
                        if (input.Word is { } similar)
                        {
                            // Only one of the similar words offered for it, chosen on purpose.
                            if (!SentenceComposer.Similar(word, corpus, SentenceComposer.FilterFor(s, p), 50).Any(x => x.Word == WordIndex.Token(similar))) throw new InvalidDataException("That word is not one of the similar recorded words.");
                            SentenceComposer.UseSimilar(s, p, similar, corpus);
                        }
                        else if (input.Splice is int index)
                        {
                            var candidates = SentenceComposer.Splices(word, corpus, SentenceComposer.FilterFor(s, p));
                            if (index < 0 || index >= candidates.Count) throw new InvalidDataException("That splice is no longer offered.");
                            SentenceComposer.UseSplice(s, p, candidates[index]);
                        }
                        else throw new InvalidDataException("Choose a similar word or a splice.");
                        break;
                    }
                case "shuffle": result = new { changed = SentenceComposer.Shuffle(s, ids, corpus) }; break;
                case "contrast": result = new { changed = SentenceComposer.Contrast(s, corpus) }; break;
                case "rematch": result = new { found = SentenceComposer.FindMissingAgain(s, corpus) }; break;
                case "restore":
                    {
                        var v = s.Versions.FirstOrDefault(x => x.N == input.Version) ?? throw new InvalidDataException("That version is no longer there.");
                        s.Text = v.Text; s.Pieces = SentenceStore.Copy(v.Pieces); s.Settings = SentenceStore.Copy(v.Settings);
                        break;
                    }
                case "pin":
                    if (!s.Kept) throw new InvalidDataException("Keep the sentence before pinning it to the board.");
                    s.Pinned = input.Pinned ?? !s.Pinned;
                    break;
                case "insert" when input.Caret is int || input.Item == null:
                    {
                        // 2.45 (D09, D08): a palette item, or a phrase block's words, into the text at the caret (the end
                        // when no caret is given). Nothing plays. The item is that exact take, chosen; a block's words are
                        // matched like typed text, within its voice when it has one.
                        if (s.Pieces.Count >= SentenceStore.MaxPieces) throw new InvalidDataException("This sentence has as many pieces as it can hold.");
                        int caret = input.Caret ?? s.Text.Length;
                        if (input.Item is { } item)
                        {
                            var label = Resolve(item).Label;
                            var (at, count, after) = SentenceComposer.InsertText(s, caret, label);
                            var p = PieceFromItem(item, 0, at, at + count);
                            p.Manual = false;
                            s.Pieces.Add(p); result = new { pieceId = p.Id, caret = after };
                        }
                        else
                        {
                            var words = Text(input.Text).Trim();
                            if (words.Length == 0 || words.Length > 400) throw new InvalidDataException("Add up to 400 characters of words.");
                            string? voice = input.VoiceIds is [var only] ? only : input.VoiceIds is { Length: > 1 } ? throw new InvalidDataException("A phrase is said by one voice.") : null;
                            if (voice != null && !SentenceStore.ValidId(voice)) throw new InvalidDataException("Choose a voice from the library.");
                            var (at, count, after) = SentenceComposer.InsertText(s, caret, words);
                            var tokens = SentenceComposer.Tokens(s.Text);
                            string? before = s.Pieces.Where(x => x.TokenEnd <= at && x.Source != null).OrderBy(x => x.TokenEnd).LastOrDefault()?.Source?.VoiceId;
                            var fresh = SentenceComposer.Match(tokens, at, at + count, voice == null ? s.Settings : SentenceComposer.Locked(s.Settings, voice), corpus, before);
                            foreach (var p in fresh) p.VoiceLock = voice;
                            if (s.Pieces.Count + fresh.Count > SentenceStore.MaxPieces) throw new InvalidDataException("This sentence has as many pieces as it can hold.");
                            s.Pieces.AddRange(fresh); result = new { pieces = fresh.Select(x => x.Id).ToArray(), caret = after };
                        }
                        SentenceComposer.Layout(s);
                        break;
                    }
                case "insert":
                    {
                        var item = input.Item ?? throw new InvalidDataException("Choose a palette item.");
                        if (s.Pieces.Count >= SentenceStore.MaxPieces) throw new InvalidDataException("This sentence has as many pieces as it can hold.");
                        var p = PieceFromItem(item, input.OffsetMs ?? (s.Pieces.Count == 0 ? 0 : s.Pieces.Max(x => x.OffsetMs + SentenceComposer.Duration(x)) + SentenceComposer.FluentGapMs));
                        s.Pieces.Add(p); result = new { pieceId = p.Id };
                        SentenceComposer.Layout(s);
                        break;
                    }
                case "title": s.Title = Clip(c.Title ?? "", 200); break;
                default: throw new InvalidDataException("Unsupported sentence change.");
            }
        }, null);
        return result == null ? view : new { sentence = view, result };
    }
    private static double Fade(double ms) => double.IsFinite(ms) && ms is >= 0 and <= 2000 ? Math.Round(ms, 1) : throw new InvalidDataException("A fade lasts 0 to 2000 ms.");

    /// <summary>A palette item as a hand-placed piece (not tied to the text).</summary>
    private SentencePiece PieceFromItem(PaletteRef item, double offset, int? token = null, int? tokenEnd = null)
    {
        var (recordingId, start, end, namedId, label) = Resolve(item);
        var r = Voices.Get(recordingId)!;
        return new SentencePiece
        {
            Id = SentenceStore.NewPieceId(), Kind = namedId != null ? "named" : "word", Text = label, NamedId = namedId, OffsetMs = Math.Clamp(offset, 0, SentenceStore.MaxTimelineMs), Manual = true, Chosen = true,
            TokenStart = token, TokenEnd = tokenEnd, Source = new() { RecordingId = recordingId, StartMs = start, EndMs = end, VoiceId = r.VoiceId }, GapAfterMs = 120,
        };
    }

    /// <summary>A palette reference checked against the library: a range of a recording, or a named piece.</summary>
    private (string RecordingId, long Start, long End, string? NamedId, string Label) Resolve(PaletteRef item)
    {
        if (item.NamedId != null)
        {
            var named = SentenceData.Read(f => f.Named.FirstOrDefault(n => n.Id == item.NamedId) is { } n ? (n.RecordingId, n.StartMs, n.EndMs, n.Name) : default);
            if (named.RecordingId == null || Voices.Get(named.RecordingId) == null) throw new InvalidDataException("That named piece is no longer in the voice library.");
            return (named.RecordingId, named.StartMs, named.EndMs, item.NamedId, "[" + named.Name + "]");
        }
        if (!SentenceStore.ValidId(item.RecordingId)) throw new InvalidDataException("Choose a palette item.");
        var r = Voices.Get(item.RecordingId!) ?? throw new InvalidDataException("That recording is no longer in the voice library.");
        if (item.StartMs < 0 || item.EndMs <= item.StartMs || item.EndMs - item.StartMs > 30000 || r.DurationMs > 0 && item.StartMs >= r.DurationMs) throw new InvalidDataException("That part is outside the recording.");
        var label = Clip(item.Text ?? "", 100);
        return (r.Id, item.StartMs, item.EndMs, null, label.Length > 0 ? label : r.Title);
    }

    private object DiscardSentence(string? id, string key, bool host)
    {
        bool kept = SentenceData.Change(f =>
        {
            var s = FindSentence(f, id);
            // A kept sentence goes from the list only by the host's hand.
            if (s.Kept ? !host : s.Owner != key && !host) throw new InvalidDataException(s.Kept ? "Only the host can remove a kept sentence." : "Only its maker can discard this sentence.");
            f.Sentences.Remove(s); return s.Kept;
        }, !host);
        SentencesChanged();
        // 2.45 (D10): the removed sentence's rendered audio goes with it, file by file, once no version refers to it.
        if (kept && id != null)
        {
            var (files, bytes) = SentenceData.SweepAudio(id);
            if (files > 0) Log($"Freed {files} kept sentence audio file{(files == 1 ? "" : "s")} ({bytes / 1024} KB)");
            return new { removed = true, freedFiles = files, freedBytes = bytes };
        }
        return new { removed = true };
    }

    /// <summary>2.45 (D10): the host clears one guest's unkept sentences (kept ones stay, and so does everyone else's).</summary>
    private object ClearGuestUnkept(string guestId, bool host)
    {
        if (!host) throw new InvalidDataException("Only the host can clear a guest's sentences.");
        if (guestId.Length is 0 or > 128 || guestId == "host") throw new InvalidDataException("Choose a guest.");
        int removed = SentenceData.Change(f => f.Sentences.RemoveAll(s => !s.Kept && s.Owner == guestId));
        if (removed > 0) { Log($"The host cleared {removed} unkept sentence{(removed == 1 ? "" : "s")} of a guest"); SentencesChanged(); }
        return new { removed };
    }

    // ---- Views ---------------------------------------------------------------------------------------------------

    /// <summary>SHA-256 of a recording's file, remembered while its length and time stay the same.</summary>
    private string? FileHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (fileHashes.TryGetValue(path, out var known) && known.Length == info.Length && known.Modified == info.LastWriteTimeUtc.Ticks) return known.Hash;
            string hash;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (fileHashes.Count > 4096) fileHashes.Clear();
            fileHashes[path] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>"ok", "missing" (the recording or its file is gone) or "changed" (its file differs from what a kept version pinned).</summary>
    private string SourceState(SentenceSource source, string pieceId, SentenceVersion? latest)
    {
        var r = Voices.Get(source.RecordingId);
        if (r == null || !File.Exists(r.Path)) return "missing";
        if (latest?.Sources.FirstOrDefault(x => x.PieceId == pieceId && x.RecordingId == source.RecordingId) is { } pinned && FileHash(r.Path) is { } now && now != pinned.Hash) return "changed";
        return "ok";
    }

    private object SentenceView(string? id, string key, bool host, bool canBuild)
    {
        var s = Visible(SentenceCopy(id, key, host), key, host, canBuild);
        var library = Voices;
        var latest = s.Versions.Count > 0 ? s.Versions[^1] : null;
        var tokens = SentenceComposer.Tokens(s.Text);
        var ordered = s.Pieces.OrderBy(p => p.OffsetMs).ToList();
        // Overlaps: a piece that starts while an earlier one still sounds cuts that one off in the render.
        var overlaps = new List<object>();
        for (int i = 0; i < ordered.Count; i++)
            for (int j = i + 1; j < ordered.Count && ordered[j].OffsetMs < ordered[i].OffsetMs + SentenceComposer.Duration(ordered[i]); j++)
                overlaps.Add(new { first = ordered[i].Id, second = ordered[j].Id, ms = Math.Round(ordered[i].OffsetMs + SentenceComposer.Duration(ordered[i]) - ordered[j].OffsetMs, 1) });
        object PartView(SentenceSource src, string pieceId) => new { src.RecordingId, src.StartMs, src.EndMs, src.VoiceId, src.Label, recordingMs = library.Get(src.RecordingId)?.DurationMs, state = SourceState(src, pieceId, latest) };
        return new
        {
            s.Id, s.Title, s.Text, s.OwnerName, mine = s.Owner == key, s.Origin, s.Revision, s.Created, s.Updated, s.Kept, s.Pinned,
            canEdit = CanChange(s, key, host, canBuild),
            settings = new { s.Settings.Chaos, s.Settings.Timing, s.Settings.Balance, s.Settings.Cleanup, s.Settings.Altered, s.Settings.VoiceIds, s.Settings.Effect },
            tokens = tokens.Select(t => t.Display).ToArray(),
            pieces = ordered.Select(p => new
            {
                p.Id, p.Kind, p.TokenStart, p.TokenEnd, p.Text, offsetMs = Math.Round(p.OffsetMs, 1), durationMs = Math.Round(SentenceComposer.Duration(p), 1), p.Manual, p.Chosen,
                p.FadeInMs, p.FadeOutMs, p.Gain, p.Missing, p.Substitute, p.NamedId, p.Effect, p.VoiceLock,
                voiceId = p.Source?.VoiceId ?? p.Parts?.FirstOrDefault()?.VoiceId,
                source = p.Source == null ? null : PartView(p.Source, p.Id),
                parts = p.Parts?.Select(x => PartView(x, p.Id)).ToArray(),
            }).ToArray(),
            overlaps,
            durationMs = ordered.Count == 0 ? 0 : Math.Round(ordered.Max(p => p.OffsetMs + SentenceComposer.Duration(p)), 1),
            missing = s.Pieces.Count(p => p.Kind == "missing"),
            suggestContrast = SentenceComposer.SoundsUnsettling(s.Text),
            versions = s.Versions.Select(v => new
            {
                v.N, v.At, v.ByName, v.DurationMs, v.Text,
                // Which pieces of that version lost (or changed) their source since: the version itself still plays (its audio was kept).
                affected = v.Sources.Select(x => (x, r: library.Get(x.RecordingId))).Where(x => x.r == null || !File.Exists(x.r.Path) || FileHash(x.r.Path) != x.x.Hash)
                    .Select(x => new { x.x.PieceId, text = v.Pieces.FirstOrDefault(p => p.Id == x.x.PieceId)?.Text ?? "", state = x.r == null || !File.Exists(x.r.Path) ? "missing" : "changed" }).ToArray(),
            }).Reverse().ToArray(),
        };
    }

    private object SentenceAlternatives(string? id, string? pieceId, string key, bool host, bool canBuild)
    {
        var s = Visible(SentenceCopy(id, key, host), key, host, canBuild);
        var p = PieceOf(s, pieceId); var corpus = Corpus(); var library = Voices;
        var takes = p.Kind is "word" or "named" ? SentenceComposer.Alternatives(s, p, corpus) : [];
        int current = SentenceComposer.CurrentAlternative(p, takes);
        var tokens = SentenceComposer.Tokens(s.Text);
        string word = p.TokenStart is int t && t < tokens.Count ? tokens[t].Key : WordIndex.Token(p.Text);
        bool missingLike = p.Kind == "missing" || p.Substitute != null || p.Kind == "splice";
        var filter = SentenceComposer.FilterFor(s, p);
        return new
        {
            pieceId = p.Id, current,
            takes = takes.Take(60).Select((x, i) => new { index = i, x.RecordingId, x.VoiceId, x.Text, durationMs = x.EndMs - x.StartMs, title = library.Get(x.RecordingId)?.Title ?? "", altered = x.Altered, current = i == current }).ToArray(),
            similar = missingLike ? SentenceComposer.Similar(word, corpus, filter).Select(x => new { word = x.Word, count = x.Count }).ToArray() : [],
            splices = missingLike ? SentenceComposer.Splices(word, corpus, filter).Select((x, i) => new { index = i, x.Description, parts = x.Parts.Select(y => new { y.Label, y.VoiceId, durationMs = y.EndMs - y.StartMs }).ToArray() }).ToArray() : [],
        };
    }

    // ---- Rendering, preview and play -----------------------------------------------------------------------------

    /// <summary>
    /// Renders pieces (a copy) from the library recordings they name, decoding each file once (the PCM cache), and
    /// hands the result to <paramref name="then"/>. Both run on the audio worker (review, 2.42), never at normal
    /// priority on the thread pool. A sentence laid out longer than the limit is refused before anything is decoded;
    /// <paramref name="cancel"/> stops a render nobody waits for any more.
    /// </summary>
    private async Task<T> RenderPieces<T>(IReadOnlyList<SentencePiece> pieces, SentenceSettings settings, Func<SentenceRender.Result, T> then, bool? cleanup = null, CancellationToken cancel = default)
    {
        if (SentenceRender.LaidOutMs(pieces, settings) > SentenceRender.MaxSentenceMs) throw new InvalidDataException($"A sentence can be at most {SentenceRender.MaxSentenceMs / 1000:0} seconds long.");
        var library = Voices; var sources = new Dictionary<string, ISliceSource>(StringComparer.Ordinal);
        foreach (var id in pieces.SelectMany(p => p.Parts ?? (p.Source != null ? [p.Source] : [])).Select(x => x.RecordingId).Distinct())
        {
            cancel.ThrowIfCancellationRequested();
            var r = library.Get(id);
            if (r == null || !File.Exists(r.Path)) continue; // the renderer names the piece
            try { sources[id] = await pcm.Get(r.Path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return await Heavy("sentence render", () => then(SentenceRender.Render(pieces, settings, id => sources.TryGetValue(id, out var source) ? source : throw new KeyNotFoundException(id), cleanup, cancel)), cancel);
    }

    /// <summary>
    /// A render for the game: one at a time (review, 2.42), and given up as soon as a newer intent, Stop or Panic
    /// arrives, whether it is still waiting for its turn or rendering. Null when it was given up.
    /// </summary>
    private async Task<T?> RenderForGame<T>(Intent intent, Func<CancellationToken, Task<T>> render) where T : class
    {
        try { await sentenceRenderSlot.WaitAsync(intent.Withdrawn.Token); }
        catch (OperationCanceledException) { return null; }
        try { return await render(intent.Withdrawn.Token); }
        catch (OperationCanceledException) when (intent.Withdrawn.IsCancellationRequested) { return null; }
        finally { sentenceRenderSlot.Release(); }
    }

    private SentenceProject SentenceCopy(string? id, string key, bool host) => SentenceData.Read(f =>
    {
        var s = FindSentence(f, id);
        return CanSee(s, key, host) ? SentenceStore.Copy(s) : throw new InvalidDataException("That sentence is no longer there.");
    });

    private async Task<object> PreviewSentence(Command c, SentenceInput input, string actor, Participant? participant, string? requester, string key, bool host, bool canBuild)
    {
        var s = Visible(SentenceCopy(c.SentenceId, key, host), key, host, canBuild);
        if (!host && !previewing.TryAdd(key, 0)) throw new InvalidDataException("Your last preview is still being made.");
        try
        {
            if (!host && !previewRate.TryTake(key)) throw new InvalidDataException("Too many previews. Wait a minute and try again.");
            byte[] wav;
            if (input.Version is int n)
            {
                var v = s.Versions.FirstOrDefault(x => x.N == n) ?? throw new InvalidDataException("That version is no longer there.");
                wav = await Heavy("sentence preview", () =>
                {
                    var audio = ReadKept(s.Id, v);
                    if (!SliceWav.TryRead(audio, out _)) throw new InvalidDataException("That version's audio could not be read.");
                    return host ? audio : KeptPreview(audio);
                });
            }
            else
            {
                List<SentencePiece> pieces;
                if (input.Pieces is { Length: > 0 } ids)
                {
                    pieces = [.. ids.Select(id => PieceOf(s, id))];
                    if (input.Alternative is int alt && pieces.Count == 1)
                    {
                        // A take to compare before swapping to it: only that take.
                        var list = SentenceComposer.Alternatives(s, pieces[0], Corpus());
                        if (alt < 0 || alt >= list.Count) throw new InvalidDataException("That take is no longer in the voice library.");
                        var piece = SentenceStore.Copy(pieces[0]); var take = list[alt];
                        piece.Source = piece.Kind == "named" ? new() { RecordingId = take.RecordingId, StartMs = take.StartMs, EndMs = take.EndMs } : SentenceComposer.SourceOf(take);
                        pieces = [piece];
                    }
                    double first = pieces.Min(p => p.OffsetMs);
                    foreach (var p in pieces) p.OffsetMs -= first;
                }
                else
                {
                    // Compare "fluent" joins with the original timing without changing the sentence.
                    if (input.Timing is "fluent" or "original") { s.Settings.Timing = input.Timing; SentenceComposer.Layout(s); }
                    pieces = s.Pieces;
                }
                bool? cleanup = input.Variant switch { "original" => false, "cleaned" => true, _ => null };
                wav = await RenderPieces(pieces, s.Settings, rendered => SentenceRender.PreviewWav(rendered.Audio, host ? null : GuestPreviewRate, PreviewMaxMs), cleanup);
            }
            return new { wav = Convert.ToBase64String(wav), revision = s.Revision };
        }
        finally { if (!host) previewing.TryRemove(key, out _); }
    }
    private static byte[] KeptPreview(byte[] wav)
    {
        if (!SliceWav.TryRead(wav, out var format)) throw new InvalidDataException("That version's audio could not be read.");
        var pcm16 = SliceWav.Pcm16(wav);
        var audio = new SliceAudio([.. pcm16.Select(x => x / 32768f)], format.SampleRate, format.Channels);
        return SentenceRender.PreviewWav(audio, GuestPreviewRate, PreviewMaxMs);
    }

    private byte[] ReadKept(string sentenceId, SentenceVersion v)
    {
        var path = SentenceData.AudioPath(sentenceId, v.File);
        if (!File.Exists(path)) throw new InvalidDataException($"Version {v.N}'s kept audio is missing from the library folder.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != v.Hash) throw new InvalidDataException($"Version {v.N}'s kept audio was changed on disk, so it is not played.");
        return bytes;
    }

    private async Task<Reply> PlaySentence(Command c, SentenceInput input, string actor, Participant? participant, string? requester, string? audience, Intent? intent, string key, bool host)
    {
        if (intent == null) throw new InvalidDataException(!disk.Permissions.RemotePlayback ? "Remote playback is disabled."
            : input.Version is null && !disk.Permissions.GuestSeeking ? GuestPartsRefusal : "Guest playback permission or session authorization changed.");
        // The revision asked for: edits made while it renders or plays are heard on the next Play.
        var s = SentenceCopy(c.SentenceId, key, host);
        if (SentenceRenderHold is { } hold) await hold();
        if (!IsNewest(intent)) return Superseded(c, "newer");
        byte[]? wav; int? version = null;
        bool kept = input.Version is not null || !CanChange(s, key, host, true) && s.Kept;
        if (kept)
        {
            var v = input.Version is int wanted ? s.Versions.FirstOrDefault(x => x.N == wanted) : s.Versions.LastOrDefault();
            if (v == null) throw new InvalidDataException("That version is no longer there.");
            wav = await Heavy("sentence read", () => ReadKept(s.Id, v)); version = v.N;
        }
        else
        {
            // Rendered words can be any part of a recording: a guest also needs seeking (like a region; review, 2.42).
            if (!host && !disk.Permissions.GuestSeeking) throw new InvalidDataException(GuestPartsRefusal);
            wav = await RenderForGame(intent, cancel => RenderPieces(s.Pieces, s.Settings, rendered => rendered.Audio.ToWav(), cancel: cancel));
            if (wav == null) return Superseded(c, "newer");
        }
        return await PlayInGame(c, wav, "sentence:" + s.Id, actor, participant, requester, audience, intent, !kept, new { revision = s.Revision, version });
    }
    private const string GuestPartsRefusal = "Guest seeking is disabled, so guests can play only kept sentences into the game.";

    /// <summary>
    /// Registers the slot files for sentences and palette taps (also when exact cue starts are off), at a quiet
    /// moment as always: never while Soundpad plays. Waits up to about two seconds; not at all when registration
    /// failed for another reason a moment ago (review, 2.42: every Play used to start it over and wait the full time).
    /// </summary>
    private async Task<bool> EnsureSentenceSlots(Intent intent)
    {
        if (SlotsReady) return true;
        slotsForSentences = true; slotsWanted = true;
        if (Environment.TickCount64 < Interlocked.Read(ref slotsFailedUntil)) return false;
        Interlocked.Exchange(ref slotsRetryAt, 0);
        for (int i = 0; i < 20 && !SlotsReady && IsNewest(intent); i++)
        {
            await RegisterSlotsIfDue();
            if (SlotsReady || Environment.TickCount64 < Interlocked.Read(ref slotsFailedUntil)) break;
            await Task.Delay(100);
        }
        return SlotsReady;
    }

    /// <summary>
    /// Plays rendered audio into the game from a slot file, through the same newest-intent arbitration as every
    /// start: a newer intent, Stop or Panic that arrived meanwhile wins, and this plays nothing. So does a guest whose
    /// permission changed meanwhile (revoked, Remote playback or, for <paramref name="parts"/>, seeking turned off).
    /// The playback the website sees is labelled <paramref name="label"/> ("sentence:…" or "palette:…").
    /// </summary>
    private async Task<Reply> PlayInGame(Command c, byte[] wav, string label, string actor, Participant? participant, string? requester, string? audience, Intent intent, bool parts, object extra)
    {
        if (!IsNewest(intent)) return Superseded(c, "newer");
        if (!SliceWav.TryRead(wav, out var format) || format.Frames == 0) throw new InvalidDataException("There is nothing to play.");
        long ms = (long)Math.Round(format.Frames * 1000.0 / format.SampleRate);
        if (!await EnsureSentenceSlots(intent))
        {
            if (!IsNewest(intent)) return Superseded(c, "newer");
            throw new InvalidDataException("Game playback is getting ready: Soundpad needs a quiet moment once. Stop what is playing, or try again in a moment.");
        }
        ExactPlan? plan = null; var tried = new List<int>();
        for (int attempt = 0; attempt < 4 && plan == null; attempt++)
        {
            if (!IsNewest(intent)) return Superseded(c, "newer");
            lock (playbackSync) if (slice != null && !tried.Contains(slice.Lease.Index)) tried.Add(slice.Lease.Index);
            var lease = Slots.Pool.Acquire(tried);
            if (lease == null) { await Task.Delay(10); continue; }
            SlotWriteResult written;
            try { written = await Slots.Pool.Write(lease, wav); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { written = new(false, 0, Slots.Pool.Info(lease.Index), ex.Message); }
            if (written.Ok) plan = new ExactPlan(lease, written.Slot.Generation, 0, ms, ms);
            else { Slots.Pool.Release(lease); tried.Add(lease.Index); }
        }
        if (plan == null) throw new InvalidDataException("The game playback slots are busy. Try again in a moment.");
        try
        {
            if (!await EnterFor(gate, intent)) return Superseded(c, "newer");
            try
            {
                if (!IsNewest(intent)) return Superseded(c, "newer");
                // Checked again after the slow part (review, 2.42), as Temp and kept plays do: revokePeer and permission
                // changes reserve no intent, so they do not withdraw a start that is under way.
                if (actor != "host" && !(sessionAccepting && GuestStillAllowed(actor, requester, audience) && !playbackSuspended && (!parts || disk.Permissions.GuestSeeking))) return Superseded(c, "suspended");
                if (!available) throw new InvalidDataException("Soundpad is unavailable. Open it normally; reconnection is automatic.");
                string list;
                try { list = await Pipe.Request("GetSoundlist()", intent.Withdrawn.Token); }
                catch (OperationCanceledException) when (intent.Withdrawn.IsCancellationRequested) { return Superseded(c, "newer"); }
                if (list != lastSoundlist) await RefreshInner();
                if (SlotIndexOf(plan.Lease, plan.Generation) is not int index) { slotsWanted = true; throw new InvalidDataException("Game playback is getting ready. Try again in a moment."); }
                string answer;
                try { answer = await Pipe.Request($"DoPlaySound({index})", intent.Withdrawn.Token); }
                catch (OperationCanceledException) when (intent.Withdrawn.IsCancellationRequested) { return Superseded(c, "newer"); }
                if (!answer.StartsWith("R-200", StringComparison.Ordinal)) throw new InvalidDataException("Soundpad rejected this action or its API does not support it.");
                NoteAudio(); NoteOwnReplay(null, ms);
                var person = Person(actor, participant); var epoch = Guid.NewGuid().ToString(); long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SlotLease? free;
                lock (playbackSync)
                {
                    if (playbackOwner > intent.Revision) return Superseded(c, "newer");
                    playbackOwner = intent.Revision; playbackVersion++;
                    free = DropSliceLocked();
                    slice = new ExactSlice(plan, false, intent); plan.Taken = true;
                    playback = new("PLAYING", 0, ms, label, epoch, person.Name, now, "web");
                    sampledTick = Environment.TickCount64; regionStart = null; seekSettlesUntil = Environment.TickCount64 + 1500;
                }
                Free(free);
                WakeAfter(ms);
                Publish(); CommandAccepted?.Invoke();
                return new("ack", c.Id, true, Data: new { played = true, epoch, slot = true, durationMs = ms, detail = extra });
            }
            finally { gate.Release(); }
        }
        finally { Discard(plan); }
    }

    private async Task<object> KeepSentence(string? id, string key, bool host, bool canBuild, Participant person)
    {
        var s = SentenceData.Read(f => { var found = FindSentence(f, id); return CanChange(found, key, host, canBuild) ? SentenceStore.Copy(found) : throw new InvalidDataException("Only its maker or the host can keep this sentence."); });
        if (!host && !Keep.GuestKeep) throw new InvalidDataException("The host has not allowed guests to keep audio.");
        if (s.Versions.Count >= SentenceStore.MaxVersions) throw new InvalidDataException($"This sentence has {SentenceStore.MaxVersions} versions. Make a new sentence for more.");
        // One Keep at a time per guest (review, 2.42): each renders a whole sentence.
        if (!host && !keepingSentence.TryAdd(key, 0)) throw new InvalidDataException("Your last Keep is still being saved.");
        long reserved = 0;
        try
        {
            if (SentenceRenderHold is { } hold) await hold();
            // Each version pins the revisions of its sources: the recordings' ids, their files' content hashes, the frames.
            var library = Voices;
            var (rendered, wav, pins) = await RenderPieces(s.Pieces, s.Settings, result =>
            {
                var list = new List<PinnedSource>();
                foreach (var part in result.Parts)
                {
                    var r = library.Get(part.RecordingId) ?? throw new SentenceRender.MissingSourceException(part.PieceId, "A recording of this sentence left the voice library meanwhile.");
                    var fileHash = FileHash(r.Path) ?? throw new SentenceRender.MissingSourceException(part.PieceId, "A recording of this sentence could not be read.");
                    list.Add(new() { PieceId = part.PieceId, RecordingId = part.RecordingId, Hash = fileHash, StartFrame = part.StartFrame, EndFrame = part.EndFrame, Rate = part.Rate });
                }
                return (result, result.Audio.ToWav(), list);
            });
            string guestName = person.Name;
            // The allowance is checked and held in one step (review, 2.42), so Keeps at the same moment cannot all pass.
            if (!host)
            {
                if (budgets.ReserveKeep(key, guestName, wav.Length, Keep) is { } refusal) { Publish(); throw new InvalidDataException(refusal); }
                reserved = wav.Length;
            }
            int n = s.Versions.Count == 0 ? 1 : s.Versions[^1].N + 1;
            var (file, hash, created) = SentenceData.WriteAudio(s.Id, n, wav);
            try
            {
                SentenceData.Change(f =>
                {
                    var live = FindSentence(f, id);
                    if (live.Versions.Any(v => v.N == n)) throw new InvalidDataException("Someone kept this sentence at the same moment. Keep it again.");
                    live.Versions.Add(new SentenceVersion
                    {
                        N = n, At = utcNow(), By = key, ByName = person.Name, Text = s.Text, Pieces = s.Pieces, Settings = s.Settings, File = file, Hash = hash, Bytes = wav.Length,
                        DurationMs = (long)Math.Round(rendered.Audio.DurationMs), Sources = pins,
                    });
                    if (string.IsNullOrEmpty(live.Title)) live.Title = Clip(s.Text, 60);
                    live.Updated = utcNow();
                    return 0;
                }, !host);
            }
            catch
            {
                // Nothing records the file this Keep wrote (review, 2.42): it goes again, and nothing is charged.
                if (created) SentenceData.RemoveAudio(s.Id, file);
                throw;
            }
            if (!host) budgets.NoteKept(key, guestName, wav.Length);
            Log($"{person.Name} kept the sentence \"{Clip(s.Text, 60)}\" (version {n})");
            SentencesChanged();
            return new { version = n, durationMs = (long)Math.Round(rendered.Audio.DurationMs), revision = s.Revision };
        }
        finally
        {
            if (reserved > 0) budgets.ReleaseKeep(key, reserved);
            if (!host) keepingSentence.TryRemove(key, out _);
        }
    }

    // ---- Palette -------------------------------------------------------------------------------------------------

    private object PaletteSearch(Command c, SentenceInput input, string key)
    {
        var sets = PersonSetsOf(key); var active = sets.Sets.First(s => s.Id == sets.Active);
        var voices = (input.VoiceIds ?? [.. active.VoiceIds]).Where(SentenceStore.ValidId).ToHashSet(StringComparer.Ordinal);
        var filter = new TakeFilter(voices.Count == 0 ? null : voices, input.Altered == true);
        var corpus = Corpus(); var library = Voices;
        var query = (c.Query ?? "").Trim();
        var items = new List<object>();
        bool Favorite(string? recordingId, long start, long end, string? namedId) => active.Favorites.Any(f => namedId != null ? f.NamedId == namedId : f.RecordingId == recordingId && f.StartMs == start && f.EndMs == end);
        // Named nonverbal pieces whose name matches (or all of them, with nothing typed).
        var named = SentenceData.Read(f => f.Named.Select(n => (n.Id, n.Name, n.RecordingId, n.StartMs, n.EndMs)).ToList());
        string bare = query.Trim('[', ']').ToLowerInvariant();
        foreach (var n in named.Where(n => bare.Length == 0 || n.Name.Contains(bare, StringComparison.Ordinal)).Take(60))
        {
            var r = library.Get(n.RecordingId); if (r == null || !filter.Allows(r.VoiceId, AlteredTake(r))) continue;
            items.Add(new { kind = "named", text = "[" + n.Name + "]", namedId = n.Id, recordingId = (string?)null, startMs = n.StartMs, endMs = n.EndMs, voiceId = r.VoiceId, durationMs = n.EndMs - n.StartMs, favorite = Favorite(null, 0, 0, n.Id) });
        }
        var tokens = WordIndex.Query(query);
        if (tokens.Length > 0)
        {
            var words = new List<string[]> { tokens };
            if (tokens.Length == 1) words.AddRange(corpus.Vocabulary(filter).Where(p => p.Key.Length > tokens[0].Length && p.Key.StartsWith(tokens[0], StringComparison.Ordinal)).OrderByDescending(p => p.Value).Take(8).Select(p => new[] { p.Key }));
            foreach (var phrase in words)
                foreach (var group in SentenceComposer.Rank(corpus.Find(phrase, filter, 400)).GroupBy(t => t.VoiceId ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
                    foreach (var take in group.Take(3))
                    {
                        var source = SentenceComposer.SourceOf(take);
                        items.Add(new { kind = phrase.Length > 1 ? "phrase" : "word", text = take.Text, namedId = (string?)null, recordingId = (string?)take.RecordingId, startMs = source.StartMs, endMs = source.EndMs, voiceId = take.VoiceId, durationMs = source.EndMs - source.StartMs, favorite = Favorite(take.RecordingId, source.StartMs, source.EndMs, null) });
                    }
        }
        else
        {
            foreach (var f in active.Favorites.Where(f => f.NamedId == null))
            {
                var r = f.RecordingId == null ? null : library.Get(f.RecordingId); if (r == null) continue;
                items.Add(new { kind = "word", text = f.Text, namedId = (string?)null, recordingId = (string?)f.RecordingId, startMs = f.StartMs, endMs = f.EndMs, voiceId = r.VoiceId, durationMs = f.EndMs - f.StartMs, favorite = true });
            }
        }
        return new { items = items.Take(200).ToArray(), activeSet = active.Id };
    }

    private sealed class Performance { public long Started; public readonly List<(long At, PaletteRef Item)> Taps = []; }
    private readonly Dictionary<string, Performance> performances = new(StringComparer.Ordinal);

    /// <summary>A palette tap: played exactly (into the game, or privately), and noted in the tapper's own performance if one is recording.</summary>
    private async Task<Reply> PaletteTap(Command c, SentenceInput input, string actor, Participant? participant, string? requester, string? audience, Intent? intent, string key, bool host)
    {
        var item = input.Item ?? throw new InvalidDataException("Choose a palette item.");
        var (recordingId, start, end, namedId, label) = Resolve(item);
        var r = Voices.Get(recordingId) ?? throw new InvalidDataException("That recording is no longer in the voice library.");
        // Only this person's own taps go into their own recording; nobody else's ever do.
        lock (performances)
        {
            if (performances.TryGetValue(key, out var perf))
            {
                long at = utcNow() - perf.Started;
                if (at > MaxPerformanceMs || perf.Taps.Count >= MaxPerformanceTaps) { }
                else perf.Taps.Add((at, new PaletteRef { RecordingId = namedId == null ? recordingId : null, NamedId = namedId, StartMs = start, EndMs = end, Text = label }));
            }
        }
        if (c.Action == "game")
        {
            // A word into the game is a part of a recording: a guest also needs seeking, as for a region (review, 2.42).
            if (intent == null) throw new InvalidDataException(!disk.Permissions.RemotePlayback ? "Remote playback is disabled." : !disk.Permissions.GuestSeeking ? "Guest seeking is disabled, so palette words play only here." : "Guest playback permission or session authorization changed.");
            var source = await pcm.Get(r.Path);
            if (!IsNewest(intent)) return Superseded(c, "newer");
            long from = SliceRenderer.FrameOf(start, source.SampleRate), to = Math.Min(SliceRenderer.FrameOf(end, source.SampleRate), source.FrameCount);
            var wav = await RenderForGame(intent, cancel => Heavy("palette render", () => SliceRenderer.Render(source, from, to, new SliceOptions { FadeInMs = 3, FadeOutMs = 5 }, cancel), cancel));
            if (wav == null) return Superseded(c, "newer");
            return await PlayInGame(c, wav, "palette:" + recordingId, actor, participant, requester, audience, intent, true, new { text = label });
        }
        if (!host && !previewing.TryAdd(key, 0)) throw new InvalidDataException("Your last preview is still being made.");
        try
        {
            if (!host && !paletteRate.TryTake(key)) throw new InvalidDataException("Too many palette previews. Wait a moment and try again.");
            var wav = await Heavy("palette preview", () =>
            {
                var (mono, rate) = AudioFile.ReadMono(r.Path, start, end);
                if (mono.Length == 0) throw new InvalidDataException("Nothing to hear there.");
                if (!host && rate > GuestPreviewRate) { mono = MimicTranscriber.Resample(mono, rate, GuestPreviewRate); rate = GuestPreviewRate; }
                return MimicRing.Wav(mono, rate);
            });
            return Ok(c, new { wav = Convert.ToBase64String(wav), text = label });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException) { throw new InvalidDataException("That sound file could not be read."); }
        finally { if (!host) previewing.TryRemove(key, out _); }
    }

    private object MarkPiece(Command c, SentenceInput input, string key, Participant person, bool canBuild)
    {
        if (!canBuild) throw new InvalidDataException("The host has not allowed guests to edit the voice library.");
        if (c.Action == "remove")
        {
            var id = input.Item?.NamedId;
            SentenceData.Change(f =>
            {
                var n = f.Named.FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException("That named piece is no longer there.");
                if (n.By != key && key != "host") throw new InvalidDataException("Only who named it or the host can remove it.");
                f.Named.Remove(n); return 0;
            }, key != "host");
            SentencesChanged();
            return new { removed = true };
        }
        var name = (c.Name ?? "").Trim().Trim('[', ']').Trim().ToLowerInvariant();
        if (!SentenceStore.NamePattern.IsMatch(name)) throw new InvalidDataException("Name it with a word or two, like laugh, gasp or breath.");
        var r = Voices.Get(c.RecordingId ?? "") ?? throw new InvalidDataException("That recording is no longer in the voice library.");
        long start = input.StartMs ?? -1, end = input.EndMs ?? -1;
        if (start < 0 || end <= start + 20 || end - start > 30000 || r.DurationMs > 0 && end > r.DurationMs + 50) throw new InvalidDataException("Mark a part of the recording (up to 30 s).");
        var piece = new NamedPiece { Id = SentenceStore.NewId(), Name = name, RecordingId = r.Id, StartMs = start, EndMs = end, By = key, ByName = person.Name, Created = utcNow() };
        // Guests leave room for the host's own named pieces (review, 2.42).
        bool host = key == "host";
        SentenceData.Change(f => { if (f.Named.Count >= SentenceStore.MaxNamed - (host ? 0 : HostReserveNamed)) throw new InvalidDataException("There are as many named pieces as the library can hold."); f.Named.Add(piece); return 0; }, !host);
        Log($"{person.Name} named a piece [{name}]");
        SentencesChanged();
        return new { namedId = piece.Id, name };
    }

    /// <summary>Record performance: start, stop (an editable take, opened as a sentence) or cancel. Only the asker's own taps.</summary>
    private object PerformanceCommand(Command c, string key, Participant person, bool canBuild)
    {
        switch (c.Action)
        {
            case "start":
                lock (performances) performances[key] = new Performance { Started = utcNow() };
                return new { recording = true, taps = 0 };
            case "cancel":
                lock (performances) performances.Remove(key);
                return new { recording = false };
            case "status":
            case null:
                lock (performances) return performances.TryGetValue(key, out var p) ? new { recording = true, taps = p.Taps.Count, sinceMs = utcNow() - p.Started } : (object)new { recording = false, taps = 0, sinceMs = 0L };
            case "stop":
                {
                    Performance? perf; List<(long At, PaletteRef Item)> taps;
                    lock (performances) { performances.TryGetValue(key, out perf); taps = perf == null ? [] : [.. perf.Taps]; }
                    if (perf == null) throw new InvalidDataException("No performance is being recorded.");
                    void End() { lock (performances) if (performances.TryGetValue(key, out var now) && ReferenceEquals(now, perf)) performances.Remove(key); }
                    if (taps.Count == 0) { End(); throw new InvalidDataException("Nothing was tapped while recording."); }
                    if (!canBuild) { End(); throw new InvalidDataException("The host has not allowed guests to build sentences."); }
                    var project = TakeFromPerformance(taps, key, person);
                    // The recording ends only once its take is saved (review, 2.42): a take that cannot be saved (the list is
                    // full) leaves the performance recording, so Stop can be pressed again after making room.
                    bool host = key == "host";
                    SentenceData.Change(f =>
                    {
                        SweepGuestDrafts(f);
                        RequireRoom(f, key, host);
                        f.Sentences.Add(project); return 0;
                    }, !host);
                    End();
                    SentencesChanged();
                    return new { sentenceId = project.Id, taps = taps.Count };
                }
            default: throw new InvalidDataException("Start, stop or cancel recording a performance.");
        }
    }

    /// <summary>
    /// A performance as an editable take: each tap a hand-placed piece at its time, the text its labels in order. Taps
    /// whose labels no longer fit the text (<see cref="SentenceStore.MaxText"/> characters, <see cref="SentenceStore.MaxTokens"/>
    /// words) stay in the take as pieces without words of their own (review, 2.42: a long take used to fail to save).
    /// </summary>
    private SentenceProject TakeFromPerformance(List<(long At, PaletteRef Item)> taps, string key, Participant person)
    {
        var pieces = new List<SentencePiece>(); var words = new List<string>(); int token = 0, length = 0;
        long first = taps[0].At;
        foreach (var (at, item) in taps)
        {
            SentencePiece piece;
            try
            {
                var label = Resolve(item).Label;
                int count = SentenceComposer.Tokens(label).Count;
                bool fits = token + count <= SentenceStore.MaxTokens && length + label.Length + (words.Count > 0 ? 1 : 0) <= SentenceStore.MaxText;
                piece = PieceFromItem(item, at - first, fits && count > 0 ? token : null, fits && count > 0 ? token + count : null);
                if (fits) { token += count; length += label.Length + (words.Count > 0 ? 1 : 0); words.Add(label); }
            }
            catch (InvalidDataException) { continue; } // that item left the library since the tap
            pieces.Add(piece);
        }
        if (pieces.Count == 0) throw new InvalidDataException("The tapped items are no longer in the voice library.");
        return new SentenceProject
        {
            Id = SentenceStore.NewId(), Owner = key, OwnerName = person.Name, Created = utcNow(), Updated = utcNow(), Origin = "performance",
            Title = "Performance " + DateTimeOffset.FromUnixTimeMilliseconds(utcNow()).ToLocalTime().ToString("HH:mm"), Text = string.Join(' ', words), Pieces = pieces,
            Settings = new SentenceSettings { Seed = RandomNumberGenerator.GetInt32(int.MaxValue) },
        };
    }

    // ---- Performance sets ----------------------------------------------------------------------------------------

    private PersonSets PersonSetsOf(string key) => SentenceData.Read(f => f.People.FirstOrDefault(p => p.Owner == key) is { } found ? SentenceStore.Copy(found) : null)
        ?? new PersonSets { Owner = key, Sets = [new PerformanceSet()] };

    private object PerformanceSets(Command c, string key)
    {
        var input = c.Set ?? new PerformanceSetInput();
        if (c.Action is "create" or "update" or "delete" or "activate")
        {
            SentenceData.Change(f =>
            {
                var mine = f.People.FirstOrDefault(p => p.Owner == key);
                if (mine == null)
                {
                    // The last entry stays the host's (review, 2.42).
                    if (f.People.Count >= SentenceStore.MaxPeople - (key == "host" ? 0 : 1)) throw new InvalidDataException("Too many people have performance sets.");
                    f.People.Add(mine = new PersonSets { Owner = key, Sets = [new PerformanceSet()] });
                }
                PerformanceSet Find() => mine.Sets.FirstOrDefault(s => s.Id == input.Id) ?? throw new InvalidDataException("That performance set is no longer there.");
                switch (c.Action)
                {
                    case "create":
                        {
                            if (mine.Sets.Count >= SentenceStore.MaxSets) throw new InvalidDataException($"You have {SentenceStore.MaxSets} performance sets.");
                            var set = new PerformanceSet { Id = SentenceStore.NewId(), Name = Clip(input.Name ?? "", 40) is { Length: > 0 } n ? n : "New set" };
                            Apply(set, input); mine.Sets.Add(set); mine.Active = set.Id;
                            break;
                        }
                    case "update": { var set = Find(); if (input.Name != null) set.Name = Clip(input.Name, 40) is { Length: > 0 } n ? n : set.Name; Apply(set, input); break; }
                    case "delete":
                        {
                            var set = Find();
                            if (set.Id == SentenceStore.DefaultSet) throw new InvalidDataException("The default set stays.");
                            mine.Sets.Remove(set); if (mine.Active == set.Id) mine.Active = SentenceStore.DefaultSet;
                            break;
                        }
                    case "activate": mine.Active = Find().Id; break;
                }
                return 0;
            }, key != "host");
            SentencesChanged();
        }
        var sets = PersonSetsOf(key);
        return new { active = sets.Active, sets = sets.Sets };
    }
    private static void Apply(PerformanceSet set, PerformanceSetInput input)
    {
        // References only: voice ids, ranges of library recordings and named pieces; never audio.
        if (input.VoiceIds != null) set.VoiceIds = [.. input.VoiceIds.Where(SentenceStore.ValidId).Distinct().Take(256)];
        if (input.Favorites != null) set.Favorites = [.. input.Favorites.Take(SentenceStore.MaxFavorites).Select(f => new PaletteRef { RecordingId = f.NamedId == null ? f.RecordingId : null, NamedId = f.NamedId, StartMs = f.NamedId == null ? f.StartMs : 0, EndMs = f.NamedId == null ? f.EndMs : 0, Text = Clip(f.Text ?? "", 100) })];
        if (input.Blocks != null)
        {
            // Phrase blocks (2.45): words and an optional voice, each with a name unique in the set (a template uses it as {Name:block}).
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ids = new HashSet<string>(StringComparer.Ordinal);
            var blocks = new List<PhraseBlock>();
            foreach (var b in input.Blocks.Take(SentenceStore.MaxBlocks + 1))
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Text)) continue;
                if (blocks.Count >= SentenceStore.MaxBlocks) throw new InvalidDataException($"A set holds at most {SentenceStore.MaxBlocks} phrase blocks.");
                var name = Clip(b.Name ?? "", 40) is { Length: > 0 } n ? n : "Phrase";
                if (!names.Add(name)) throw new InvalidDataException($"Another phrase block is called “{name}”.");
                if (b.VoiceId != null && !SentenceStore.ValidId(b.VoiceId)) throw new InvalidDataException("Choose a voice from the library.");
                blocks.Add(new PhraseBlock { Id = SentenceStore.ValidId(b.Id) && ids.Add(b.Id) ? b.Id : SentenceStore.NewId(), Name = name, Text = Clip(b.Text, 400), VoiceId = b.VoiceId });
            }
            set.Blocks = blocks.Count > 0 ? blocks : null;
        }
        if (input.Templates != null) set.Templates = [.. input.Templates.Where(t => !string.IsNullOrWhiteSpace(t.Text)).Take(SentenceStore.MaxTemplates).Select(t => new SentenceTemplate { Id = SentenceStore.ValidId(t.Id) ? t.Id : SentenceStore.NewId(), Name = Clip(t.Name ?? "", 40) is { Length: > 0 } n ? n : "Template", Text = Clip(t.Text ?? "", 400) })];
    }
}
