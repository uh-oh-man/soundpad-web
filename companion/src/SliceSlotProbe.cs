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
    /// <summary>How long to wait after a play for the player to report the expected duration.</summary>
    public int SettleMs { get; init; } = 1500;
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
    public long? FirstReportedMs { get; init; }
    public long? SecondReportedMs { get; init; }
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
    /// <param name="readDurationMs">The duration the player reports for what it is playing, or null when it reports none.</param>
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
        result = result with { FirstReportedMs = await Reported(readDurationMs, options.FirstMs, options, cancel).ConfigureAwait(false) };
        if (!await WaitStopped(readStatus, options.FirstMs, options, cancel).ConfigureAwait(false))
            return result with { Detail = "The player did not return to STOPPED after the first play." };

        write = await SlotPool.WriteInPlace(slotPath, second, options.WriteAttempts, options.RetryDelayMs, cancel).ConfigureAwait(false);
        result = result with { SecondWriteAttempts = write.Attempts };
        if (!write.Ok) return result with { Outcome = SlotProbeOutcome.WriteBlocked, Detail = "The second write failed, so the player still had the file open after playing it: " + write.Error };
        await playPath(slotPath).ConfigureAwait(false);
        result = result with { SecondReportedMs = await Reported(readDurationMs, options.SecondMs, options, cancel).ConfigureAwait(false) };
        bool stopped = await WaitStopped(readStatus, options.SecondMs, options, cancel).ConfigureAwait(false);

        bool Near(long? reported, int written) => reported is long value && Math.Abs(value - written) <= options.ToleranceMs;
        if (!Near(result.FirstReportedMs, options.FirstMs))
            return result with { Detail = $"The first play reported {Show(result.FirstReportedMs)} for {options.FirstMs} ms of silence, so the duration cannot be used as evidence." };
        string tail = stopped ? "" : " The player had not returned to STOPPED when the probe ended.";
        if (Near(result.SecondReportedMs, options.SecondMs))
            return result with { Outcome = SlotProbeOutcome.Reread, Detail = $"The reported duration followed the file ({Show(result.FirstReportedMs)}, then {Show(result.SecondReportedMs)}): the player re-reads a changed file." + tail };
        if (Near(result.SecondReportedMs, options.FirstMs))
            return result with { Outcome = SlotProbeOutcome.NotReread, Detail = $"The second play still reported {Show(result.SecondReportedMs)} after the file became {options.SecondMs} ms: the player did not re-read it." + tail };
        return result with { Detail = $"The second play reported {Show(result.SecondReportedMs)}, which matches neither {options.FirstMs} ms nor {options.SecondMs} ms." + tail };
    }

    private static bool Stopped(string? status) => string.Equals(status?.Trim(), "STOPPED", StringComparison.OrdinalIgnoreCase);
    private static string Show(long? ms) => ms is long value ? value + " ms" : "no duration";

    /// <summary>
    /// The duration the player reports after a play. It can lag (still showing the previous sound), so this looks
    /// until the expected value appears or the settle time runs out, and then returns the last value seen.
    /// </summary>
    private static async Task<long?> Reported(Func<Task<long?>> readDurationMs, int expected, SlotProbeOptions options, CancellationToken cancel)
    {
        long? last = null; long deadline = Environment.TickCount64 + options.SettleMs;
        while (true)
        {
            var value = await readDurationMs().ConfigureAwait(false);
            if (value is long ms) { last = ms; if (Math.Abs(ms - expected) <= options.ToleranceMs) return ms; }
            if (Environment.TickCount64 >= deadline) return last;
            await Task.Delay(Math.Max(1, options.PollMs), cancel).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitStopped(Func<Task<string>> readStatus, int playingMs, SlotProbeOptions options, CancellationToken cancel)
    {
        long deadline = Environment.TickCount64 + playingMs + options.StopTimeoutMs;
        while (true)
        {
            if (Stopped(await readStatus().ConfigureAwait(false))) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(Math.Max(1, options.PollMs), cancel).ConfigureAwait(false);
        }
    }
}
