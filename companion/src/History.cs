namespace SoundpadWeb;

// Rolling recording history: the value types shared by HistoryStore and whoever queries it.
// One absolute frame timeline per companion session. Frame 0 is the first captured frame and the
// index never resets, across Stop/Start and device changes. Nothing here opens an audio device.

/// <summary>Frames [StartFrame, EndFrame) on the session's absolute frame timeline.</summary>
public readonly record struct HistoryRange(long StartFrame, long EndFrame)
{
    public long Frames => EndFrame - StartFrame;
}

/// <summary>One contiguous run of captured audio. Frame f in it was heard at WallStartUtcMs + (f - StartFrame) * 1000 / rate.</summary>
public sealed record HistorySpan(long StartFrame, long EndFrame, long WallStartUtcMs);

/// <summary>
/// A break between two runs. It occupies zero frames (the audio on either side is adjacent on the frame
/// axis) but is always reported so a view can draw it. WallToUtcMs is null while recording has not resumed.
/// </summary>
public sealed record HistoryGap(long AtFrame, long WallFromUtcMs, long? WallToUtcMs, string Reason);

/// <summary>The only values a <see cref="HistoryGap.Reason"/> can have.</summary>
public static class HistoryGapReason
{
    public const string Stopped = "stopped", DeviceLost = "device-lost", Update = "update", Overrun = "overrun", Crash = "crash";
    public static bool IsKnown(string? reason) => reason is Stopped or DeviceLost or Update or Overrun or Crash;
}

/// <summary>What one viewer may know about the timeline. Built by <see cref="HistoryStore.Timeline"/>, already clipped to their access.</summary>
/// <param name="LiveFrame">The next frame to be captured (frames are absolute, so this is the session total).</param>
/// <param name="LiveWallUtcMs">Wall time of the newest captured frame's end; null before anything was captured.</param>
/// <param name="Recording">True while a run is open (audio was appended and no gap has been marked since).</param>
/// <param name="Available">Ranges this viewer can read right now: the rolling window plus pinned islands, clipped to their access.</param>
public sealed record HistoryTimeline(int Rate, long LiveFrame, long? LiveWallUtcMs, bool Recording, int WindowSeconds, HistoryRange[] Available, HistorySpan[] Spans, HistoryGap[] Gaps);

/// <summary>Host-only storage figures.</summary>
/// <param name="OldestFrame">First frame of the rolling window still held (pinned islands can be older).</param>
/// <param name="WindowBytes">Bytes of audio from OldestFrame to LiveFrame.</param>
/// <param name="PinnedBytes">Bytes of every segment kept by a pin; this is what counts against PinBudgetBytes.</param>
/// <param name="PinnedOutsideWindowBytes">The part of PinnedBytes that is older than the window, i.e. the extra disk use pins cause right now.</param>
/// <param name="UnflushedFrames">Frames held only in memory, waiting for the background writer.</param>
/// <param name="Overruns">How many times audio was dropped because the writer fell behind.</param>
/// <param name="WriteError">The last disk error of the background writer, or null once a write succeeded again.</param>
public sealed record HistoryTotals(long LiveFrame, long OldestFrame, long WindowBytes, long PinnedBytes, long PinnedOutsideWindowBytes, long PinBudgetBytes, int WindowSeconds, long UnflushedFrames, long Overruns, long DroppedFrames, string? WriteError);

/// <summary>Settings for a <see cref="HistoryStore"/>. Rate and SegmentSeconds are fixed for the life of a session.</summary>
public sealed record HistoryOptions
{
    /// <summary>Sample rate of the mono audio given to Append. 48000 in production, 16000 in fixtures.</summary>
    public int Rate { get; init; } = 48000;
    /// <summary>Rolling window length, 60 to 3600 s. Changeable later with <see cref="HistoryStore.SetWindow"/>.</summary>
    public int WindowSeconds { get; init; } = HistoryStore.DefaultWindowSeconds;
    /// <summary>Length of one segment file. 10 s; only tests have a reason to change it.</summary>
    public int SegmentSeconds { get; init; } = 10;
    /// <summary>How much audio may wait in memory for the writer before Append starts dropping. At least two segments.</summary>
    public int TailSeconds { get; init; } = 30;
    /// <summary>Upper bound for the bytes of all pinned segments. Pin fails beyond it.</summary>
    public long PinBudgetBytes { get; init; } = 128L * 1024 * 1024;
    /// <summary>Longest single Read, so one request cannot allocate without bound.</summary>
    public int MaxReadSeconds { get; init; } = 600;
    /// <summary>Clock for wall-time anchors (UTC milliseconds). Tests inject one.</summary>
    public Func<long>? UtcNowMs { get; init; }
    /// <summary>File system to use. Tests inject a slow or failing one.</summary>
    public IHistoryFiles? Files { get; init; }
}

/// <summary>
/// Who is asking. Every read, peak and metadata call on <see cref="HistoryStore"/> takes one, so scoping
/// cannot be forgotten at a call site. A guest sees history from their join frame onwards plus the earlier
/// ranges the host explicitly shared; everything else is refused or clipped inside the store.
/// </summary>
public sealed class HistoryAccess
{
    private static readonly HistoryRange[] Everything = [new(0, long.MaxValue)];
    public const int MaxSharedRanges = 64;

    /// <summary>Everything still available.</summary>
    public static HistoryAccess Host { get; } = new(true, 0, null, [], Everything);

