using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
namespace SoundpadWeb;

/// <summary>A recording as everyone sees it (no file path).</summary>
public sealed record RecordingView(string Id, string Title, string? VoiceId, string[] Tags, bool Favorite, string? CorrectedText, bool Reviewed, string WordsState, string? WordsError,
    long DurationMs, long Created, long Updated, string Source, string? SessionId, string ByName, string? SoundId, string Text, int WordCount);
/// <summary>A quiet "possible match" for a wanted word (2.41): in a new recording, or in the live history (with a jump marker).</summary>
public sealed record LibraryNotice(long Id, string Kind, string VoiceId, string VoiceName, string Text, string? RecordingId, string? SessionId, long? StartFrame, long? EndFrame, long At);

/// <summary>Reads a sound file as mono samples (read only; the file is never written).</summary>
internal static class AudioFile
{
    public const int MaxSeconds = 600;
    public static (float[] Mono, int Rate) ReadMono(string path, long fromMs = 0, long? toMs = null, int maxSeconds = MaxSeconds)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidDataException("The sound file is missing.");
        if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("The sound file is larger than 512 MiB.");
        using var audio = new DecodedAudio(path);
        if (toMs == null && audio.Duration.TotalSeconds > maxSeconds + 1) throw new InvalidDataException($"The sound is longer than {maxSeconds / 60} minutes.");
        int channels = audio.Channels, rate = audio.Rate;
        long skip = fromMs * rate / 1000, keep = Math.Min((toMs ?? long.MaxValue / 2) - fromMs, (long)maxSeconds * 1000) * rate / 1000;
        var mono = new List<float>((int)Math.Min(keep, (long)rate * 60));
        // The first channel too: channels in opposite phase cancel out in the mix (a "wide" stereo effect), and then
        // the first channel alone is what can be heard and recognised.
        var first = channels > 1 ? new List<float>(mono.Capacity) : null;
        double mixEnergy = 0, firstEnergy = 0;
        var buffer = new float[channels * 4096]; int read; long frame = 0;
        while (keep > 0 && (read = audio.Read(buffer)) > 0)
            for (int i = 0; i + channels <= read && keep > 0; i += channels, frame++)
            {
                if (frame < skip) continue;
                float sum = 0;
                for (int k = 0; k < channels; k++) sum += buffer[i + k];
                float mixed = sum / channels;
                mono.Add(mixed); keep--;
                mixEnergy += mixed * mixed;
                if (first != null) { first.Add(buffer[i]); firstEnergy += buffer[i] * buffer[i]; }
            }
        return (first != null && mixEnergy < 0.1 * firstEnergy ? first.ToArray() : mono.ToArray(), rate);
    }
}

// The voice library, its word search, the Soundpad category word index, wanted-word notices, history search and
// speaker grouping (2.41). See VoiceLibrary.cs, WordIndex.cs, SoundWords.cs and SpeakerGrouping.cs.
public sealed partial class Bridge
{
    private readonly WordIndex wordIndex = new();
    private VoiceLibrary? voiceLibrary;
    private SoundWords? soundWords;
    private readonly object voiceInit = new();
    private long voicesRevision;
    /// <summary>Where Temp clips of this launch came from, so keeping one records its provenance.</summary>
    private readonly ConcurrentDictionary<string, LibrarySource> provenance = new();
    /// <summary>Changes to the voice library per minute, per guest (2.41 review).</summary>
    public const int GuestLibraryWritesPerMinute = 60;
    private RateWindow libraryWriteRate = new(GuestLibraryWritesPerMinute, TimeSpan.FromMinutes(1));
    /// <summary>Test seam: another limit for guests' library changes.</summary>
    internal void UseLibraryWriteLimit(RateWindow limit) => libraryWriteRate = limit;

    private VoiceLibrary Voices
    {
        get
        {
            lock (voiceInit)
            {
                if (voiceLibrary != null) return voiceLibrary;
                // The sound index shares the word index; both are loaded together so a search sees everything.
                soundWords ??= new SoundWords(store.DirectoryPath, wordIndex);
                var loaded = new VoiceLibrary(store.DirectoryPath, wordIndex, () => utcNow());
                loaded.Changed += VoicesChanged;
                Volatile.Write(ref voiceLibrary, loaded);
                if (loaded.PendingCount > 0 || loaded.Settings.IndexCategories.Count > 0) _ = Task.Run(SignalWorker);
                return loaded;
            }
        }
    }
    private SoundWords SoundIndex { get { _ = Voices; return soundWords!; } }
    /// <summary>Test seam: the library and the category index.</summary>
    public VoiceLibrary VoiceLibraryStore => Voices;
    public SoundWords SoundWordStore => SoundIndex;
    private void VoicesChanged() { Interlocked.Increment(ref voicesRevision); Publish(); }
    /// <summary>
    /// The library's settings for the snapshot and the history's word handlers (2.41 review): never waits for the
    /// library. Its lock is held across a whole rewrite of its file, and its first load reads every library file; the
    /// snapshot is made on the receive loop, ahead of Stop and Panic. Until the library has loaded (in the background,
    /// started here) this is null, and the snapshot then says nothing about guest library editing.
    /// </summary>
    private LibrarySettings? LibrarySettingsNow
    {
        get
        {
            if (Volatile.Read(ref voiceLibrary) is { } library) return library.Settings;
            if (Interlocked.Exchange(ref voicesLoading, 1) == 0 && Volatile.Read(ref disposed) == 0)
                _ = Task.Run(() =>
                {
                    try { _ = Voices; Publish(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { Interlocked.Exchange(ref voicesLoading, 0); }
                });
            return null;
        }
    }
    private int voicesLoading;

    /// <summary>The sound id Soundpad's list gives a file (as LibraryParser makes it for a path's first entry).</summary>
    private string FirstSoundIdOf(string path) => Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(disk.IdentityKey), Encoding.UTF8.GetBytes(path.Replace('/', '\\').ToUpperInvariant() + "\n0"))).ToLowerInvariant();
    private string? PlayableSoundId(string path) { var id = FirstSoundIdOf(path); return parsed.Indexes.ContainsKey(id) ? id : null; }

