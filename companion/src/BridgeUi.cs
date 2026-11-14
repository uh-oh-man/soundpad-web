using System.Text.Json;
namespace SoundpadWeb;

// Host style sync (2.43, gate uiSync): the host's browser and the companion window share their style (navigation,
// theme, motion, contrast, header animation) and Mimic's open draft through ui.json. Host only.
public sealed partial class Bridge
{
    private UiPrefsStore? uiPrefs;
    private UiPrefsStore UiPrefs => uiPrefs ??= new(Path.Combine(store.DirectoryPath, "ui.json"));
    /// <summary>Rises when ui.json changes, so the host's other pages fetch it (snapshot uiRevision). In memory only.</summary>
    private long uiRevision;

    private Reply? ExecuteUiPrefs(Command c, string actor)
    {
        if (c.Op is not ("uiPrefsGet" or "uiPrefsSet")) return null;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            if (actor != "host") throw new InvalidDataException("Host permission required.");
            if (c.Op == "uiPrefsGet") { var (prefs, saved) = UiPrefs.Read(); return new("ack", c.Id, true, Data: new { prefs, saved }); }
            if (c.UiPrefs is not { } input) throw new InvalidDataException("Send the style settings to change.");
            var next = UiPrefs.Set(input);
            Interlocked.Increment(ref uiRevision);
            Publish();
            return new("ack", c.Id, true, Data: new { prefs = next, saved = true });
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new("ack", c.Id, false, "The shared style could not be saved."); }
    }
}
