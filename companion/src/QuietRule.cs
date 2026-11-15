namespace SoundpadWeb;

/// <summary>
/// The quiet rule (2.37, shared since 2.44): a change to Soundpad's library runs only when Soundpad was seen stopped,
/// stayed stopped for <see cref="QuietMs"/> with nothing played by the companion meanwhile, and is still stopped at the
/// moment of the change. Soundpad 4.0.35 exited once when a category was removed right after one of its sounds
/// played; every native library change (Temp category removal and every Organize step) waits for this.
/// </summary>
internal static class QuietRule
{
    public const int QuietMs = 1000;
    public enum Outcome
    {
        /// <summary>Soundpad was playing, paused or unknown: nothing ran.</summary>
        NotStopped,
        /// <summary>The companion played something during the quiet second: nothing ran; try again.</summary>
        Played,
        /// <summary>The action ran, inside the same gate entry as the last check.</summary>
        Ran,
    }
    /// <summary>
    /// One attempt: checks, waits out the quiet second, then checks again and runs <paramref name="action"/> in that
    /// same gate entry (<paramref name="batch"/> holds the native gate for the work it is given), so no play can come
    /// between the last check and the change. <paramref name="activity"/> is when the companion last played or saw
    /// something playing (<see cref="Environment.TickCount64"/>).
    /// </summary>
    public static async Task<(Outcome Outcome, T? Result)> TryOnce<T>(Func<Func<Task>, Task> batch, ISoundpad pipe, Func<long>? activity, Func<Task<T>> action, int quietMs = QuietMs, CancellationToken cancel = default)
    {
        async Task<R> In<R>(Func<Task<R>> work) { R result = default!; await batch(async () => result = await work()); return result; }
        long? stopped = await In(async () => await pipe.Request("GetPlayStatus()") == "STOPPED" ? Environment.TickCount64 : (long?)null);
        if (stopped == null) return (Outcome.NotStopped, default);
        long seen = activity?.Invoke() ?? long.MinValue;
        long wait = Math.Max(stopped.Value, seen) + quietMs - Environment.TickCount64;
        if (wait > 0) await Task.Delay((int)Math.Min(wait, quietMs), cancel);
        return await In(async () =>
        {
            if (await pipe.Request("GetPlayStatus()") != "STOPPED") return (Outcome.NotStopped, default(T));
            if ((activity?.Invoke() ?? long.MinValue) != seen) return (Outcome.Played, default(T));
            return (Outcome.Ran, await action());
        });
    }
}