    private RecordingView View(LibraryRecording r)
    {
        var words = Voices.DisplayWords(r.Id);
        var text = r.CorrectedText ?? string.Join(" ", words.Select(w => w.Text));
        return new(r.Id, r.Title, r.VoiceId, [.. r.Tags], r.Favorite, r.CorrectedText, r.Reviewed, r.WordsState, r.WordsError, r.DurationMs, r.Created, r.Updated, r.Source.Kind, r.Source.SessionId,
            r.ByName, PlayableSoundId(r.Path), text.Length > 300 ? text[..299] + "…" : text, words.Length);
    }

    // ---- Provenance and Keep -------------------------------------------------------------------------------------

    private void NoteProvenance(string assetId, LibrarySource source)
    {
        if (provenance.Count > 4096) provenance.Clear();
        provenance[assetId] = source;
    }

    /// <summary>The history words of [start, end) as library words (ms from start), when that stretch is fully recognised.</summary>
    private List<LibraryWord>? WordsFromHistory(LibrarySource source)
    {
        var h = history;
        if (source.Kind != "history" || h == null || source.SessionId != h.SessionId || source.StartFrame is not long start || source.EndFrame is not long end) return null;
        try
        {
            if (transcriber.PendingBetween(h, [new HistoryRange(start, end)], start, end).Count > 0) return null;
            var list = new List<LibraryWord>();
            foreach (var (w, fix) in transcriber.WordsBetween(h, HistoryAccess.Host, start, end))
            {
                long middle = (w.Start + w.End) / 2;
                if (middle < start || middle >= end) continue;
                long s = Math.Max(0, (w.Start - start) * 1000 / h.Rate), e = Math.Min((end - start) * 1000 / h.Rate, (w.End - start) * 1000 / h.Rate);
                if (e <= s) continue;
                list.Add(new(s, e, (fix?.Text ?? w.Text).Trim() is { Length: > 0 } t ? (t.Length > 200 ? t[..200] : t) : w.Text, w.Probability, w.Status == WordStatus.Uncertain));
            }
            return list;
        }
        catch (HistoryException) { return null; }
    }

    private static long DurationOf(string path)
    {
        try { using var reader = new NAudio.Wave.WaveFileReader(path); return (long)reader.TotalTime.TotalMilliseconds; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return 0; }
    }

    /// <summary>
    /// Every kept clip becomes a library recording (2.41): called after a Keep (host, guest, draft, quit prompt). Never
    /// fails the Keep itself; a problem is logged and the clip can be added later with "Add to voice library".
    /// </summary>
    private LibraryRecording? LibraryFromKeep(string assetId, string by, string byName, LibrarySource? source = null, IReadOnlyList<LibraryWord>? words = null)
    {
        try
        {
            var asset = Audio.AssetById(assetId);
            var variant = asset.Variants.LastOrDefault(v => v.Permanent && v.Current) ?? asset.Variants.LastOrDefault(v => v.Permanent);
            if (variant == null) { libraryKeepProblem[assetId] = "The clip is not kept yet."; return null; }
            source ??= provenance.GetValueOrDefault(assetId) ?? new LibrarySource { Kind = asset.Origin == "native" ? "soundpad" : "upload" };
            words ??= WordsFromHistory(source);
            var (recording, added) = Voices.Add(new LibraryRecording { AssetId = asset.Id, Path = variant.Path, Bytes = variant.Bytes, DurationMs = DurationOf(variant.Path), Title = asset.Name, Source = source }, words, by, byName);
            if (added)
            {
                Log($"{byName} kept \"{recording.Title}\" in the voice library");
                if (recording.WordsState == "ready") CheckWantedRecording(recording.Id); else SignalWorker();
            }
            return recording;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("A kept clip could not be added to the voice library: " + ex.Message);
            if (libraryKeepProblem.Count > 256) libraryKeepProblem.Clear();
            libraryKeepProblem[assetId] = ex.Message;
            return null;
        }
    }
    /// <summary>Why the last <see cref="LibraryFromKeep"/> of a clip added nothing (for "Keep in voice library" to say so).</summary>
    private readonly ConcurrentDictionary<string, string> libraryKeepProblem = new();

    // ---- Commands ------------------------------------------------------------------------------------------------

    private static bool IsLibraryOp(string op) => op.StartsWith("library", StringComparison.Ordinal);
    public const int MaxLibraryPage = 100;

    private LibraryFilter FilterOf(Command c) => new(VoiceId: string.IsNullOrEmpty(c.VoiceId) ? null : c.VoiceId, Unassigned: c.Unassigned == true, Tag: string.IsNullOrWhiteSpace(c.Tag) ? null : c.Tag.Trim(), Favorite: c.Favorite == true ? true : null, Reviewed: c.Reviewed, Text: c.Query);

    /// <summary>The 2.41 voice-library commands. Null when the op is not one of them. They read files and search, so they run off the receive loop.</summary>
    private async Task<Reply?> ExecuteVoices(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        if (!IsLibraryOp(c.Op)) return null;
        return await Task.Run(() => VoicesCommand(c, actor, participant, requester, audience));
    }

