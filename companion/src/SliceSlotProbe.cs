using System.Diagnostics;
namespace SoundpadWeb;

public enum SlotProbeOutcome
{
    /// <summary>Nothing was written or played: the player was not stopped, or the path is not a slot file.</summary>
    Refused,
    /// <summary>The reported duration followed the file both times: the player reads a changed file again on play.</summary>
    Reread,
    /// <summary>The second play still reported the first length: the player keeps what it read the first time.</summary>
    NotReread,
    /// <summary>The slot file could not be rewritten (the player kept it open). Which write failed is in the detail.</summary>
    WriteBlocked,
    /// <summary>The durations fit neither answer, or the player did not stop in time. Read the numbers.</summary>
    Inconclusive,
}

public sealed record SlotProbeOptions
{
    /// <summary>Length of the first silence written.</summary>
    public int FirstMs { get; init; } = 400;
    /// <summary>Length of the second silence written. Far enough from the first that rounding cannot confuse them.</summary>
    public int SecondMs { get; init; } = 900;
    /// <summary>How far a reported duration may be from the written length and still count as that length.</summary>
    public int ToleranceMs { get; init; } = 60;
    /// <summary>Time between two looks at the player.</summary>
    public int PollMs { get; init; } = 50;
    /// <summary>How long to wait, beyond the silence's own length, for the player to be stopped again.</summary>
    public int StopTimeoutMs { get; init; } = 4000;
    public int Rate { get; init; } = 48000;
    public int Channels { get; init; } = 2;
    public int WriteAttempts { get; init; } = 6;
    public int RetryDelayMs { get; init; } = 25;
}

public sealed record SlotProbeResult(SlotProbeOutcome Outcome, string Detail)
{
    /// <summary>True only when the player demonstrably re-read the changed file.</summary>
    public bool Reread => Outcome == SlotProbeOutcome.Reread;
    public string? StatusBefore { get; init; }
    public long FirstWrittenMs { get; init; }
    public long SecondWrittenMs { get; init; }
    /// <summary>The duration the player reported for each play (the verdict is based on these).</summary>
    public long? FirstReportedMs { get; init; }
    public long? SecondReportedMs { get; init; }
    /// <summary>
    /// A second, independent sign: how long the player stayed busy, from the play command until it was seen
    /// stopped again (so it includes up to one poll interval). Null when it was never seen playing or never
    /// stopped. A player could report a remembered duration yet play the new audio, or the other way round;
    /// these numbers show which.
    /// </summary>
    public long? FirstPlayedMs { get; init; }
    public long? SecondPlayedMs { get; init; }
    /// <summary>Open attempts each write needed (more than 1 means the player was holding the file for a while).</summary>
    public int FirstWriteAttempts { get; init; }
    public int SecondWriteAttempts { get; init; }
}

