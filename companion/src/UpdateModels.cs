namespace SoundpadWeb;

/// <summary>The updater's state as the host's pages see it (2.44, gate updater); stripped for guests.</summary>
public record UpdateStatus(string State, string Current, string? Latest = null, string? NotesUrl = null, long? Bytes = null, long? Total = null, string? Error = null, string? Reason = null, bool? Offered = null);