    /// <summary>
    /// A guest who joined at <paramref name="fromFrame"/> (take <see cref="HistoryStore.LiveFrame"/> at the moment they
    /// are admitted and keep it across reconnects). <paramref name="fromWallUtcMs"/> is the wall time of that moment:
    /// with it, a gap that was already open when they joined is reported from their join time; without it that gap is hidden.
    /// </summary>
    public static HistoryAccess Guest(long fromFrame, IEnumerable<HistoryRange>? sharedRanges = null, long? fromWallUtcMs = null)
    {
        if (fromFrame < 0) throw new ArgumentOutOfRangeException(nameof(fromFrame));
        var shared = (sharedRanges ?? []).ToArray();
        if (shared.Length > MaxSharedRanges) throw new ArgumentException("Too many shared ranges.", nameof(sharedRanges));
        if (shared.Any(r => r.StartFrame < 0 || r.EndFrame <= r.StartFrame)) throw new ArgumentException("A shared range is empty or negative.", nameof(sharedRanges));
        // Merge touching ranges so "wholly inside one allowed range" is the complete test for a read.
        var merged = new List<HistoryRange>();
        foreach (var r in shared.Append(new HistoryRange(fromFrame, long.MaxValue)).OrderBy(r => r.StartFrame))
        {
            if (merged.Count > 0 && r.StartFrame <= merged[^1].EndFrame) merged[^1] = merged[^1] with { EndFrame = Math.Max(merged[^1].EndFrame, r.EndFrame) };
            else merged.Add(r);
        }
        return new(false, fromFrame, fromWallUtcMs, shared, [.. merged]);
    }

    private HistoryAccess(bool host, long fromFrame, long? fromWall, HistoryRange[] shared, HistoryRange[] allowed)
    {
        IsHost = host; FromFrame = fromFrame; FromWallUtcMs = fromWall; SharedRanges = shared; Allowed = allowed;
    }

    public bool IsHost { get; }
    public long FromFrame { get; }
    public long? FromWallUtcMs { get; }
    public IReadOnlyList<HistoryRange> SharedRanges { get; }
    /// <summary>Sorted, non-touching ranges this access may see. Host: one range covering everything.</summary>
    internal HistoryRange[] Allowed { get; }
    /// <summary>True when [start, end) lies wholly inside what this access may see.</summary>
    public bool Allows(long startFrame, long endFrame)
    {
        foreach (var r in Allowed) if (startFrame >= r.StartFrame && endFrame <= r.EndFrame) return startFrame < endFrame;
        return false;
    }
}

/// <summary>
/// Base of the typed refusals below. Their messages are written for the person at the website, like the
/// InvalidDataException messages elsewhere in the companion, but InvalidDataException is sealed, so a caller
/// that turns those into failed acks must catch this type as well.
/// </summary>
public abstract class HistoryException(string message) : Exception(message);

/// <summary>
/// Thrown instead of returning shifted, zero-filled or partial audio. <see cref="Available"/> is what the
/// same access can read right now, so a view can re-clamp its selection.
/// </summary>
public sealed class HistoryUnavailableException(string reason, string message, HistoryRange[] available) : HistoryException(message)
{
    public const string Expired = "expired", NotShared = "not-shared", NotRecorded = "not-recorded", Closed = "closed";
    /// <summary>"expired" (scrolled out of the window), "not-shared" (outside this guest's access), "not-recorded" (newer than live) or "closed".</summary>
    public string Reason { get; } = reason;
    public IReadOnlyList<HistoryRange> Available { get; } = available;
    /// <summary>The newest contiguous available range (the rolling window as this access sees it), or null when nothing is available.</summary>
    public HistoryRange? Window => Available.Count > 0 ? Available[^1] : null;
}

/// <summary>Thrown by Pin when the pinned segments would exceed <see cref="HistoryOptions.PinBudgetBytes"/>. Nothing was changed.</summary>
public sealed class HistoryPinBudgetException(long requiredBytes, long budgetBytes)
    : HistoryException($"Protected recording history is full: keeping that would need {requiredBytes / 1048576.0:0.#} MB of the {budgetBytes / 1048576.0:0.#} MB allowed. Keep or discard a draft first.")
{
    public long RequiredBytes { get; } = requiredBytes;
    public long BudgetBytes { get; } = budgetBytes;
}

/// <summary>A session folder found on disk by <see cref="HistoryStore.Recover"/>.</summary>
/// <param name="Valid">False when the index is missing or damaged; such a session can only be discarded.</param>
/// <param name="Frames">Frames on the timeline when the index was last written.</param>
/// <param name="RetainedFrames">Frames whose audio is still on disk (window plus pins).</param>
/// <param name="CleanlyClosed">True when Close(false) finished (an update hand-over); false after a crash.</param>
/// <param name="InUse">True when a store in this process has the session open; it cannot be opened or discarded.</param>
public sealed record HistoryRecoverable(string SessionId, bool Valid, string? Problem, int Rate, long Frames, long RetainedFrames, long Bytes, long CreatedUtcMs, long UpdatedUtcMs, bool CleanlyClosed, int Pins, bool InUse);

/// <summary>What <see cref="HistoryStore.Open"/> found and repaired.</summary>
/// <param name="LostFrames">Frames the index promised that were not intact on disk (a torn tail).</param>
/// <param name="DroppedSegments">Segment files that were missing or too short.</param>
/// <param name="DroppedPins">Owner keys whose pinned audio could not be restored in full.</param>
/// <param name="GapReason">Reason of the gap now open at the live frame (the caller's, or an older one that was already open).</param>
public sealed record HistoryRecovery(long RecoveredFrames, long LostFrames, int DroppedSegments, bool TornTail, string[] DroppedPins, string? GapReason);