/// <summary>
/// Answers the question the slot design rests on: when a file that is already in the player's library changes on
/// disk, does the next play use the new contents? It writes silence of one length into a slot, plays it, reads
/// the duration the player reports, writes silence of another length, plays again and compares.
///
/// It is safe to run on a live system: it refuses unless the player is stopped, it only writes to slot files, and
/// everything it writes is pure digital silence, so nothing can be heard.
/// </summary>
public static class SlotProbe
{
    /// <param name="playPath">Asks the player to play the library entry registered for this path (the slot must already be registered).</param>
    /// <param name="readDurationMs">The duration the player reports for what it is playing, or null (or 0) when it reports none.</param>
    /// <param name="readStatus">The player's status text; "STOPPED" (any case) means idle.</param>
    /// <param name="slotPath">Full path of the slot file, named like <see cref="SlotPool.FileName"/>.</param>
    public static async Task<SlotProbeResult> Run(Func<string, Task> playPath, Func<Task<long?>> readDurationMs, Func<Task<string>> readStatus, string slotPath, SlotProbeOptions? options = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(playPath); ArgumentNullException.ThrowIfNull(readDurationMs); ArgumentNullException.ThrowIfNull(readStatus); ArgumentNullException.ThrowIfNull(slotPath);
        options ??= new();
        if (options.FirstMs < 1 || options.SecondMs < 1 || Math.Abs(options.SecondMs - options.FirstMs) <= options.ToleranceMs * 2)
            throw new ArgumentException("The two silences must differ by more than twice the tolerance.", nameof(options));
        // This overwrites a file, so it must be one of the app's own slots and nothing else.
        if (!Path.IsPathFullyQualified(slotPath) || slotPath.StartsWith(@"\\") || !SlotPool.IsSlotFile(slotPath))
            return new(SlotProbeOutcome.Refused, "The probe only writes to slot files (slot-N.wav) on a local drive.");
        string before = await readStatus().ConfigureAwait(false) ?? "";
        if (!Stopped(before)) return new(SlotProbeOutcome.Refused, "The player is not stopped, so the probe did not run.") { StatusBefore = before };

        var first = SliceWav.Silence(SliceRenderer.FrameOf(options.FirstMs, options.Rate), options.Rate, options.Channels);
        var second = SliceWav.Silence(SliceRenderer.FrameOf(options.SecondMs, options.Rate), options.Rate, options.Channels);
        // Checked rather than assumed, because this plays through the owner's real routing.
        if (!SliceWav.IsDigitalSilence(first) || !SliceWav.IsDigitalSilence(second)) throw new InvalidOperationException("The probe would have written something other than silence.");
        var result = new SlotProbeResult(SlotProbeOutcome.Inconclusive, "") { StatusBefore = before, FirstWrittenMs = options.FirstMs, SecondWrittenMs = options.SecondMs };

        var write = await SlotPool.WriteInPlace(slotPath, first, options.WriteAttempts, options.RetryDelayMs, cancel).ConfigureAwait(false);
        result = result with { FirstWriteAttempts = write.Attempts };
        if (!write.Ok) return result with { Outcome = SlotProbeOutcome.WriteBlocked, Detail = "The first write failed: " + write.Error };
        await playPath(slotPath).ConfigureAwait(false);
        var seen = await Observe(readDurationMs, readStatus, options.FirstMs, options, cancel).ConfigureAwait(false);
        result = result with { FirstReportedMs = seen.ReportedMs, FirstPlayedMs = seen.PlayedMs };
        if (!seen.Stopped) return result with { Detail = "The player did not return to STOPPED after the first play." };

        write = await SlotPool.WriteInPlace(slotPath, second, options.WriteAttempts, options.RetryDelayMs, cancel).ConfigureAwait(false);
        result = result with { SecondWriteAttempts = write.Attempts };
        if (!write.Ok) return result with { Outcome = SlotProbeOutcome.WriteBlocked, Detail = "The second write failed, so the player still had the file open after playing it: " + write.Error };
        await playPath(slotPath).ConfigureAwait(false);
        seen = await Observe(readDurationMs, readStatus, options.SecondMs, options, cancel).ConfigureAwait(false);
        result = result with { SecondReportedMs = seen.ReportedMs, SecondPlayedMs = seen.PlayedMs };

        bool Near(long? reported, int written) => reported is long value && Math.Abs(value - written) <= options.ToleranceMs;
        string tail = $" Busy for {Show(result.FirstPlayedMs)}, then {Show(result.SecondPlayedMs)}." + (seen.Stopped ? "" : " The player had not returned to STOPPED when the probe ended.");
        if (!Near(result.FirstReportedMs, options.FirstMs))
            return result with { Detail = $"The first play reported {Show(result.FirstReportedMs)} for {options.FirstMs} ms of silence, so the duration cannot be used as evidence." + tail };
        if (Near(result.SecondReportedMs, options.SecondMs))
            return result with { Outcome = SlotProbeOutcome.Reread, Detail = $"The reported duration followed the file ({Show(result.FirstReportedMs)}, then {Show(result.SecondReportedMs)}): the player re-reads a changed file." + tail };
        if (Near(result.SecondReportedMs, options.FirstMs))
            return result with { Outcome = SlotProbeOutcome.NotReread, Detail = $"The second play still reported {Show(result.SecondReportedMs)} after the file became {options.SecondMs} ms: the player did not re-read it." + tail };
        return result with { Detail = $"The second play reported {Show(result.SecondReportedMs)}, which matches neither {options.FirstMs} ms nor {options.SecondMs} ms." + tail };
    }

    private static bool Stopped(string? status) => string.Equals(status?.Trim(), "STOPPED", StringComparison.OrdinalIgnoreCase);
    private static string Show(long? ms) => ms is long value ? value + " ms" : "no value";

    private readonly record struct Observation(long? ReportedMs, long? PlayedMs, bool Stopped);

    /// <summary>
    /// Watches one play until the player is stopped again. The reported duration can lag (still showing the
    /// previous sound), so it is read on every look until it matches what was written; otherwise the last value
    /// seen is kept. A player that never shows as playing may simply have been quicker than the polling, so it is
    /// given the silence's own length before "stopped" is believed.
    /// </summary>
    private static async Task<Observation> Observe(Func<Task<long?>> readDurationMs, Func<Task<string>> readStatus, int writtenMs, SlotProbeOptions options, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        long? reported = null; bool matched = false, sawPlaying = false;
        while (true)
        {
            bool stopped = Stopped(await readStatus().ConfigureAwait(false));
            long elapsed = clock.ElapsedMilliseconds;
            if (!matched && await readDurationMs().ConfigureAwait(false) is long ms && ms > 0) { reported = ms; matched = Math.Abs(ms - writtenMs) <= options.ToleranceMs; }
            if (stopped && sawPlaying) return new(reported, elapsed, true);
            if (stopped && elapsed >= writtenMs + options.ToleranceMs) return new(reported, null, true);
            if (!stopped) sawPlaying = true;
            if (elapsed >= writtenMs + options.StopTimeoutMs) return new(reported, null, false);
            await Task.Delay(Math.Max(1, options.PollMs), cancel).ConfigureAwait(false);
        }
    }
}
