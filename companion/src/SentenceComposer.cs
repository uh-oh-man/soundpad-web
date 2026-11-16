namespace SoundpadWeb;

/// <summary>One recorded occurrence of a word or phrase in a library recording.</summary>
public sealed record CorpusTake(string RecordingId, string? VoiceId, int WordIndex, int WordCount, long StartMs, long EndMs, long GapAfterMs, string Text, double Quality, bool Altered, long Created);
/// <summary>A named nonverbal piece with the voice of its recording.</summary>
public sealed record CorpusNamed(string Id, string Name, string RecordingId, string? VoiceId, long StartMs, long EndMs, bool Altered);
/// <summary>Which takes the automatic choice may use: the active voice filters, and altered takes only when allowed.</summary>
public sealed record TakeFilter(IReadOnlySet<string>? Voices, bool Altered)
{
    public bool Allows(string? voiceId, bool altered) => (Altered || !altered) && (Voices == null || Voices.Count == 0 || voiceId != null && Voices.Contains(voiceId));
    public static TakeFilter Of(SentenceSettings s) => new(s.VoiceIds.Count == 0 ? null : s.VoiceIds.ToHashSet(StringComparer.Ordinal), s.Altered);
}

/// <summary>What sentences are made from: the voice library's recorded words and named pieces (or a test stand-in).</summary>
public interface ISentenceCorpus
{
    /// <summary>Every place the exact phrase was recorded, among the takes the filter allows (at most <paramref name="max"/>).</summary>
    IReadOnlyList<CorpusTake> Find(string[] tokens, TakeFilter filter, int max);
    IReadOnlyList<CorpusNamed> Named(string name, TakeFilter filter);
    /// <summary>Distinct recorded words (tokens) with how often they occur.</summary>
    IReadOnlyDictionary<string, int> Vocabulary(TakeFilter filter);
}

/// <summary>A word of the sentence text: its search form (or "[name]" for a nonverbal piece) and how it was typed.</summary>
public readonly record struct SentenceToken(string Key, string Display)
{
    public bool Nonverbal => Key.StartsWith('[');
}

/// <summary>A splice a person may approve: letters of recorded words joined into a word that was never recorded whole.</summary>
public sealed record SpliceCandidate(string Description, IReadOnlyList<SentenceSource> Parts);

/// <summary>
/// Matches sentence text to recorded words and phrases (2.42). Pure: the same text, settings, seed and library give
/// the same pieces. It never invents speech and never substitutes a different word: a word nobody recorded stays a
/// visibly missing piece (a gap or a bleep) until a person deliberately picks a similar word or approves a splice.
/// </summary>
public static class SentenceComposer
{
    public const int MaxPhrase = 8;
    /// <summary>Breathing room around a word's recognised edges, as the library's word player uses (20 ms before, 10 after).</summary>
    public const long PadBeforeMs = 20, PadAfterMs = 10;
    /// <summary>The join between pieces with fluent timing.</summary>
    public const double FluentGapMs = 40;
    public const double MissingCost = 10;

    // ---- Text --------------------------------------------------------------------------------------------------

    /// <summary>The sentence text as words. "[laugh]" (any [name]) is a nonverbal piece; punctuation around words is dropped.</summary>
    public static List<SentenceToken> Tokens(string? text) => [.. Words(text).Select(w => w.Token)];

