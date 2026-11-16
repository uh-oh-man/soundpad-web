using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace SoundpadWeb;

/// <summary>
/// Feedback (2.45): the host and guests send short notes (a bug, an idea, an opinion) from the header's Feedback
/// button. Every note is appended to one file on the host's desktop, always with the same name, so the owner and
/// Claude know where to read them. Guests never learn the path; the file is only ever appended to.
/// </summary>
public sealed partial class Bridge
{
    public const string FeedbackFileName = "Soundpad Web feedback.md";
    public const int FeedbackMaxText = 4000;
    /// <summary>The file stops taking notes past this size, so a flood can never fill the desktop's drive.</summary>
    public const long FeedbackMaxFileBytes = 8L * 1024 * 1024;
    private const int FeedbackPerWindow = 10;
    private static readonly TimeSpan FeedbackWindow = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> feedbackTimes = new();
    private readonly object feedbackLock = new();

    /// <summary>Test seam: where the feedback file is written. Defaults to the user's desktop.</summary>
    public string FeedbackFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string FeedbackPath => Path.Combine(FeedbackFolder, FeedbackFileName);

    private static readonly Dictionary<string, string> FeedbackKinds = new(StringComparer.Ordinal)
    {
        ["bug"] = "Bug",
        ["idea"] = "Idea",
        ["opinion"] = "Opinion",
    };

    private Reply? ExecuteFeedback(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        if (c.Op != "feedbackSend") return null;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && (audioAudience != null && (!sessionAccepting || audience != audioAudience) || requester != null && revokedAudioPeers.ContainsKey(requester))) throw new InvalidDataException("This guest session authorization changed.");
            if (c.Body is not { ValueKind: JsonValueKind.Object } body) throw new InvalidDataException("Write a note first.");
            string kind = body.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() is { } ks && FeedbackKinds.ContainsKey(ks) ? ks : throw new InvalidDataException("Choose Bug, Idea or Opinion.");
            string text = body.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "").Trim() : "";
            if (text.Length == 0) throw new InvalidDataException("Write a note first.");
            if (text.Length > FeedbackMaxText) throw new InvalidDataException($"Keep a note under {FeedbackMaxText} characters.");
            string? where = body.TryGetProperty("where", out var w) && w.ValueKind == JsonValueKind.String ? Clean(w.GetString() ?? "", 80) : null;
            string? page = body.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString() ?? "", 24) : null;
            string who = host ? "Host" : Clean(participant?.Name ?? "", 40) is { Length: > 0 } n ? n : "A guest";
            string person = PersonKey(actor, participant, requester);
            if (!host && !FeedbackAllowed(person)) throw new InvalidDataException("That's a lot of notes at once. Wait a few minutes, then send again.");
            AppendFeedback(new FeedbackNote(DateTimeOffset.Now, who, host ? "host" : "guest", FeedbackKinds[kind], text, where, page));
            Log($"Saved {(host ? "the host's" : "a guest's")} feedback note");
            return new("ack", c.Id, true, Data: new { saved = true, file = host ? FeedbackPath : null });
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not save a feedback note: {ex.Message}");
            return new("ack", c.Id, false, actor == "host" ? $"The note could not be saved to the desktop: {ex.Message}" : "The host's companion could not save the note. Try again in a moment.");
        }
    }

    private bool FeedbackAllowed(string person)
    {
        var times = feedbackTimes.GetOrAdd(person, _ => new Queue<DateTimeOffset>());
        lock (times)
        {
            var now = DateTimeOffset.UtcNow;
            while (times.Count > 0 && now - times.Peek() > FeedbackWindow) times.Dequeue();
            if (times.Count >= FeedbackPerWindow) return false;
            times.Enqueue(now);
            return true;
        }
    }

    public sealed record FeedbackNote(DateTimeOffset At, string From, string Role, string Kind, string Text, string? Where, string? Version);

    /// <summary>Appends one note. The file is created with a short header the first time; it is never rewritten.</summary>
    public void AppendFeedback(FeedbackNote note)
    {
        lock (feedbackLock)
        {
            Directory.CreateDirectory(FeedbackFolder);
            var path = FeedbackPath;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > FeedbackMaxFileBytes) throw new IOException("the feedback file is full (8 MB); move or rename it to start a new one");
            var sb = new StringBuilder();
            if (!info.Exists || info.Length == 0)
                sb.Append("# Soundpad Web feedback\n\nNotes sent with the Feedback button in Soundpad Web, newest at the bottom. The companion only ever adds to this file.\n");
            sb.Append('\n').Append("## ").Append(note.At.ToString("yyyy-MM-dd HH:mm")).Append(" · ").Append(note.Kind).Append(" · ").Append(note.From)
                .Append(note.Role == "guest" ? " (guest)" : "").Append('\n');
            var meta = new List<string>();
            if (note.Where is { Length: > 0 }) meta.Add($"Where: {note.Where}");
            if (note.Version is { Length: > 0 }) meta.Add($"Website {note.Version}");
            meta.Add($"Companion {typeof(Bridge).Assembly.GetName().Version?.ToString(3)}");
            sb.Append('\n').Append('_').Append(string.Join(" · ", meta)).Append("_\n\n");
            foreach (var line in note.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                sb.Append("> ").Append(line).Append('\n');
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                    file.Write(bytes);
                    file.Flush(true);
                    return;
                }
                catch (IOException) when (attempt < 20 && File.Exists(path)) { Thread.Sleep(50); }
            }
        }
    }

    /// <summary>One line of plain text: no control characters, no Markdown heading or quote breaks.</summary>
    private static string Clean(string value, int max)
    {
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim();
    }
}
