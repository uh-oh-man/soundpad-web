namespace SoundpadWeb;

/// <summary>A change about to be recorded in the audit trail.</summary>
public sealed record AuditChange(string Target, string TargetId, string Field, string? Before, string? After);

// Edits, voices, wanted words, audit and Undo (2.41). Every change is one audit group; Undo reverts a whole group,
// and only when nothing has changed those values since (otherwise it says so and changes nothing).
public sealed partial class VoiceLibrary
{
    private long Reserve(int count) { long first = file.NextAudit; file.NextAudit += Math.Max(1, count); return first; }

    private void Record(long first, string by, string byName, IReadOnlyList<AuditChange> changes, long? undoOf = null)
    {
        long at = now(); var lines = new List<string>();
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            var entry = new LibraryAudit(first + i, first, at, by, byName, c.Target, c.TargetId, c.Field, c.Before, c.After, undoOf);
            audit.Add(entry); lines.Add(Json.Write(entry));
        }
        if (audit.Count > 2000) audit.RemoveRange(0, audit.Count - 2000);
        Append(AuditPath, lines);
    }

    /// <summary>Applies the changes (in memory), saves, and records them as one group; on a failed save everything goes back.</summary>
    private long Commit(string by, string byName, List<AuditChange> changes, Action apply, Action revert, long? undoOf = null)
    {
        if (changes.Count == 0) return 0;
        apply();
        long first = Reserve(changes.Count);
        try { SaveLocked(); }
        catch { revert(); file.NextAudit = first; throw; }
        // A corrected text that lines up word by word is what search finds (also after Undo).
        foreach (var c in changes) if (c.Target == "recording" && c.Field == "correctedText") IndexLocked(c.TargetId);
        Record(first, by, byName, changes, undoOf);
        Raise();
        return first;
    }

    private static string[] CleanTags(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        foreach (var raw in tags ?? [])
        {
            var tag = Clip(raw, MaxTagLength);
            if (tag.Length > 0 && !result.Contains(tag, StringComparer.OrdinalIgnoreCase)) result.Add(tag);
        }
        if (result.Count > MaxTags) throw new InvalidDataException($"A recording has at most {MaxTags} tags.");
        return [.. result];
    }

    /// <summary>Edits one or many recordings as one action (one Undo). Returns the audit group (0 when nothing changed).</summary>
    public long Edit(IReadOnlyList<string> ids, LibraryEdit edit, string by, string byName)
    {
        lock (gate)
        {
            RequireWritable();
            if (ids.Count is 0 or > 500) throw new InvalidDataException("Choose between 1 and 500 recordings.");
            var targets = ids.Distinct().Select(id => byId.GetValueOrDefault(id) ?? throw new InvalidDataException("That recording is no longer in the voice library.")).ToList();
            if (edit.VoiceId != null && !file.Voices.Any(v => v.Id == edit.VoiceId)) throw new InvalidDataException("That voice is no longer in the library.");
            var title = edit.Title != null ? Clip(edit.Title, MaxTitle) : null;
            if (edit.Title != null && title!.Length == 0) throw new InvalidDataException("Give the recording a title.");
            string? corrected = edit.CorrectedText == null ? null : Clip(edit.CorrectedText, MaxText);
            var setTags = edit.Tags != null ? CleanTags(edit.Tags) : null;
            var addTags = CleanTags(edit.AddTags); var removeTags = CleanTags(edit.RemoveTags);
            var changes = new List<AuditChange>(); var undo = new List<Action>(); var redo = new List<Action>();
            void Change<T>(LibraryRecording r, string field, T before, T after, Action<T> set)
            {
                if (Json.Write(before) == Json.Write(after)) return;
                changes.Add(new("recording", r.Id, field, Json.Write(before), Json.Write(after)));
                redo.Add(() => set(after)); undo.Add(() => set(before));
            }
            foreach (var r in targets)
            {
                if (title != null) Change(r, "title", r.Title, title, v => r.Title = v);
                if (edit.VoiceId != null || edit.ClearVoice) Change(r, "voice", r.VoiceId, edit.ClearVoice ? null : edit.VoiceId, v => r.VoiceId = v);
                var tags = setTags?.ToList() ?? [.. r.Tags];
                foreach (var t in addTags) if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase)) tags.Add(t);
                tags.RemoveAll(t => removeTags.Contains(t, StringComparer.OrdinalIgnoreCase));
                if (tags.Count > MaxTags) throw new InvalidDataException($"A recording has at most {MaxTags} tags.");
                Change(r, "tags", r.Tags.ToArray(), tags.ToArray(), v => r.Tags = [.. v]);
                if (edit.Favorite is bool favorite) Change(r, "favorite", r.Favorite, favorite, v => r.Favorite = v);
                if (corrected != null) Change(r, "correctedText", r.CorrectedText, corrected.Length == 0 ? null : corrected, v => r.CorrectedText = v);
                if (edit.Reviewed is bool reviewed) Change(r, "reviewed", r.Reviewed, reviewed, v => r.Reviewed = v);
            }
            long at = now();
            return Commit(by, byName, changes,
                () => { foreach (var a in redo) a(); foreach (var r in targets) r.Updated = at; },
                () => { foreach (var a in undo) a(); });
        }
    }

    /// <summary>Takes recordings out of the library (never their files). Undo puts them back.</summary>
    public long Remove(IReadOnlyList<string> ids, string by, string byName)
    {
        lock (gate)
        {
            RequireWritable();
            if (ids.Count is 0 or > 500) throw new InvalidDataException("Choose between 1 and 500 recordings.");
            var targets = ids.Distinct().Select(id => byId.GetValueOrDefault(id) ?? throw new InvalidDataException("That recording is no longer in the voice library.")).ToList();
            var changes = targets.Select(r => new AuditChange("recording", r.Id, "record", Json.Write(r), null)).ToList();
            return Commit(by, byName, changes,
                () => { foreach (var r in targets) { file.Recordings.Remove(r); byId.Remove(r.Id); index.Remove(DocKey(r.Id)); } },
                () => { foreach (var r in targets) Restore(r); });
        }
    }

    private void Restore(LibraryRecording r)
    {
        if (byId.ContainsKey(r.Id)) return;
        file.Recordings.Add(r); byId[r.Id] = r;
        if (words.ContainsKey(r.Id)) IndexLocked(r.Id);
        else if (r.WordsState == "ready") r.WordsState = "pending";
    }

    // ---- Voices and wanted words --------------------------------------------------------------------------------

    public (LibraryVoice Voice, long Group) CreateVoice(string name, string? color, string by, string byName)
    {
        lock (gate)
        {
            RequireWritable();
            var clean = Clip(name, 40);
            if (clean.Length == 0) throw new InvalidDataException("Give the voice a name.");
            if (file.Voices.Count >= MaxVoices) throw new InvalidDataException($"The library holds at most {MaxVoices} voices.");
            if (file.Voices.Any(v => string.Equals(v.Name, clean, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"There is already a voice called {clean}.");
            var voice = new LibraryVoice { Id = NewId(), Name = clean, Color = color is { } c && ColorPattern.IsMatch(c) ? c : Palette[file.Voices.Count % Palette.Length], Created = now() };
            long group = Commit(by, byName, [new("voice", voice.Id, "record", null, Json.Write(voice))], () => file.Voices.Add(voice), () => file.Voices.Remove(voice));
            return (voice, group);
        }
    }

    public long UpdateVoice(string voiceId, string? name, string? color, string by, string byName)
    {
        lock (gate)
        {
            RequireWritable();
            var voice = file.Voices.FirstOrDefault(v => v.Id == voiceId) ?? throw new InvalidDataException("That voice is no longer in the library.");
            var changes = new List<AuditChange>(); string newName = voice.Name, newColor = voice.Color;
            if (name != null)
            {
                newName = Clip(name, 40);
                if (newName.Length == 0) throw new InvalidDataException("Give the voice a name.");
                if (file.Voices.Any(v => v != voice && string.Equals(v.Name, newName, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"There is already a voice called {newName}.");
                if (newName != voice.Name) changes.Add(new("voice", voice.Id, "name", Json.Write(voice.Name), Json.Write(newName)));
            }
            if (color != null)
            {
                if (!ColorPattern.IsMatch(color)) throw new InvalidDataException("Choose a colour like #7c6cf2.");
                newColor = color;
                if (newColor != voice.Color) changes.Add(new("voice", voice.Id, "color", Json.Write(voice.Color), Json.Write(newColor)));
            }
            string oldName = voice.Name, oldColor = voice.Color;
            return Commit(by, byName, changes, () => { voice.Name = newName; voice.Color = newColor; }, () => { voice.Name = oldName; voice.Color = oldColor; });
        }
    }

    public long DeleteVoice(string voiceId, string by, string byName)
    {
        lock (gate)
        {
            RequireWritable();
            var voice = file.Voices.FirstOrDefault(v => v.Id == voiceId) ?? throw new InvalidDataException("That voice is no longer in the library.");
            if (file.Recordings.Any(r => r.VoiceId == voiceId)) throw new InvalidDataException($"{voice.Name} still has recordings. Give them another voice first.");
            var wanted = file.Wanted.Where(w => w.VoiceId == voiceId).ToList();
            var changes = wanted.Select(w => new AuditChange("wanted", w.Id, "record", Json.Write(w), null)).Append(new("voice", voice.Id, "record", Json.Write(voice), null)).ToList();
            int at = file.Voices.IndexOf(voice);
            return Commit(by, byName, changes, () => { file.Wanted.RemoveAll(w => w.VoiceId == voiceId); file.Voices.Remove(voice); }, () => { file.Voices.Insert(at, voice); file.Wanted.AddRange(wanted); });
        }
    }

    public (WantedWord Wanted, long Group) AddWanted(string voiceId, string text, string by, string byName)
    {
        lock (gate)
        {
            RequireWritable();
            if (!file.Voices.Any(v => v.Id == voiceId)) throw new InvalidDataException("That voice is no longer in the library.");
            var clean = Clip(text, 100);
            if (WordIndex.Query(clean).Length == 0) throw new InvalidDataException("Type a word or a phrase.");
            var tokens = string.Join(' ', WordIndex.Query(clean));
            if (file.Wanted.FirstOrDefault(w => w.VoiceId == voiceId && string.Join(' ', WordIndex.Query(w.Text)) == tokens) is { } same) return (same, 0);
            if (file.Wanted.Count(w => w.VoiceId == voiceId) >= MaxWantedPerVoice) throw new InvalidDataException($"A voice has at most {MaxWantedPerVoice} wanted words.");
            var wanted = new WantedWord { Id = NewId(), VoiceId = voiceId, Text = clean, By = by, ByName = byName, At = now() };
            long group = Commit(by, byName, [new("wanted", wanted.Id, "record", null, Json.Write(wanted))], () => file.Wanted.Add(wanted), () => file.Wanted.Remove(wanted));
            return (wanted, group);
        }
    }

    public long RemoveWanted(string wantedId, string by, string byName)
    {
        lock (gate)
        {
            RequireWritable();
            var wanted = file.Wanted.FirstOrDefault(w => w.Id == wantedId) ?? throw new InvalidDataException("That wanted word is already gone.");
            return Commit(by, byName, [new("wanted", wanted.Id, "record", Json.Write(wanted), null)], () => file.Wanted.Remove(wanted), () => file.Wanted.Add(wanted));
        }
    }

    /// <summary>Host settings (not audited for Undo: they are the host's switches).</summary>
    public LibrarySettings UpdateSettings(Func<LibrarySettings, LibrarySettings> change)
    {
        lock (gate)
        {
            RequireWritable();
            var before = file.Settings; var after = change(before);
            if (after.IndexCategories.Count > MaxCategories) throw new InvalidDataException($"Choose at most {MaxCategories} categories.");
            file.Settings = after;
            try { SaveLocked(); } catch { file.Settings = before; throw; }
            Raise();
            return after;
        }
    }

    // ---- Audit and Undo ------------------------------------------------------------------------------------------

    public LibraryAudit[] Audit(string? targetId = null, int max = 50)
    {
        lock (gate) return [.. audit.Where(a => targetId == null || a.TargetId == targetId).Reverse().Take(Math.Clamp(max, 1, 500))];
    }

    /// <summary>
    /// Undoes one action (an audit group). Only the person who did it, or the host. Refused when any of its values
    /// changed since, or a recording or voice it touched is gone; nothing is then changed.
    /// </summary>
    public long Undo(long group, string by, string byName, bool host)
    {
        lock (gate)
        {
            RequireWritable();
            var entries = audit.Where(a => a.Group == group).OrderByDescending(a => a.Id).ToList();
            if (entries.Count == 0) throw new InvalidDataException("That change is too old to undo.");
            if (!host && entries[0].By != by) throw new InvalidDataException("You can undo only your own changes.");
            if (audit.Any(a => a.UndoOf == group)) throw new InvalidDataException("That change was already undone.");
            var apply = new List<Action>(); var revert = new List<Action>(); var changes = new List<AuditChange>();
            foreach (var e in entries)
            {
                if (Current(e) != e.After) throw new InvalidDataException("That changed again since. Nothing was undone.");
                var (set, unset) = Setter(e);
                apply.Add(set); revert.Add(unset);
                changes.Add(new(e.Target, e.TargetId, e.Field, e.After, e.Before));
            }
            return Commit(by, byName, changes, () => { foreach (var a in apply) a(); }, () => { for (int i = revert.Count - 1; i >= 0; i--) revert[i](); }, group);
        }
    }

    /// <summary>The value an audit entry's field has now (as JSON), to see whether Undo still applies.</summary>
    private string? Current(LibraryAudit e)
    {
        switch (e.Target)
        {
            case "recording":
                if (e.Field == "record") return byId.TryGetValue(e.TargetId, out var whole) ? (e.After == null ? "present" : e.After) : null;
                if (!byId.TryGetValue(e.TargetId, out var r)) return "missing";
                return e.Field switch
                {
                    "title" => Json.Write(r.Title), "voice" => Json.Write(r.VoiceId), "tags" => Json.Write(r.Tags.ToArray()), "favorite" => Json.Write(r.Favorite),
                    "correctedText" => Json.Write(r.CorrectedText), "reviewed" => Json.Write(r.Reviewed), _ => "unknown",
                };
            case "voice":
                var voice = file.Voices.FirstOrDefault(v => v.Id == e.TargetId);
                if (e.Field == "record") return voice == null ? null : e.After == null ? "present" : e.After;
                if (voice == null) return "missing";
                return e.Field == "name" ? Json.Write(voice.Name) : e.Field == "color" ? Json.Write(voice.Color) : "unknown";
            case "wanted":
                var wanted = file.Wanted.FirstOrDefault(w => w.Id == e.TargetId);
                return wanted == null ? null : e.After == null ? "present" : e.After;
        }
        return "unknown";
    }

    private static T Read<T>(string? json) => json == null ? default! : System.Text.Json.JsonSerializer.Deserialize<T>(json, Json.Options)!;

    /// <summary>(set to Before, set back to After) for one entry.</summary>
    private (Action Set, Action Unset) Setter(LibraryAudit e)
    {
        switch (e.Target, e.Field)
        {
            case ("recording", "record"):
                if (e.Before == null)
                {
                    var r = byId[e.TargetId];
                    return (() => { file.Recordings.Remove(r); byId.Remove(r.Id); index.Remove(DocKey(r.Id)); }, () => Restore(r));
                }
                var back = Read<LibraryRecording>(e.Before);
                if (back.VoiceId != null && !file.Voices.Any(v => v.Id == back.VoiceId)) back.VoiceId = null;
                return (() => Restore(back), () => { file.Recordings.Remove(back); byId.Remove(back.Id); index.Remove(DocKey(back.Id)); });
            case ("recording", _):
                {
                    var r = byId[e.TargetId];
                    Action<string?> set = e.Field switch
                    {
                        "title" => v => r.Title = Read<string>(v),
                        "voice" => v => r.VoiceId = Read<string?>(v) is { } id && file.Voices.Any(x => x.Id == id) ? id : null,
                        "tags" => v => r.Tags = [.. Read<string[]>(v) ?? []],
                        "favorite" => v => r.Favorite = Read<bool>(v),
                        "correctedText" => v => r.CorrectedText = Read<string?>(v),
                        "reviewed" => v => r.Reviewed = Read<bool>(v),
                        _ => throw new InvalidDataException("That change cannot be undone."),
                    };
                    return (() => set(e.Before), () => set(e.After));
                }
            case ("voice", "record"):
                if (e.Before == null)
                {
                    var voice = file.Voices.First(v => v.Id == e.TargetId);
                    if (file.Recordings.Any(r => r.VoiceId == voice.Id) || file.Wanted.Any(w => w.VoiceId == voice.Id)) throw new InvalidDataException($"{voice.Name} has recordings or wanted words now. Nothing was undone.");
                    return (() => file.Voices.Remove(voice), () => file.Voices.Add(voice));
                }
                var restored = Read<LibraryVoice>(e.Before);
                return (() => file.Voices.Add(restored), () => file.Voices.Remove(restored));
            case ("voice", _):
                {
                    var voice = file.Voices.First(v => v.Id == e.TargetId);
                    Action<string?> set = e.Field == "name" ? v => voice.Name = Read<string>(v) : v => voice.Color = Read<string>(v);
                    return (() => set(e.Before), () => set(e.After));
                }
            case ("wanted", "record"):
                if (e.Before == null)
                {
                    var wanted = file.Wanted.First(w => w.Id == e.TargetId);
                    return (() => file.Wanted.Remove(wanted), () => file.Wanted.Add(wanted));
                }
                var again = Read<WantedWord>(e.Before);
                return (() => file.Wanted.Add(again), () => file.Wanted.Remove(again));
        }
        throw new InvalidDataException("That change cannot be undone.");
    }

    // ---- Lists and search ------------------------------------------------------------------------------------------

    private bool Matches(LibraryRecording r, LibraryFilter f)
    {
        if (f.Unassigned && r.VoiceId != null) return false;
        if (f.VoiceId != null && r.VoiceId != f.VoiceId) return false;
        if (f.Tag != null && !r.Tags.Contains(f.Tag, StringComparer.OrdinalIgnoreCase)) return false;
        if (f.Favorite is bool fav && r.Favorite != fav) return false;
        if (f.Reviewed is bool rev && r.Reviewed != rev) return false;
        return true;
    }

    /// <summary>Newest first; a page of at most 200.</summary>
    public (int Total, LibraryRecording[] Items) List(LibraryFilter filter, int offset, int limit)
    {
        lock (gate)
        {
            var text = filter.Text?.Trim();
            var all = file.Recordings.Where(r => Matches(r, filter) && (string.IsNullOrEmpty(text) || r.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || (r.CorrectedText?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
            all.Reverse();
            return (all.Count, [.. all.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 200))]);
        }
    }

    /// <summary>
    /// Recordings matching the filter, as a document test for the word index. The matching ids are taken now, so the
    /// test never takes the library's lock inside the index's (the library takes the index's inside its own).
    /// </summary>
    public Func<string, bool> Include(LibraryFilter filter)
    {
        HashSet<string> ids;
        lock (gate) ids = file.Recordings.Where(r => Matches(r, filter)).Select(r => DocKey(r.Id)).ToHashSet(StringComparer.Ordinal);
        return ids.Contains;
    }

    /// <summary>Recordings whose title or corrected text contains the phrase (for search results with no timed word).</summary>
    public LibraryRecording[] TextMatches(string phrase, LibraryFilter filter, int max)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return [];
        lock (gate) return [.. file.Recordings.Where(r => Matches(r, filter) && (r.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase) || (r.CorrectedText?.Contains(phrase, StringComparison.OrdinalIgnoreCase) ?? false))).Reverse().Take(max)];
    }

    public string[] AllTags() { lock (gate) return [.. file.Recordings.SelectMany(r => r.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(500)]; }
    public Dictionary<string, int> VoiceCounts() { lock (gate) return file.Recordings.GroupBy(r => r.VoiceId ?? "").ToDictionary(g => g.Key, g => g.Count()); }
    public int UnreviewedCount { get { lock (gate) return file.Recordings.Count(r => !r.Reviewed); } }
}