    private Reply VoicesCommand(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && (audioAudience != null && (!sessionAccepting || audience != audioAudience) || requester != null && revokedAudioPeers.ContainsKey(requester))) throw new InvalidDataException("This guest session authorization changed.");
            var library = Voices;
            var person = Person(actor, participant); string by = PersonKey(actor, participant, requester);
            bool canEdit = host || library.Settings.GuestLibraryEdit;
            void RequireEdit()
            {
                if (!canEdit) throw new InvalidDataException("The host has not allowed guests to edit the voice library.");
                // Every change rewrites the library file and adds to its audit trail (2.41 review): a guest's pace is limited.
                if (!host && !libraryWriteRate.TryTake(by)) throw new InvalidDataException("Too many changes to the voice library. Wait a minute and try again.");
            }
            void RequireHost() { if (!host) throw new InvalidDataException("Only the host can change that."); }
            string[] Ids() => c.RecordingIds is { Length: > 0 } many ? many : c.RecordingId is { } one ? [one] : throw new InvalidDataException("Choose a recording.");
            switch (c.Op)
            {
                case "libraryStatus": return Ok(c, Status(host, actor, participant, requester, audience, canEdit));
                case "libraryList":
                    {
                        var (total, items) = library.List(FilterOf(c), c.Offset ?? 0, Math.Clamp(c.Limit ?? 50, 1, MaxLibraryPage));
                        return Ok(c, new { total, offset = c.Offset ?? 0, items = items.Select(View).ToArray() });
                    }
                case "libraryGet":
                    {
                        var r = library.Get(c.RecordingId ?? "") ?? throw new InvalidDataException("That recording is no longer in the voice library.");
                        var words = library.DisplayWords(r.Id).Select(w => new { s = w.StartMs, e = w.EndMs, t = w.Text, u = w.Uncertain }).ToArray();
                        var history = library.Audit(r.Id, 20).Select(a => AuditView(a)).ToArray();
                        return Ok(c, new { recording = View(r), words, audit = history });
                    }
                case "librarySearch": return Ok(c, Search(c));
                case "libraryVocabulary": return Ok(c, Vocabulary(c));
                case "libraryAudit": return Ok(c, new { entries = library.Audit(c.RecordingId, Math.Clamp(c.Limit ?? 50, 1, 200)).Select(AuditView).ToArray() });
                case "libraryEdit":
                    {
                        RequireEdit();
                        var ids = Ids();
                        var edit = new LibraryEdit(c.Title, string.IsNullOrEmpty(c.VoiceId) ? null : c.VoiceId, c.VoiceId == "", c.Tags, c.AddTags, c.RemoveTags, c.Favorite, c.CorrectedText, c.Reviewed);
                        long group = library.Edit(ids, edit, by, person.Name);
                        if (c.CorrectedText != null) foreach (var id in ids) library.Reindex(id);
                        if (c.VoiceId is { Length: > 0 }) foreach (var id in ids) CheckWantedRecording(id);
                        return Ok(c, new { group });
                    }
                case "libraryRemove": RequireEdit(); return Ok(c, new { group = library.Remove(Ids(), by, person.Name) });
                case "libraryUndo":
                    {
                        RequireEdit();
                        long group = library.Undo(c.AuditId ?? throw new InvalidDataException("Choose a change to undo."), by, person.Name, host);
                        return Ok(c, new { group });
                    }
                case "libraryVoice":
                    {
                        RequireEdit();
                        switch (c.Action)
                        {
                            case "create": { var (voice, group) = library.CreateVoice(c.Name ?? "", c.Color, by, person.Name); return Ok(c, new { voice, group }); }
                            case "update": return Ok(c, new { group = library.UpdateVoice(c.VoiceId ?? "", c.Name, c.Color, by, person.Name) });
                            case "delete": return Ok(c, new { group = library.DeleteVoice(c.VoiceId ?? "", by, person.Name) });
                            default: throw new InvalidDataException("Create, rename or delete a voice.");
                        }
                    }
                case "libraryWanted":
                    {
                        RequireEdit();
                        if (c.Action == "add")
                        {
                            var (wanted, group) = library.AddWanted(c.VoiceId ?? "", c.Query ?? "", by, person.Name);
                            return Ok(c, new { wanted, group });
                        }
                        if (c.Action == "remove") return Ok(c, new { group = library.RemoveWanted(c.RecordingId ?? c.Name ?? "", by, person.Name) });
                        throw new InvalidDataException("Add or remove a wanted word.");
                    }
                case "libraryAdd":
                    {
                        RequireEdit();
                        if (c.SoundId == null || !parsed.Paths.TryGetValue(c.SoundId, out var path)) throw new InvalidDataException("That sound is no longer in Soundpad. Refresh and choose it again.");
                        var sound = parsed.Public.Sounds.First(s => s.Id == c.SoundId);
                        if (library.FindByPath(path) is { } existing) return Ok(c, new { recordingId = existing.Id, added = false });
                        // A guest adds only what they could already hear privately (2.41 review): a recording is previewable
                        // by everyone, so any other Soundpad sound would hand them its audio.
                        if (!host && !GuestPreviewable(path)) throw new InvalidDataException("Only the host can add that sound to the voice library.");
                        var source = sound.AssetId != null && provenance.TryGetValue(sound.AssetId, out var known) ? known : new LibrarySource { Kind = "soundpad", SoundId = c.SoundId };
                        var indexed = SoundIndex.Get(path) is { Words: { } list } entry && new FileInfo(path) is { Exists: true } info && entry.Size == info.Length && entry.Modified == info.LastWriteTimeUtc.Ticks ? list : null;
                        var (recording, added) = library.Add(new LibraryRecording { AssetId = sound.AssetId, Path = path, Bytes = File.Exists(path) ? new FileInfo(path).Length : 0, DurationMs = sound.DurationMs ?? DurationOf(path), Title = sound.Name, Source = source }, (IReadOnlyList<LibraryWord>?)indexed ?? WordsFromHistory(source), by, person.Name);
                        if (recording.WordsState == "pending") SignalWorker(); else if (added) CheckWantedRecording(recording.Id);
                        return Ok(c, new { recordingId = recording.Id, added });
                    }
                case "libraryRetry": RequireEdit(); library.Retranscribe(c.RecordingId ?? ""); SignalWorker(); return Ok(c, new { });
                case "libraryPreview": return Ok(c, LibraryPreview(c, actor, participant, requester));
                case "librarySettings":
                    {
                        RequireHost();
                        var next = library.UpdateSettings(s => s with { GroupSpeakers = c.GroupSpeakers ?? s.GroupSpeakers, GuestLibraryEdit = c.GuestLibraryEdit ?? s.GuestLibraryEdit });
                        if (!next.GroupSpeakers) lock (speakerGate) speakers = null;
                        return Ok(c, new { settings = next });
                    }
                case "libraryIndex":
                    {
                        RequireHost();
                        var names = c.Categories?.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                        var next = library.UpdateSettings(s => s with
                        {
                            IndexCategories = names ?? s.IndexCategories,
                            IndexPaused = c.Action == "pause" ? true : c.Action == "resume" ? false : s.IndexPaused,
                        });
                        lock (workerGate) categoryQueue = null;
                        if (!next.IndexPaused) SignalWorker();
                        Log(c.Action == "pause" ? "Paused the word index of Soundpad categories" : names != null ? $"Word index: {names.Count} Soundpad categor{(names.Count == 1 ? "y" : "ies")} chosen" : "Resumed the word index");
                        return Ok(c, IndexProgress(true));
                    }
            }
            throw new InvalidDataException("Unsupported command.");
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new("ack", c.Id, false, "The voice library could not read or save its files: " + ex.Message); }
    }
    private static Reply Ok(Command c, object data) => new("ack", c.Id, true, Data: data);
    private static object AuditView(LibraryAudit a) => new { a.Id, a.Group, a.At, a.ByName, a.Target, a.TargetId, a.Field, Before = PublicRecord(a, a.Before), After = PublicRecord(a, a.After), a.UndoOf };
    /// <summary>
    /// A whole recording or wanted word in the audit (2.41 review): its 'record' entries keep everything for Undo, on
    /// this PC only. What is sent says only which one it was: never the file path (with the Windows user name), the
    /// asset, the provenance or who added it (a participant id).
    /// </summary>
    private static string? PublicRecord(LibraryAudit a, string? json)
    {
        if (json == null || a.Field != "record" || a.Target is not ("recording" or "wanted")) return json;
        string[] keep = a.Target == "recording" ? ["id", "title"] : ["id", "voiceId", "text"];
        var result = new System.Text.Json.Nodes.JsonObject();
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is System.Text.Json.Nodes.JsonObject whole)
                foreach (var name in keep) if (whole[name] is { } value) result[name] = value.DeepClone();
        }
        catch (System.Text.Json.JsonException) { }
        return result.ToJsonString();
    }

    private object Status(bool host, string actor, Participant? participant, string? requester, string? audience, bool canEdit)
    {
        var library = Voices;
        var counts = library.VoiceCounts();
        // Which recordings each voice has, taken once per voice (not once per wanted word).
        var byVoice = new Dictionary<string, Func<string, bool>>();
        var wanted = library.Wanted.Select(w =>
        {
            if (!byVoice.TryGetValue(w.VoiceId, out var include)) byVoice[w.VoiceId] = include = library.Include(new LibraryFilter(VoiceId: w.VoiceId));
            int found = wordIndex.Find(WordIndex.Query(w.Text), include, 100).Count;
            return new { w.Id, w.VoiceId, w.Text, w.ByName, available = found > 0, count = found };
        }).ToArray();
        var s = library.Settings;
        return new
        {
            revision = Interlocked.Read(ref voicesRevision),
            blocked = library.Blocked,
            problem = host ? library.Problem : library.Blocked ? "The host's voice library could not be read." : null,
            count = library.Count,
            unreviewed = library.UnreviewedCount,
            pending = library.PendingCount,
            voices = library.Voices.Select(v => new { v.Id, v.Name, v.Color, count = counts.GetValueOrDefault(v.Id) }).ToArray(),
            unassigned = counts.GetValueOrDefault(""),
            tags = library.AllTags(),
            wanted,
            canEdit,
            settings = new { s.GuestLibraryEdit, s.GroupSpeakers, indexCategories = host ? s.IndexCategories : null, s.IndexPaused },
            index = IndexProgress(host),
            worker = workerStatus,
            notices = Notices(actor, participant, requester, audience),
        };
    }

    // ---- Search and vocabulary ----------------------------------------------------------------------------------

    /// <summary>
    /// The sound files of the categories indexed now, still in Soundpad: document key → (sound id, title). Host paths
    /// stay here. A category taken out of the index (or a sound moved out of one) is no longer searched or previewed
    /// (2.41 review), though its words stay on file in case it is chosen again.
    /// </summary>
    private Dictionary<string, (string SoundId, string Title)> IndexedSounds()
    {
        var catalog = parsed; var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var settings = Voices.Settings;
        if (settings.IndexCategories.Count == 0) return result;
        var selected = SelectedSoundFiles(settings).Select(f => SoundWords.Key(f.Path)).ToHashSet(StringComparer.Ordinal);
        var titles = catalog.Public.Sounds.ToDictionary(s => s.Id, s => s.Name);
        foreach (var (id, path) in catalog.Paths)
            if (selected.Contains(SoundWords.Key(path))) result.TryAdd(SoundWords.Key(path), (id, titles.GetValueOrDefault(id) ?? System.IO.Path.GetFileNameWithoutExtension(path)));
        return result;
    }
    /// <summary>A Soundpad sound guests may hear privately or add to the library: indexed, in a category indexed now.</summary>
    private bool GuestPreviewable(string path) => SoundIndex.Has(path) && IndexedSounds().ContainsKey(SoundWords.Key(path));

    private object Search(Command c)
    {
        var tokens = WordIndex.Query(c.Query);
        if (tokens.Length == 0) throw new InvalidDataException("Type a word or a phrase to search for.");
        var library = Voices; var filter = FilterOf(c) with { Text = null };
        string kind = c.Kind ?? "all";
        bool clips = kind is "all" or "clip", sounds = kind is "all" or "sound";
        // A voice, tag or favourite filter is about recordings; Soundpad sounds have none of those.
        if (filter.VoiceId != null || filter.Tag != null || filter.Favorite != null || filter.Unassigned) sounds = false;
        var include = library.Include(filter);
        var soundMap = sounds ? IndexedSounds() : [];
        var hits = wordIndex.Find(tokens, key => clips && VoiceLibrary.IsRecordingKey(key) && include(key) || sounds && SoundWords.IsSoundKey(key) && soundMap.ContainsKey(key[2..]), 20000);
        var results = new List<(long Order, object Result)>();
        foreach (var doc in hits.GroupBy(h => h.Key))
        {
            int at = doc.Min(h => h.Position), count = doc.Count();
            if (VoiceLibrary.IsRecordingKey(doc.Key))
            {
                var r = library.Get(VoiceLibrary.RecordingOf(doc.Key)); if (r == null) continue;
                var words = library.DisplayWords(r.Id);
                results.Add((r.Created, new { type = "clip", id = r.Id, soundId = PlayableSoundId(r.Path), title = r.Title, voiceId = r.VoiceId, durationMs = r.DurationMs, matches = count, match = at, length = tokens.Length, context = Context(words, at, tokens.Length) }));
            }
            else
            {
                var key = doc.Key[2..]; var (soundId, title) = soundMap[key];
                var entry = SoundIndex.ByDocKey(doc.Key); if (entry?.Words == null) continue;
                results.Add((long.MinValue / 2, new { type = "sound", id = soundId, soundId = (string?)soundId, title, voiceId = (string?)null, durationMs = entry.Words.Length > 0 ? entry.Words[^1].EndMs : 0, matches = count, match = at, length = tokens.Length, context = Context(entry.Words, at, tokens.Length) }));
            }
        }
        // Titles and corrected text match too, without a timed word to play from.
        if (clips)
        {
            var phrase = string.Join(' ', tokens); var seen = hits.Select(h => h.Key).ToHashSet();
            foreach (var r in library.TextMatches(phrase, filter, 500))
                if (!seen.Contains("r:" + r.Id))
                    results.Add((r.Created, new { type = "clip", id = r.Id, soundId = PlayableSoundId(r.Path), title = r.Title, voiceId = r.VoiceId, durationMs = r.DurationMs, matches = 0, match = -1, length = 0, context = Array.Empty<object>() }));
        }
        var ordered = results.OrderByDescending(r => r.Order).Select(r => r.Result).ToList();
        int offset = Math.Max(0, c.Offset ?? 0), limit = Math.Clamp(c.Limit ?? 30, 1, MaxLibraryPage);
        return new { total = ordered.Count, offset, results = ordered.Skip(offset).Take(limit).ToArray(), sentences = Array.Empty<object>() };
    }

    /// <summary>Up to six words either side of a match, with their times, so the page can play any of them.</summary>
    private static object[] Context(LibraryWord[] words, int at, int length)
    {
        int from = Math.Max(0, at - 6), to = Math.Min(words.Length, at + length + 6);
        return [.. Enumerable.Range(from, to - from).Select(i => (object)new { i, s = words[i].StartMs, e = words[i].EndMs, t = words[i].Text, hit = i >= at && i < at + length, u = words[i].Uncertain })];
    }

    private object Vocabulary(Command c)
    {
        var library = Voices;
        var filter = new LibraryFilter(VoiceId: string.IsNullOrEmpty(c.VoiceId) ? null : c.VoiceId, Unassigned: c.Unassigned == true);
        var voiceDocs = library.Include(filter);
        var counts = wordIndex.Vocabulary(voiceDocs);
        var query = WordIndex.Token(c.Query ?? "");
        var list = counts.Where(p => query.Length == 0 || p.Key.StartsWith(query, StringComparison.Ordinal)).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        int offset = Math.Max(0, c.Offset ?? 0), limit = Math.Clamp(c.Limit ?? 100, 1, 500);
        var wanted = filter.VoiceId == null ? [] : library.Wanted.Where(w => w.VoiceId == filter.VoiceId).Select(w =>
        {
            int found = wordIndex.Find(WordIndex.Query(w.Text), voiceDocs, 100).Count;
            return new { w.Id, w.Text, available = found > 0, count = found };
        }).ToArray();
        return new { total = list.Count, offset, words = list.Skip(offset).Take(limit).Select(p => new { text = p.Key, count = p.Value }).ToArray(), wanted };
    }

    // ---- Preview -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Private preview of a library recording or an indexed Soundpad sound (or one word of it): to the asker only,
    /// never into the game. As Mimic's preview (2.40): at most 30 s; guests get 16 kHz, rate limited, one at a time.
    /// </summary>
    private object LibraryPreview(Command c, string actor, Participant? participant, string? requester)
    {
        bool host = actor == "host";
        string path;
        if (c.RecordingId != null) path = (Voices.Get(c.RecordingId) ?? throw new InvalidDataException("That recording is no longer in the voice library.")).Path;
        else if (c.SoundId != null && parsed.Paths.TryGetValue(c.SoundId, out var soundPath) && (host || GuestPreviewable(soundPath))) path = soundPath;
        else throw new InvalidDataException("Choose a recording to preview.");
        long from = Math.Max(0, c.PositionMs ?? 0), to = c.EndMs is long end && end > from ? end : from + PreviewMaxMs;
        if (to - from > PreviewMaxMs) to = from + PreviewMaxMs;
        string key = PersonKey(actor, participant, requester);
        if (!host && !previewing.TryAdd(key, 0)) throw new InvalidDataException("Your last preview is still being made.");
        try
        {
            if (!host && !previewRate.TryTake(key)) throw new InvalidDataException("Too many previews. Wait a minute and try again.");
            var (mono, rate) = AudioFile.ReadMono(path, from, to);
            if (mono.Length == 0) throw new InvalidDataException("Nothing to hear there.");
            if (!host && rate > GuestPreviewRate) { mono = MimicTranscriber.Resample(mono, rate, GuestPreviewRate); rate = GuestPreviewRate; }
            return new { wav = Convert.ToBase64String(MimicRing.Wav(mono, rate)) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException) { throw new InvalidDataException("That sound file could not be read."); }
        finally { if (!host) previewing.TryRemove(key, out _); }
    }

    // ---- Wanted words and notices --------------------------------------------------------------------------------

    private readonly List<LibraryNotice> notices = [];
    private readonly HashSet<string> noticed = [];
    private long nextNotice = 1;

    private void AddNotice(string dedupe, string kind, LibraryVoice voice, string text, string? recordingId, string? sessionId, long? start, long? end)
    {
        lock (notices)
        {
            if (!noticed.Add(dedupe)) return;
            if (noticed.Count > 10000) noticed.Clear();
            notices.Add(new(nextNotice++, kind, voice.Id, voice.Name, text, recordingId, sessionId, start, end, utcNow()));
            if (notices.Count > 50) notices.RemoveRange(0, notices.Count - 50);
        }
        Interlocked.Increment(ref voicesRevision); Publish();
    }

    /// <summary>A recording with a voice now has its words: does it contain a word that voice wants?</summary>
    private void CheckWantedRecording(string recordingId)
    {
        try
        {
            var library = Voices; var r = library.Get(recordingId);
            if (r?.VoiceId == null || r.WordsState != "ready") return;
            var voice = library.Voices.FirstOrDefault(v => v.Id == r.VoiceId); if (voice == null) return;
            foreach (var w in library.Wanted.Where(w => w.VoiceId == voice.Id))
            {
                var tokens = WordIndex.Query(w.Text);
                if (wordIndex.Find(tokens, key => key == "r:" + recordingId, 1).Count > 0)
                    AddNotice($"r|{voice.Id}|{string.Join(' ', tokens)}|{recordingId}", "recording", voice, w.Text, recordingId, null, null, null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>New words in the live history: a wanted word may have been said (possible match, with where).</summary>
    private void OnWordsAdded(HistoryStore h, IReadOnlyList<TranscriptWord> added)
    {
        if (added.Count == 0) return;
        GroupSpeakersLater(h, added);
        WantedInHistory(h, added);
    }

    private void WantedInHistory(HistoryStore h, IReadOnlyList<TranscriptWord> added)
    {
        VoiceLibrary library;
        try { library = Voices; } catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
        var wanted = library.Wanted; if (wanted.Length == 0) return;
        var voices = library.Voices.ToDictionary(v => v.Id);
        var ordered = added.OrderBy(w => w.Start).ToList(); var tokens = ordered.Select(w => WordIndex.Token(w.Text)).ToArray();
        foreach (var w in wanted)
        {
            var phrase = WordIndex.Query(w.Text); if (phrase.Length == 0 || !voices.TryGetValue(w.VoiceId, out var voice)) continue;
            for (int i = 0; i + phrase.Length <= tokens.Length; i++)
            {
                bool all = true;
                for (int k = 0; k < phrase.Length && all; k++) all = tokens[i + k] == phrase[k];
                if (!all) continue;
                var first = ordered[i]; var last = ordered[i + phrase.Length - 1];
                // A speaker group a person linked to another voice says it is not this voice.
                if (SpeakerVoiceOf(h.SessionId, first.PhraseId) is { } linked && linked != voice.Id) continue;
                AddNotice($"h|{voice.Id}|{string.Join(' ', phrase)}|{h.SessionId}|{first.PhraseId}", "history", voice, w.Text, null, h.SessionId, first.Start, last.End);
            }
        }
    }

    /// <summary>
    /// The recovered history before <paramref name="boundary"/> was discarded (2.41 review): its possible-match notices
    /// go, and so do the speaker groupings of the phrases that lost words there.
    /// </summary>
    private void DropVoicesBefore(string sessionId, long boundary, IReadOnlyCollection<long> phrases)
    {
        int removed;
        lock (notices) removed = notices.RemoveAll(n => n.Kind == "history" && n.SessionId == sessionId && n.StartFrame is long s && s < boundary);
        lock (speakerGate)
            if (speakers is { } session && session.SessionId == sessionId)
                foreach (var id in phrases)
                    if (session.Phrases.Remove(id, out var group) && group is int g && session.Counts.TryGetValue(g, out var count))
                    {
                        if (count <= 1) session.Counts.Remove(g); else session.Counts[g] = count - 1;
                    }
        if (removed > 0 || phrases.Count > 0) Interlocked.Increment(ref voicesRevision); // historyDiscard publishes
    }

    /// <summary>
    /// Notices the asker may see: recordings for everyone; history matches only for a guest the host allows Mimic, and
    /// only where they may see the history (a match says what was said, as a transcript would). The host page also
    /// drops history notices for a guest whose own Mimic permission is off, which the companion cannot see.
    /// </summary>
    private LibraryNotice[] Notices(string actor, Participant? participant, string? requester, string? audience)
    {
        LibraryNotice[] all; lock (notices) all = [.. notices];
        if (actor == "host") return all;
        if (!GuestMimicAllowed(requester, audience)) return [.. all.Where(n => n.Kind == "recording")];
        var h = history; HistoryAccess? access = null;
        try { if (h != null) access = AccessFor(actor, participant, requester, h); } catch (Exception ex) when (ex is HistoryException or InvalidDataException) { }
        return [.. all.Where(n => n.Kind == "recording" || access != null && n.SessionId == h!.SessionId && n.StartFrame is long s && n.EndFrame is long e && access.Allows(s, e))];
    }

    // ---- History search ------------------------------------------------------------------------------------------

    /// <summary>Exact word or phrase in the current history's transcript, only where the asker may see it. Newest first.</summary>
    private object HistorySearch(Command c, string actor, Participant? participant, string? requester)
    {
        var h = CurrentHistory() ?? throw new InvalidDataException("Mimic has not recorded anything yet.");
        if (c.SourceId != null && c.SourceId != h.SessionId) throw new InvalidDataException("Mimic restarted since you looked. Look again.");
        var phrase = WordIndex.Query(c.Query);
        if (phrase.Length == 0) throw new InvalidDataException("Type a word or a phrase to search for.");
        var access = AccessFor(actor, participant, requester, h);
        var words = transcriber.WordsBetween(h, access, 0, long.MaxValue).OrderBy(w => w.Word.Start).ToList();
        var tokens = words.Select(w => WordIndex.Token(w.Correction?.Text ?? w.Word.Text)).ToArray();
        var matches = new List<object>();
        for (int i = tokens.Length - phrase.Length; i >= 0 && matches.Count < 50; i--)
        {
            bool all = true;
            for (int k = 0; k < phrase.Length && all; k++) all = tokens[i + k] == phrase[k];
            if (!all) continue;
            var first = words[i].Word; var last = words[i + phrase.Length - 1].Word;
            var around = words.Skip(Math.Max(0, i - 5)).Take(phrase.Length + 10).Where(w => w.Word.PhraseId == first.PhraseId || w.Word.PhraseId == last.PhraseId);
            matches.Add(new { startFrame = first.Start, endFrame = last.End, text = string.Join(" ", words.Skip(i).Take(phrase.Length).Select(w => w.Correction?.Text ?? w.Word.Text)), context = string.Join(" ", around.Select(w => w.Correction?.Text ?? w.Word.Text)) });
        }
        return new { sessionId = h.SessionId, matches };
    }

    // ---- Background worker: library words and the category word index -------------------------------------------

    private readonly object workerGate = new();
    private Task? libraryWorker;
    private readonly SemaphoreSlim workSignal = new(0, 1);
    private readonly CancellationTokenSource workerStop = new();
    private volatile string workerStatus = "";
    private volatile string? workerCurrent;
    private readonly HashSet<string> failedRecordings = [];
    private Queue<(string Path, string Title)>? categoryQueue;
    private string? categoryQueueKey;
    private long categoryQueueAt;
    /// <summary>How often a finished pass over the indexed categories starts again, for files changed or unreadable since (tests set 0).</summary>
    internal int CategoryRescanMs { get; set; } = 60000;
    private long lastProgressPublish;
    /// <summary>Breathing space between two files (tests set 0).</summary>
    internal int WorkerPauseMs { get; set; } = 400;
    /// <summary>Tests turn the background loop off and drive the worker themselves (DrainLibraryWorker).</summary>
    internal bool BackgroundWorker { get; set; } = true;
    /// <summary>Test seam: true once nothing is left to do (pending recordings, unindexed category files).</summary>
    public bool LibraryWorkerIdle { get; private set; } = true;

    private void SignalWorker()
    {
        if (Volatile.Read(ref disposed) != 0 || !BackgroundWorker) return;
        lock (workerGate) libraryWorker ??= Task.Run(() => WorkerLoop(workerStop.Token));
        try { if (workSignal.CurrentCount == 0) workSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool did = false;
            try { did = await WorkOnce(token); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { workerStatus = "Background transcription paused: " + ex.Message; }
            LibraryWorkerIdle = !did;
            try
            {
                if (!did) await workSignal.WaitAsync(TimeSpan.FromSeconds(10), token);
                else if (WorkerPauseMs > 0) await Task.Delay(WorkerPauseMs, token);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>The Soundpad sound files of the chosen categories (and their sub-categories), each once.</summary>
    private List<(string Path, string Title)> SelectedSoundFiles(LibrarySettings settings)
    {
        var catalog = parsed; var chosen = settings.IndexCategories.ToHashSet(StringComparer.Ordinal);
        var titles = catalog.Public.Sounds.ToDictionary(s => s.Id, s => s.Name);
        var result = new List<(string, string)>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        // The app's own exact-playback slot files are never indexed.
        var slots = new HashSet<string>(Slots.Owned(), StringComparer.OrdinalIgnoreCase);
        void Walk(Category category, bool inside)
        {
            bool take = inside || chosen.Contains(category.Name);
            if (take) foreach (var id in category.SoundIds) if (catalog.Paths.TryGetValue(id, out var path) && seen.Add(SoundWords.Key(path)) && !slots.Contains(path)) result.Add((path, titles.GetValueOrDefault(id) ?? id));
            foreach (var child in category.Children) Walk(child, take);
        }
        if (chosen.Count > 0) foreach (var c in catalog.Public.Categories) Walk(c, false);
        return result;
    }

    private object IndexProgress(bool host)
    {
        var settings = Voices.Settings;
        var files = settings.IndexCategories.Count == 0 ? [] : SelectedSoundFiles(settings);
        var index = SoundIndex;
        int indexed = files.Count(f => index.Has(f.Path)), failed = files.Count(f => index.Get(f.Path)?.Error != null);
        return new { total = files.Count, indexed, failed, paused = settings.IndexPaused, current = host ? workerCurrent : null, active = settings.IndexCategories.Count > 0 };
    }

    private void ProgressChanged(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref lastProgressPublish) < 1000) return;
        Interlocked.Exchange(ref lastProgressPublish, now);
        Interlocked.Increment(ref voicesRevision); Publish();
    }

    private async Task<bool> WorkOnce(CancellationToken token)
    {
        var library = Voices;
        LibraryRecording? pending;
        lock (workerGate) pending = library.NextPending(failedRecordings);
        var settings = library.Settings;
        bool categories = settings.IndexCategories.Count > 0 && !settings.IndexPaused;
        if (pending == null && !categories) { workerStatus = ""; return false; }
        if (!transcriber.ModelReady)
        {
            workerStatus = "Waiting for the speech model: download it in Mimic (Transcripts) to find the words of new recordings and indexed sounds.";
            return false;
        }
        if (pending != null)
        {
            workerStatus = "Finding the words of new recordings"; workerCurrent = pending.Title; ProgressChanged(true);
            try
            {
                var words = await transcriber.WordsOfFile(pending.Path, token);
                library.SetWords(pending.Id, words);
                CheckWantedRecording(pending.Id);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                lock (workerGate) failedRecordings.Add(pending.Id);
                library.SetWordsFailed(pending.Id, ex is InvalidDataException ? ex.Message : "The kept file could not be read.");
            }
            finally { workerCurrent = null; }
            return true;
        }
        // The category index: one file at a time; files already indexed at this length and time are skipped.
        var files = SelectedSoundFiles(settings);
        string key = string.Join("\n", files.Select(f => f.Path));
        lock (workerGate)
        {
            // A finished pass starts again now and then, so a file re-saved at the same path (or one that could not be
            // read for a moment) is indexed again; Done skips everything unchanged.
            bool rescan = categoryQueue is { Count: 0 } && Environment.TickCount64 - categoryQueueAt >= CategoryRescanMs;
            if (categoryQueue == null || categoryQueueKey != key || rescan)
            {
                categoryQueue = new(files.Where(f => !SoundIndex.Has(f.Path)).Concat(files.Where(f => SoundIndex.Has(f.Path))));
                categoryQueueKey = key; categoryQueueAt = Environment.TickCount64;
            }
        }
        while (true)
        {
            (string Path, string Title) next;
            lock (workerGate)
            {
                // libraryIndex (pause, resume, other categories) drops the queue under this lock; the next pass rebuilds it.
                if (categoryQueue is not { } queue) return true;
                if (queue.Count == 0) { workerStatus = ""; return false; }
                next = queue.Dequeue();
            }
            FileInfo info;
            try { info = new FileInfo(next.Path); if (!info.Exists) continue; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            long size = info.Length, modified = info.LastWriteTimeUtc.Ticks;
            if (SoundIndex.Done(next.Path, size, modified)) continue;
            if (Voices.Settings.IndexPaused) { lock (workerGate) categoryQueue = null; return false; }
            workerStatus = "Indexing the words of Soundpad sounds"; workerCurrent = next.Title; ProgressChanged(true);
            SoundWords.Entry entry;
            try
            {
                var words = await transcriber.WordsOfFile(next.Path, token);
                // Changed while it was read: not indexed now; the next pass sees the new version.
                info.Refresh();
                if (info.Length != size || info.LastWriteTimeUtc.Ticks != modified) { workerCurrent = null; return true; }
                entry = new(next.Path, size, modified, [.. words], null, utcNow());
            }
            catch (Exception ex) when (ex is IOException and not EndOfStreamException or UnauthorizedAccessException)
            {
                // Locked or unreadable for now (another program has it open): not recorded, so the next pass tries again.
                workerCurrent = null; ProgressChanged(true);
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                entry = new(next.Path, size, modified, null, ex is InvalidDataException ? ex.Message : "The file could not be read.", utcNow());
            }
            SoundIndex.Store(entry);
            workerCurrent = null; ProgressChanged(true);
            return true;
        }
    }

    /// <summary>Test seam: runs the worker until it has nothing left to do (or the time runs out).</summary>
    public async Task<bool> DrainLibraryWorker(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!await WorkOnce(CancellationToken.None)) return true;
        }
        return false;
    }

    // ---- Speaker grouping (experimental) -------------------------------------------------------------------------

    private sealed class SpeakerSession
    {
        public required string SessionId;
        public SpeakerGrouper Grouper = new();
        public readonly Dictionary<long, int?> Phrases = [];
        public readonly Dictionary<int, (string? Name, string? VoiceId)> Names = [];
        public readonly Dictionary<int, int> Counts = [];
    }
    private readonly object speakerGate = new();
    private SpeakerSession? speakers;
    private readonly SemaphoreSlim speakerWork = new(1, 1);

    private void GroupSpeakersLater(HistoryStore h, IReadOnlyList<TranscriptWord> added)
    {
        if (LibrarySettingsNow?.GroupSpeakers != true) return;
        var phrases = added.GroupBy(w => w.PhraseId).Select(g => (Id: g.Key, Start: g.Min(w => w.Start), End: g.Max(w => w.End))).ToList();
        _ = Task.Run(async () =>
        {
            await speakerWork.WaitAsync();
            try
            {
                foreach (var (id, start, end) in phrases)
                {
                    float[] audio;
                    try { audio = h.Read(HistoryAccess.Host, start, (int)Math.Min(end - start, (long)h.Rate * 30)); }
                    catch (HistoryException) { continue; }
                    var features = SpeakerAnalysis.Analyze(MimicTranscriber.Resample(audio, h.Rate, SpeakerAnalysis.Rate));
                    lock (speakerGate)
                    {
                        if (speakers == null || speakers.SessionId != h.SessionId) speakers = new SpeakerSession { SessionId = h.SessionId };
                        int? group = speakers.Grouper.Assign(features);
                        speakers.Phrases[id] = group;
                        if (group is int g) speakers.Counts[g] = speakers.Counts.GetValueOrDefault(g) + 1;
                    }
                }
                ProgressChanged();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            finally { speakerWork.Release(); }
        });
    }

    /// <summary>The speaker group of a phrase (host ids), or null (Unassigned, not analysed, grouping off).</summary>
    private int? SpeakerOf(string sessionId, long phraseId)
    {
        lock (speakerGate) return speakers is { } s && s.SessionId == sessionId && s.Phrases.TryGetValue(phraseId, out var g) ? g : null;
    }
    private string? SpeakerVoiceOf(string sessionId, long phraseId)
    {
        lock (speakerGate) return SpeakerOf(sessionId, phraseId) is int g && speakers!.Names.TryGetValue(g, out var n) ? n.VoiceId : null;
    }

    /// <summary>The speaker groups of the current session (speakerGroups), and naming and linking them (speakerName, speakerLink).</summary>
    private object Speakers(Command c, string actor)
    {
        var h = CurrentHistory();
        bool host = actor == "host";
        if (c.Op != "speakerGroups")
        {
            if (!host && !Voices.Settings.GuestLibraryEdit) throw new InvalidDataException("The host has not allowed guests to edit the voice library.");
            if (h == null || c.GroupId is not int group) throw new InvalidDataException("Choose a speaker group.");
            // Group numbers start again with each recording: one from another recording is another speaker (2.41 review).
            if (c.SourceId != null && c.SourceId != h.SessionId) throw new InvalidDataException("Mimic restarted since you looked. Look again.");
            string? voiceId = null;
            if (c.Op == "speakerLink" && !string.IsNullOrEmpty(c.VoiceId))
            {
                if (!Voices.Voices.Any(v => v.Id == c.VoiceId)) throw new InvalidDataException("That voice is no longer in the library.");
                voiceId = c.VoiceId;
            }
            lock (speakerGate)
            {
                if (speakers == null || speakers.SessionId != h.SessionId || !speakers.Counts.ContainsKey(group)) throw new InvalidDataException("That speaker group is gone.");
                var current = speakers.Names.GetValueOrDefault(group);
                speakers.Names[group] = c.Op == "speakerName" ? (string.IsNullOrWhiteSpace(c.Name) ? null : c.Name.Trim()[..Math.Min(40, c.Name.Trim().Length)], current.VoiceId) : (current.Name, voiceId);
            }
            ProgressChanged(true);
        }
        var settings = Voices.Settings;
        lock (speakerGate)
        {
            var s = h != null && speakers?.SessionId == h.SessionId ? speakers : null;
            var groups = s == null ? [] : s.Counts.Keys.Order().Select(g => new
            {
                id = g, label = s.Names.GetValueOrDefault(g).Name ?? $"Speaker {g}", name = s.Names.GetValueOrDefault(g).Name, voiceId = s.Names.GetValueOrDefault(g).VoiceId,
                color = VoiceLibrary.Palette[(g - 1) % VoiceLibrary.Palette.Length], phrases = s.Counts[g],
            }).ToArray();
            return new { enabled = settings.GroupSpeakers, groups, unassigned = s?.Phrases.Values.Count(v => v == null) ?? 0 };
        }
    }
}
