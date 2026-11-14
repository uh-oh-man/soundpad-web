namespace SoundpadWeb;

/// <summary>
/// Word-level inverted index over the voice library and the indexed Soundpad categories (2.41). Each document is a
/// sequence of tokens with a stable key ("r:&lt;recording id&gt;" or "s:&lt;sound file&gt;"); a token's position is the
/// index of its word in that document, so a hit names the exact word whose timing plays it. Documents are added,
/// replaced and removed one at a time: nothing is rebuilt when one recording changes.
/// </summary>
public sealed class WordIndex
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> handles = new(StringComparer.Ordinal);
    private readonly List<string?> keys = [];
    private readonly List<string[]?> documents = [];
    private readonly Stack<int> free = new();
    /// <summary>token → document handle → positions</summary>
    private readonly Dictionary<string, Dictionary<int, List<int>>> postings = new(StringComparer.Ordinal);

    public const int MaxQueryTokens = 12;

    /// <summary>The searchable form of a word: lower case, curly apostrophes straightened, punctuation at either end dropped.</summary>
    public static string Token(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Replace('’', '\'').Replace('‘', '\'').ToLowerInvariant();
        int a = 0, z = s.Length;
        while (a < z && !char.IsLetterOrDigit(s[a])) a++;
        while (z > a && !char.IsLetterOrDigit(s[z - 1])) z--;
        return a < z ? s[a..z] : "";
    }

    /// <summary>A typed word or phrase as tokens (empty pieces dropped, at most <see cref="MaxQueryTokens"/>).</summary>
    public static string[] Query(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Token).Where(t => t.Length > 0).Take(MaxQueryTokens)];

    public int Count { get { lock (gate) return handles.Count; } }

    /// <summary>Adds or replaces a document.</summary>
    public void Set(string key, IReadOnlyList<string> words)
    {
        var tokens = words.Select(Token).ToArray();
        lock (gate)
        {
            RemoveLocked(key);
            int handle = free.Count > 0 ? free.Pop() : keys.Count;
            if (handle == keys.Count) { keys.Add(key); documents.Add(tokens); }
            else { keys[handle] = key; documents[handle] = tokens; }
            handles[key] = handle;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Length == 0) continue;
                if (!postings.TryGetValue(tokens[i], out var docs)) postings[tokens[i]] = docs = [];
                if (!docs.TryGetValue(handle, out var positions)) docs[handle] = positions = [];
                positions.Add(i);
            }
        }
    }

    public void Remove(string key) { lock (gate) RemoveLocked(key); }

    private void RemoveLocked(string key)
    {
        if (!handles.Remove(key, out int handle)) return;
        foreach (var token in documents[handle]!.Distinct())
            if (token.Length > 0 && postings.TryGetValue(token, out var docs) && docs.Remove(handle) && docs.Count == 0) postings.Remove(token);
        keys[handle] = null; documents[handle] = null; free.Push(handle);
    }

    public bool Contains(string key) { lock (gate) return handles.ContainsKey(key); }

    /// <summary>Every place the exact phrase occurs: (document key, first word index). Documents filtered by <paramref name="include"/>.</summary>
    public List<(string Key, int Position)> Find(string[] tokens, Func<string, bool>? include = null, int max = int.MaxValue)
    {
        var result = new List<(string, int)>();
        if (tokens.Length == 0) return result;
        lock (gate)
        {
            // Start from the rarest token of the phrase.
            int pivot = 0; int best = int.MaxValue;
            for (int i = 0; i < tokens.Length; i++)
            {
                int n = postings.TryGetValue(tokens[i], out var d) ? d.Count : 0;
                if (n == 0) return result;
                if (n < best) { best = n; pivot = i; }
            }
            foreach (var (handle, positions) in postings[tokens[pivot]])
            {
                var key = keys[handle]!;
                if (include != null && !include(key)) continue;
                var doc = documents[handle]!;
                foreach (int p in positions)
                {
                    int start = p - pivot;
                    if (start < 0 || start + tokens.Length > doc.Length) continue;
                    bool all = true;
                    for (int k = 0; k < tokens.Length && all; k++) all = doc[start + k] == tokens[k];
                    if (!all) continue;
                    result.Add((key, start));
                    if (result.Count >= max) return result;
                }
            }
        }
        return result;
    }

    /// <summary>Distinct tokens with how often they occur, over the documents <paramref name="include"/> accepts.</summary>
    public Dictionary<string, int> Vocabulary(Func<string, bool> include)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (gate)
            foreach (var (key, handle) in handles)
            {
                if (!include(key)) continue;
                foreach (var token in documents[handle]!) if (token.Length > 0) counts[token] = counts.GetValueOrDefault(token) + 1;
            }
        return counts;
    }
}