    /// <summary>
    /// The words of the text with where each stands in it, [Start, End) in characters (2.45, for inserting at a caret).
    /// The one place text becomes words: <see cref="Tokens"/> is these without the positions.
    /// </summary>
    public static IEnumerable<(SentenceToken Token, int Start, int End)> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        int count = 0, i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) yield break;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (count >= SentenceStore.MaxTokens) yield break;
            var raw = text[start..i];
            if (raw.Length >= 3 && raw[0] == '[' && raw.TrimEnd('.', ',', '!', '?', ';', ':').EndsWith(']'))
            {
                var name = raw.TrimEnd('.', ',', '!', '?', ';', ':')[1..^1].Trim().ToLowerInvariant();
                if (SentenceStore.NamePattern.IsMatch(name)) { count++; yield return (new("[" + name + "]", raw), start, i); continue; }
            }
            var key = WordIndex.Token(raw);
            if (key.Length > 0) { count++; yield return (new(key, raw), start, i); }
        }
    }

    private static string Display(IReadOnlyList<SentenceToken> tokens, int start, int end) => string.Join(' ', tokens.Skip(start).Take(end - start).Select(t => t.Display));

    // ---- Deterministic randomness ---------------------------------------------------------------------------------

    /// <summary>FNV-1a over the parts: stable across runs and machines (string.GetHashCode is not).</summary>
    public static ulong StableHash(params object[] parts)
    {
        ulong h = 14695981039346656037UL;
        foreach (var part in parts)
        {
            foreach (char ch in part?.ToString() ?? "") { h ^= ch; h *= 1099511628211UL; }
            h ^= 0xff; h *= 1099511628211UL;
        }
        return h;
    }
    /// <summary>SplitMix64: a small, seedable generator whose sequence never depends on the runtime.</summary>
    public sealed class Rng(ulong seed)
    {
        private ulong state = seed;
        public double Next()
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }

    // ---- Matching ----------------------------------------------------------------------------------------------

    private sealed record Candidate(string Voice, CorpusTake? Take, CorpusNamed? Named, double Score);

    private static IReadOnlyList<CorpusTake> Takes(ISentenceCorpus corpus, string[] span, TakeFilter filter) => corpus.Find(span, filter, 400);

    /// <summary>Ranks takes: originals, certain words, favourites and reviewed ones first (Quality), then newest; a stable order.</summary>
    public static List<CorpusTake> Rank(IEnumerable<CorpusTake> takes) =>
        [.. takes.OrderByDescending(t => t.Quality - (t.Altered ? 0.5 : 0)).ThenByDescending(t => t.Created).ThenBy(t => t.RecordingId, StringComparer.Ordinal).ThenBy(t => t.WordIndex)];

    /// <summary>
    /// Pieces for tokens [a, b) of the text: a dynamic program over phrase lengths and voices. Smooth (chaos 0)
    /// prefers the longest recorded phrases and staying with one voice; Chaotic (100) rewards switching voices and
    /// shorter pieces; a seeded jitter (scaled by chaos) varies the choice deterministically. A word with no
    /// recording in the allowed takes becomes a missing piece; nothing else is ever put in its place.
    /// </summary>
    public static List<SentencePiece> Match(IReadOnlyList<SentenceToken> tokens, int a, int b, SentenceSettings settings, ISentenceCorpus corpus, string? previousVoice = null)
    {
        var pieces = new List<SentencePiece>();
        int n = b - a;
        if (n <= 0) return pieces;
        double c = Math.Clamp(settings.Chaos, 0, 100) / 100.0;
        var filter = TakeFilter.Of(settings);
        var rng = new Rng(StableHash(settings.Seed, settings.Shuffles, a, string.Join(' ', tokens.Skip(a).Take(n).Select(t => t.Key))));
        // candidates[i][L-1]: the best take of each voice for tokens [a+i, a+i+L).
        var candidates = new List<Candidate>[n][];
        for (int i = 0; i < n; i++)
        {
            candidates[i] = new List<Candidate>[Math.Min(MaxPhrase, n - i)];
            for (int L = 1; L <= candidates[i].Length; L++)
            {
                var list = candidates[i][L - 1] = [];
                var span = tokens.Skip(a + i).Take(L).ToArray();
                if (span.Any(t => t.Nonverbal))
                {
                    if (L > 1) break;
                    foreach (var named in corpus.Named(span[0].Key[1..^1], filter).OrderBy(x => x.Id, StringComparer.Ordinal))
                        list.Add(new(named.VoiceId ?? "", null, named, 0.9 + rng.Next() * 0.35 * c));
                    list = [.. list.GroupBy(x => x.Voice).Select(g => g.OrderByDescending(x => x.Score).First())];
                    candidates[i][L - 1] = list;
                    continue;
                }
                var takes = Takes(corpus, [.. span.Select(t => t.Key)], filter);
                if (takes.Count == 0) { candidates[i] = candidates[i][..L]; break; } // a longer phrase cannot exist either
                foreach (var group in Rank(takes).GroupBy(t => t.VoiceId ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    // Within a voice the best-ranked take leads; chaos lets a seeded draw pick among the top few.
                    var ranked = group.ToList();
                    int pick = c > 0 && ranked.Count > 1 ? (int)(rng.Next() * Math.Min(ranked.Count, 1 + (int)Math.Round(3 * c))) : 0;
                    var take = ranked[Math.Min(pick, ranked.Count - 1)];
                    list.Add(new(group.Key, take, null, take.Quality - (take.Altered ? 0.5 : 0) + rng.Next() * 0.35 * c));
                }
            }
        }
        // DP over positions and the voice of the last piece.
        const string Start = "\u0001";
        var best = new Dictionary<string, (double Cost, int From, string FromVoice, Candidate? Pick, int Length)>[n + 1];
        for (int i = 0; i <= n; i++) best[i] = new(StringComparer.Ordinal);
        best[0][previousVoice ?? Start] = (0, -1, "", null, 0);
        double lengthPenalty = 0.6 * c;
        double Switch(string from, string to) => from == Start || from == to ? 0 : (1 - c) * 0.8 - c * 0.9;
        for (int i = 0; i < n; i++)
        {
            foreach (var (voice, state) in best[i].OrderBy(p => p.Key, StringComparer.Ordinal).ToArray())
            {
                void Offer(int to, string next, double cost, Candidate? pick, int length)
                {
                    if (!best[to].TryGetValue(next, out var known) || cost < known.Cost - 1e-12) best[to][next] = (cost, i, voice, pick, length);
                }
                bool any = false;
                for (int L = 1; L <= candidates[i].Length; L++)
                {
                    foreach (var cand in candidates[i][L - 1] ?? [])
                    {
                        any = true;
                        double cost = state.Cost + 1 + lengthPenalty * (L - 1) - 0.25 * cand.Score + Switch(voice, cand.Voice);
                        Offer(i + L, cand.Voice, cost, cand, L);
                    }
                }
                if (!any || candidates[i].Length == 0 || candidates[i][0] is not { Count: > 0 }) Offer(i + 1, voice, state.Cost + MissingCost, null, 1);
            }
        }
        // Walk back from the cheapest end.
        var end = best[n].OrderBy(p => p.Value.Cost).ThenBy(p => p.Key, StringComparer.Ordinal).First();
        var steps = new List<(int From, int Length, Candidate? Pick)>();
        int at = n; string v = end.Key;
        while (at > 0)
        {
            var s = best[at][v];
            steps.Add((s.From, s.Length, s.Pick));
            at = s.From; v = s.FromVoice;
        }
        steps.Reverse();
        foreach (var (from, length, pick) in steps)
        {
            int ts = a + from, te = ts + length;
            pieces.Add(pick == null ? MissingPiece(tokens, ts) : pick.Named != null ? NamedPiece(pick.Named, ts, tokens[ts].Display) : TakePiece(pick.Take!, ts, te, Display(tokens, ts, te)));
        }
        return pieces;
    }

    public static SentencePiece MissingPiece(IReadOnlyList<SentenceToken> tokens, int index) => new()
    {
        Id = SentenceStore.NewPieceId(), Kind = "missing", TokenStart = index, TokenEnd = index + 1, Text = tokens[index].Display,
        Missing = "gap", DurationMs = MissingMs(tokens[index].Key),
    };
    /// <summary>A missing word keeps roughly the time it would take to say.</summary>
    public static double MissingMs(string word) => Math.Clamp(120 + 70 * word.Length, 250, 900);

    public static SentencePiece TakePiece(CorpusTake take, int start, int end, string text) => new()
    {
        Id = SentenceStore.NewPieceId(), Kind = "word", TokenStart = start, TokenEnd = end, Text = text,
        Source = SourceOf(take), GapAfterMs = Math.Clamp(take.GapAfterMs, 0, 800),
    };
    public static SentenceSource SourceOf(CorpusTake take) => new() { RecordingId = take.RecordingId, StartMs = Math.Max(0, take.StartMs - PadBeforeMs), EndMs = take.EndMs + PadAfterMs, VoiceId = take.VoiceId };
    public static SentencePiece NamedPiece(CorpusNamed named, int? token, string text) => new()
    {
        Id = SentenceStore.NewPieceId(), Kind = "named", TokenStart = token, TokenEnd = token + 1, Text = text, NamedId = named.Id,
        Source = new() { RecordingId = named.RecordingId, StartMs = named.StartMs, EndMs = named.EndMs, VoiceId = named.VoiceId }, GapAfterMs = 150,
    };

    // ---- Rematch: only the changed part --------------------------------------------------------------------------

    /// <summary>
    /// The text changed: pieces of the unchanged words before and after the change stay exactly as they were (their
    /// takes, swaps and manual placement; later words only renumbered), pieces a person inserted stay, and only the
    /// changed words (widened to whole pieces that straddled the change) are matched again.
    /// </summary>
    public static void Rematch(SentenceProject project, string newText, ISentenceCorpus corpus)
    {
        var oldTokens = Tokens(project.Text); var newTokens = Tokens(newText);
        int prefix = 0, suffix = 0;
        while (prefix < oldTokens.Count && prefix < newTokens.Count && oldTokens[prefix].Key == newTokens[prefix].Key) prefix++;
        while (suffix < oldTokens.Count - prefix && suffix < newTokens.Count - prefix && oldTokens[^(suffix + 1)].Key == newTokens[^(suffix + 1)].Key) suffix++;
        int oldEnd = oldTokens.Count - suffix, delta = newTokens.Count - oldTokens.Count;
        // The changed old range is [prefix, oldEnd); a pure insertion is empty but still splits a piece across it.
        int lo = prefix, hi = oldEnd;
        bool changed;
        do
        {
            changed = false;
            foreach (var p in project.Pieces)
            {
                if (p.TokenStart is not int s || p.TokenEnd is not int e) continue;
                bool touches = lo < hi ? s < hi && e > lo : s < lo && e > lo;
                if (touches && (s < lo || e > hi)) { lo = Math.Min(lo, s); hi = Math.Max(hi, e); changed = true; }
            }
        } while (changed);
        var kept = new List<SentencePiece>(); var gone = new List<SentencePiece>();
        SentencePiece? left = null, right = null;
        foreach (var p in project.Pieces)
        {
            if (p.TokenStart is not int s || p.TokenEnd is not int e) { kept.Add(p); continue; }
            if (e <= lo) { kept.Add(p); if (e == lo) left = p; }
            else if (s >= hi) { if (s == hi) right = p; p.TokenStart = s + delta; p.TokenEnd = e + delta; kept.Add(p); }
            else gone.Add(p); // its words changed: it goes
        }
        // Keep the text as typed for unchanged pieces (capitalisation and punctuation may have changed there).
        foreach (var p in kept) if (p.TokenStart is int s && p.TokenEnd is int e && e <= newTokens.Count && p.Kind != "pause") p.Text = Display(newTokens, s, e);
        int newLo = lo, newHi = hi + delta;
        string? before = kept.Where(p => p.TokenEnd <= newLo && p.Source != null).OrderBy(p => p.TokenEnd).LastOrDefault()?.Source?.VoiceId;
        // Words typed into a voice slot's or a phrase block's words stay in that voice (2.45): when every piece the
        // change replaced was kept to one voice, or (a pure insertion) the pieces on both sides are.
        string? voiceLock = gone.Count > 0
            ? gone.All(p => p.VoiceLock != null && p.VoiceLock == gone[0].VoiceLock) ? gone[0].VoiceLock : null
            : left?.VoiceLock is { } l && l == right?.VoiceLock ? l : null;
        var fresh = Match(newTokens, newLo, newHi, voiceLock == null ? project.Settings : Locked(project.Settings, voiceLock), corpus, before);
        foreach (var p in fresh) p.VoiceLock = voiceLock;
        project.Pieces = [.. kept, .. fresh];
        project.Text = newText;
        Layout(project);
    }

    // ---- Voice slots, phrase blocks and the caret (2.45) -----------------------------------------------------------

    /// <summary>The settings with only one voice allowed (a voice slot's or a phrase block's).</summary>
    public static SentenceSettings Locked(SentenceSettings settings, string voice)
    {
        var copy = SentenceStore.Copy(settings);
        copy.VoiceIds = [voice];
        return copy;
    }
    /// <summary>Which takes may play a piece: its voice slot's voice when it has one, otherwise the sentence's mix.</summary>
    public static TakeFilter FilterFor(SentenceProject project, SentencePiece piece) =>
        piece.VoiceLock is { } voice ? new(new HashSet<string>(StringComparer.Ordinal) { voice }, project.Settings.Altered) : TakeFilter.Of(project.Settings);

    private static readonly System.Text.RegularExpressions.Regex VoiceMark = new(@"\{@([a-f0-9]{32})?\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A filled template's text with voice marks: {@&lt;voice id&gt;} says the words after it are that voice's (a voice
    /// slot, or a phrase block's voice), {@} gives them back to the sentence's mix. Returns the text without the marks
    /// and its words in runs, each with its voice (null: the mix). Marks are only read in a template's text.
    /// </summary>
    public static (string Text, List<(int Start, int End, string? Voice)> Runs) VoiceMarked(string marked)
    {
        var parts = new List<(string Text, string? Voice)>(); string? voice = null; int at = 0;
        foreach (System.Text.RegularExpressions.Match m in VoiceMark.Matches(marked))
        {
            parts.Add((marked[at..m.Index], voice));
            voice = m.Groups[1].Success ? m.Groups[1].Value : null;
            at = m.Index + m.Length;
        }
        parts.Add((marked[at..], voice));
        var text = new System.Text.StringBuilder(); var runs = new List<(int Start, int End, string? Voice)>(); int words = 0;
        foreach (var (part, partVoice) in parts)
        {
            var clean = part.Trim();
            if (clean.Length == 0) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(clean);
            int count = Tokens(text.ToString()).Count - words;
            if (count <= 0) continue;
            if (runs.Count > 0 && runs[^1].End == words && runs[^1].Voice == partVoice) runs[^1] = (runs[^1].Start, words + count, partVoice);
            else runs.Add((words, words + count, partVoice));
            words += count;
        }
        return (text.ToString(), runs);
    }

    /// <summary>
    /// A new sentence from runs of words, each matched within its own voice (a voice slot's) or the sentence's mix.
    /// Pieces of a run with a voice are kept to it (<see cref="SentencePiece.VoiceLock"/>).
    /// </summary>
    public static void Compose(SentenceProject project, string text, IReadOnlyList<(int Start, int End, string? Voice)> runs, ISentenceCorpus corpus)
    {
        var tokens = Tokens(text); var pieces = new List<SentencePiece>(); string? before = null;
        foreach (var (start, end, voice) in runs)
        {
            int stop = Math.Min(end, tokens.Count);
            if (stop <= start) continue;
            var fresh = Match(tokens, start, stop, voice == null ? project.Settings : Locked(project.Settings, voice), corpus, before);
            foreach (var p in fresh) p.VoiceLock = voice;
            pieces.AddRange(fresh);
            before = fresh.LastOrDefault(p => p.Source != null)?.Source?.VoiceId ?? before;
        }
        project.Text = text; project.Pieces = pieces;
        Layout(project);
    }

    /// <summary>
    /// Puts words into the text at a caret, a character position (2.45: palette "Add to sentence" and phrase blocks).
    /// A caret inside a word puts them after that word, and one inside a recorded phrase after the phrase, so no piece
    /// is split. Nothing is matched or played here: later pieces only move along, and the caller adds the piece(s) for
    /// the new words, [Token, Token + Count). Refused when the text would grow past its limits.
    /// </summary>
    public static (int Token, int Count) InsertText(SentenceProject project, int caret, string words)
    {
        int count = Tokens(words).Count;
        if (count == 0) throw new InvalidDataException("There is nothing to add.");
        var text = project.Text ?? "";
        var existing = Words(text).ToList();
        if (existing.Count + count > SentenceStore.MaxTokens) throw new InvalidDataException($"A sentence has at most {SentenceStore.MaxTokens} words.");
        caret = Math.Clamp(caret, 0, text.Length);
        int at = existing.Count(w => w.Start < caret);
        for (bool moved = true; moved;)
        {
            moved = false;
            foreach (var p in project.Pieces)
                if (p.TokenStart is int s && p.TokenEnd is int e && s < at && at < e) { at = e; moved = true; }
        }
        at = Math.Min(at, existing.Count);
        words = words.Trim();
        string next = at == 0 ? (text.Trim().Length == 0 ? words : words + " " + text) : text[..existing[at - 1].End] + " " + words + text[existing[at - 1].End..];
        if (next.Length > SentenceStore.MaxText) throw new InvalidDataException($"A sentence is at most {SentenceStore.MaxText} characters.");
        var after = Tokens(next);
        if (after.Count != existing.Count + count) throw new InvalidDataException("Those words could not be added there.");
        foreach (var p in project.Pieces)
            if (p.TokenStart is int s && p.TokenEnd is int e && s >= at) { p.TokenStart = s + count; p.TokenEnd = e + count; }
        project.Text = next;
        return (at, count);
    }

    /// <summary>
    /// A person changed the mix (Smooth–Chaotic, the voice filters, altered takes): the automatic choices are made
    /// again. What a person decided stays: hand-placed pieces, swapped takes, chosen substitutes, approved splices,
    /// deleted words (now pauses), inserted pieces, and each still-missing word's Gap or Bleep.
    /// </summary>
    public static void Recompose(SentenceProject project, ISentenceCorpus corpus)
    {
        var tokens = Tokens(project.Text);
        // 2.45: words a voice slot or a phrase block kept to one voice are decided too (the mix does not reach them).
        static bool Decided(SentencePiece p) => p.TokenStart is not int || p.Manual || p.Chosen || p.Kind is "pause" or "splice" || p.Substitute != null || p.VoiceLock != null;
        var kept = project.Pieces.Where(Decided).ToList();
        var missing = project.Pieces.Where(p => p.Kind == "missing" && p.TokenStart is int).ToDictionary(p => p.TokenStart!.Value);
        var covered = new bool[tokens.Count];
        foreach (var p in kept) if (p.TokenStart is int s && p.TokenEnd is int e) for (int i = s; i < Math.Min(e, tokens.Count); i++) covered[i] = true;
        var fresh = new List<SentencePiece>();
        for (int i = 0; i < tokens.Count;)
        {
            if (covered[i]) { i++; continue; }
            int j = i; while (j < tokens.Count && !covered[j]) j++;
            string? before = kept.Concat(fresh).Where(p => p.TokenEnd <= i && p.Source != null).OrderBy(p => p.TokenEnd).LastOrDefault()?.Source?.VoiceId;
            foreach (var p in Match(tokens, i, j, project.Settings, corpus, before))
            {
                if (p.Kind == "missing" && missing.TryGetValue(p.TokenStart!.Value, out var old)) { p.Id = old.Id; p.Missing = old.Missing; p.DurationMs = old.DurationMs; }
                fresh.Add(p);
            }
            i = j;
        }
        project.Pieces = [.. kept, .. fresh];
        Layout(project);
    }

    /// <summary>"Find missing again": missing words are looked up once more (on request only; never by itself).</summary>
    public static int FindMissingAgain(SentenceProject project, ISentenceCorpus corpus)
    {
        var tokens = Tokens(project.Text);
        var missing = project.Pieces.Where(p => p.Kind == "missing" && p.TokenStart is int).OrderBy(p => p.TokenStart).ToList();
        int found = 0;
        foreach (var run in Runs(missing))
        {
            int a = run[0].TokenStart!.Value, b = run[^1].TokenEnd!.Value;
            if (b > tokens.Count) continue;
            var voiceLock = run[0].VoiceLock;
            var fresh = Match(tokens, a, b, voiceLock == null ? project.Settings : Locked(project.Settings, voiceLock), corpus);
            if (fresh.All(p => p.Kind == "missing")) continue;
            foreach (var p in fresh) p.VoiceLock = voiceLock;
            // Keep each still-missing word's Gap or Bleep choice.
            foreach (var p in fresh.Where(p => p.Kind == "missing")) if (run.FirstOrDefault(r => r.TokenStart == p.TokenStart) is { } old) { p.Missing = old.Missing; p.DurationMs = old.DurationMs; p.Id = old.Id; }
            found += fresh.Count(p => p.Kind != "missing");
            project.Pieces.RemoveAll(run.Contains);
            project.Pieces.AddRange(fresh);
        }
        if (found > 0) Layout(project);
        return found;
    }
    private static IEnumerable<List<SentencePiece>> Runs(List<SentencePiece> missing)
    {
        var run = new List<SentencePiece>();
        foreach (var p in missing)
        {
            if (run.Count > 0 && (run[^1].TokenEnd != p.TokenStart || run[^1].VoiceLock != p.VoiceLock)) { yield return run; run = []; }
            run.Add(p);
        }
        if (run.Count > 0) yield return run;
    }

    // ---- Layout ------------------------------------------------------------------------------------------------

    public static double SpeedOf(PieceEffect? e) => e == null || e.Speed <= 0 ? 1 : e.Speed;
    /// <summary>How long a piece lasts on the timeline.</summary>
    public static double Duration(SentencePiece p) => p.Kind switch
    {
        "word" or "named" => (p.Source!.EndMs - p.Source.StartMs) / SpeedOf(p.Effect),
        "splice" => p.Parts!.Sum(x => x.EndMs - x.StartMs) / SpeedOf(p.Effect),
        _ => p.DurationMs,
    };
    private static double GapAfter(SentencePiece p, SentenceSettings s) => p.Kind is "pause" or "missing" ? 0 : s.Timing == "original" ? Math.Clamp(p.GapAfterMs, 0, 800) : FluentGapMs;

    /// <summary>
    /// Places the pieces nobody placed by hand one after another in the order of their words, after whatever comes
    /// before them (a hand-placed piece included). Hand-placed pieces never move.
    /// </summary>
    public static void Layout(SentenceProject project, string? timing = null)
    {
        var settings = timing == null ? project.Settings : new SentenceSettings { Timing = timing };
        double cursor = 0;
        foreach (var p in project.Pieces.Where(p => p.TokenStart is int).OrderBy(p => p.TokenStart).ThenBy(p => p.OffsetMs))
        {
            if (!p.Manual) p.OffsetMs = Math.Min(cursor, SentenceStore.MaxTimelineMs);
            cursor = p.OffsetMs + Duration(p) + GapAfter(p, settings);
        }
        project.Pieces.Sort((x, y) => x.OffsetMs.CompareTo(y.OffsetMs) is int byTime && byTime != 0 ? byTime : (x.TokenStart ?? int.MaxValue).CompareTo(y.TokenStart ?? int.MaxValue));
    }

    // ---- Alternatives, Swap and Shuffle ---------------------------------------------------------------------------

    /// <summary>Other recorded takes of exactly the same words (or other pieces with the same name), best first.</summary>
    public static List<CorpusTake> Alternatives(SentenceProject project, SentencePiece piece, ISentenceCorpus corpus)
    {
        var filter = FilterFor(project, piece);
        if (piece.Kind == "named")
        {
            var name = Tokens(piece.Text).FirstOrDefault().Key ?? "";
            return [.. corpus.Named(name.Length > 2 ? name[1..^1] : name, filter).OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => new CorpusTake(x.RecordingId, x.VoiceId, -1, 1, x.StartMs, x.EndMs, 150, "[" + x.Name + "]", 1, x.Altered, 0))];
        }
        string[] span = piece.Substitute != null ? [WordIndex.Token(piece.Substitute)] : SpanOf(project, piece);
        return span.Length == 0 ? [] : Rank(corpus.Find(span, filter, 400));
    }
    private static string[] SpanOf(SentenceProject project, SentencePiece piece)
    {
        if (piece.TokenStart is not int s || piece.TokenEnd is not int e) return [.. WordIndex.Query(piece.Text)];
        var tokens = Tokens(project.Text);
        return e <= tokens.Count ? [.. tokens.Skip(s).Take(e - s).Select(t => t.Key)] : [];
    }
    private static bool Same(SentenceSource? s, CorpusTake t) => s != null && s.RecordingId == t.RecordingId && s.StartMs == Math.Max(0, t.StartMs - PadBeforeMs) && s.EndMs == t.EndMs + PadAfterMs;
    private static bool SameNamed(SentenceSource? s, CorpusTake t) => s != null && s.RecordingId == t.RecordingId && s.StartMs == t.StartMs && s.EndMs == t.EndMs;

    /// <summary>The index of the piece's current take among its alternatives (-1 when it is not one of them).</summary>
    public static int CurrentAlternative(SentencePiece piece, List<CorpusTake> alternatives) =>
        alternatives.FindIndex(t => piece.Kind == "named" ? SameNamed(piece.Source, t) : Same(piece.Source, t));

    private static void UseTake(SentencePiece piece, CorpusTake take)
    {
        if (piece.Kind == "named") piece.Source = new() { RecordingId = take.RecordingId, StartMs = take.StartMs, EndMs = take.EndMs, VoiceId = take.VoiceId };
        else { piece.Source = SourceOf(take); piece.GapAfterMs = Math.Clamp(take.GapAfterMs, 0, 800); }
    }

    /// <summary>Swap: the next take (or the one asked for). The person chose it, so it is kept through later edits.</summary>
    public static bool Swap(SentenceProject project, SentencePiece piece, ISentenceCorpus corpus, int? index = null)
    {
        if (piece.Kind is not ("word" or "named")) return false;
        var list = Alternatives(project, piece, corpus);
        if (list.Count == 0) return false;
        int current = CurrentAlternative(piece, list);
        int next = index is int wanted ? wanted : (current + 1) % list.Count;
        if (next < 0 || next >= list.Count) throw new InvalidDataException("That take is no longer in the voice library.");
        UseTake(piece, list[next]);
        piece.Chosen = true;
        if (!piece.Manual) Layout(project);
        return true;
    }

    /// <summary>
    /// Shuffle voices: the given pieces (all recorded pieces when none are given) take a take of another voice
    /// where one exists, drawn from the seed and the number of shuffles so far (so it is repeatable).
    /// </summary>
    public static int Shuffle(SentenceProject project, IReadOnlyCollection<string>? pieceIds, ISentenceCorpus corpus)
    {
        project.Settings.Shuffles++;
        var rng = new Rng(StableHash(project.Settings.Seed, "shuffle", project.Settings.Shuffles));
        int changed = 0;
        foreach (var piece in project.Pieces.Where(p => p.Kind is "word" or "named" && (pieceIds == null || pieceIds.Count == 0 || pieceIds.Contains(p.Id))).OrderBy(p => p.OffsetMs).ToList())
        {
            var list = Alternatives(project, piece, corpus);
            var others = list.Where(t => (t.VoiceId ?? "") != (piece.Source?.VoiceId ?? "")).ToList();
            var pool = others.Count > 0 ? others : list.Where(t => !(piece.Kind == "named" ? SameNamed(piece.Source, t) : Same(piece.Source, t))).ToList();
            if (pool.Count == 0) continue;
            UseTake(piece, pool[(int)(rng.Next() * pool.Count) % pool.Count]);
            changed++;
        }
        Layout(project);
        return changed;
    }

    // ---- Contrast suggestions ---------------------------------------------------------------------------------------

    private static readonly HashSet<string> Unsettling = new(StringComparer.Ordinal)
    {
        "behind", "die", "dead", "death", "kill", "blood", "watching", "watch", "alone", "dark", "scream", "run", "hide", "help", "never", "forever",
        "coming", "come", "found", "find", "hungry", "teeth", "bones", "grave", "hello", "nobody", "something", "under", "stay", "closer", "please",
    };
    /// <summary>A line that may sound more unsettling with contrasting deliveries (a suggestion only; nothing changes by itself).</summary>
    public static bool SoundsUnsettling(string text) => Tokens(text).Count(t => Unsettling.Contains(t.Key)) >= 1 && Tokens(text).Count >= 3;

    /// <summary>
    /// Contrasting deliveries: each recorded piece takes the take that differs most from the one before it in
    /// pace (and voice, where another voice has it). Applied only when a person asks; Swap still overrides it.
    /// </summary>
    public static int Contrast(SentenceProject project, ISentenceCorpus corpus)
    {
        double? previousPace = null; string? previousVoice = null; int changed = 0;
        foreach (var piece in project.Pieces.Where(p => p.Kind == "word" && p.TokenStart is int).OrderBy(p => p.TokenStart).ToList())
        {
            var list = Alternatives(project, piece, corpus);
            if (list.Count == 0) continue;
            double Pace(CorpusTake t) => (t.EndMs - t.StartMs) / Math.Max(1.0, t.Text.Length);
            var pick = previousPace is double pp
                ? list.OrderByDescending(t => Math.Abs(Pace(t) - pp) + ((t.VoiceId ?? "") != (previousVoice ?? "") ? 40 : 0)).ThenBy(t => t.RecordingId, StringComparer.Ordinal).First()
                : list[0];
            if (!Same(piece.Source, pick)) { UseTake(piece, pick); changed++; }
            previousPace = Pace(pick); previousVoice = pick.VoiceId;
        }
        Layout(project);
        return changed;
    }

    // ---- Missing words: similar words and splices (offered only) ------------------------------------------------

    public static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int previous = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int temp = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = temp;
            }
        }
        return row[b.Length];
    }

    /// <summary>Recorded words that look like the missing one (close spelling or the same start), for a deliberate choice.</summary>
    public static List<(string Word, int Count)> Similar(string word, ISentenceCorpus corpus, TakeFilter filter, int max = 8)
    {
        word = WordIndex.Token(word);
        if (word.Length == 0) return [];
        int allowed = Math.Max(1, word.Length / 3);
        string stem = word.Length >= 4 ? word[..4] : word;
        return [.. corpus.Vocabulary(filter).Where(p => p.Key != word && (Distance(p.Key, word) <= allowed || word.Length >= 4 && p.Key.StartsWith(stem, StringComparison.Ordinal)))
            .OrderBy(p => Distance(p.Key, word)).ThenByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(max).Select(p => (p.Key, p.Value))];
    }

    /// <summary>
    /// Candidate splices for a missing word: its first letters from one recorded word and the rest from another
    /// (a whole word where one matches, otherwise the matching share of a longer word's time). Only offered; a
    /// person must approve one, and it stays marked as a splice.
    /// </summary>
    public static List<SpliceCandidate> Splices(string word, ISentenceCorpus corpus, TakeFilter filter, int max = 4)
    {
        word = WordIndex.Token(word);
        var result = new List<(double Score, SpliceCandidate Candidate)>();
        if (word.Length < 4 || !word.All(char.IsLetter)) return [];
        var vocabulary = corpus.Vocabulary(filter).Keys.Where(k => k.All(char.IsLetter)).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).ToList();
        for (int k = 2; k <= word.Length - 2; k++)
        {
            string left = word[..k], right = word[k..];
            string? leftWord = vocabulary.FirstOrDefault(v => v == left) ?? vocabulary.FirstOrDefault(v => v.Length > left.Length && v.StartsWith(left, StringComparison.Ordinal));
            string? rightWord = vocabulary.FirstOrDefault(v => v == right) ?? vocabulary.FirstOrDefault(v => v.Length > right.Length && v.EndsWith(right, StringComparison.Ordinal));
            if (leftWord == null || rightWord == null) continue;
            var leftTake = Rank(corpus.Find([leftWord], filter, 50)).FirstOrDefault();
            var rightTake = Rank(corpus.Find([rightWord], filter, 50)).FirstOrDefault();
            if (leftTake == null || rightTake == null) continue;
            long leftLength = leftTake.EndMs - leftTake.StartMs, rightLength = rightTake.EndMs - rightTake.StartMs;
            var first = new SentenceSource { RecordingId = leftTake.RecordingId, StartMs = Math.Max(0, leftTake.StartMs - PadBeforeMs), EndMs = leftTake.StartMs + (leftWord == left ? leftLength : Math.Max(30, leftLength * left.Length / leftWord.Length)), VoiceId = leftTake.VoiceId, Label = $"“{left}” of “{leftWord}”" };
            var second = new SentenceSource { RecordingId = rightTake.RecordingId, StartMs = rightTake.EndMs - (rightWord == right ? rightLength : Math.Max(30, rightLength * right.Length / rightWord.Length)), EndMs = rightTake.EndMs + PadAfterMs, VoiceId = rightTake.VoiceId, Label = $"“{right}” of “{rightWord}”" };
            if (second.StartMs >= second.EndMs || first.StartMs >= first.EndMs) continue;
            double score = (leftWord == left ? 2 : 1) + (rightWord == right ? 2 : 1) - Math.Abs(k - word.Length / 2.0) * 0.1;
            result.Add((score, new($"“{left}” from “{leftWord}” + “{right}” from “{rightWord}”", [first, second])));
        }
        return [.. result.OrderByDescending(r => r.Score).ThenBy(r => r.Candidate.Description, StringComparer.Ordinal).Take(max).Select(r => r.Candidate)];
    }

    /// <summary>A person chose a similar recorded word for a missing one: it plays that word and stays marked as a substitute.</summary>
    public static void UseSimilar(SentenceProject project, SentencePiece piece, string word, ISentenceCorpus corpus)
    {
        var token = WordIndex.Token(word);
        var take = Rank(corpus.Find([token], FilterFor(project, piece), 50)).FirstOrDefault() ?? throw new InvalidDataException("That word is no longer in the voice library.");
        piece.Kind = "word"; piece.Missing = null; piece.Substitute = token; piece.Parts = null; piece.Chosen = true;
        UseTake(piece, take);
        if (!piece.Manual) Layout(project);
    }

    /// <summary>A person approved a splice candidate.</summary>
    public static void UseSplice(SentenceProject project, SentencePiece piece, SpliceCandidate candidate)
    {
        piece.Kind = "splice"; piece.Missing = null; piece.Substitute = null; piece.Source = null; piece.Chosen = true;
        piece.Parts = [.. candidate.Parts.Select(SentenceStore.Copy)];
        piece.GapAfterMs = 80;
        if (!piece.Manual) Layout(project);
    }

    /// <summary>Back to a plain missing word (Gap or Bleep), undoing a substitute or a splice.</summary>
    public static void MakeMissing(SentenceProject project, SentencePiece piece, string mode)
    {
        if (mode is not ("gap" or "bleep")) throw new InvalidDataException("A missing word is a gap or a bleep.");
        var tokens = Tokens(project.Text);
        if (piece.Kind != "missing")
        {
            if (piece.TokenStart is not int s || piece.TokenEnd != s + 1 || piece.Substitute == null && piece.Kind != "splice") throw new InvalidDataException("Only a missing word can be a gap or a bleep.");
            piece.Kind = "missing"; piece.Source = null; piece.Parts = null; piece.Substitute = null; piece.Chosen = false;
            piece.DurationMs = s < tokens.Count ? MissingMs(tokens[s].Key) : 400;
        }
        piece.Missing = mode;
        if (!piece.Manual) Layout(project);
    }
}
