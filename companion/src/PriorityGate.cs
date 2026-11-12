namespace SoundpadWeb;

/// <summary>
/// One holder at a time, like a semaphore, but the next holder is chosen by lane and a waiter can be taken out of
/// the queue (2.35). Playback control waits in <see cref="Lane.Urgent"/>, ordinary commands in
/// <see cref="Lane.Normal"/>, and library refreshes, managed-audio batches, recovery and deferred saves in
/// <see cref="Lane.Background"/>. Within a lane the order is arrival order.
/// A lower lane is never starved: a normal waiter older than <see cref="NormalAgeMs"/> (or a background one older
/// than <see cref="BackgroundAgeMs"/>) gets the next turn, but only one such turn between urgent holders.
/// </summary>
internal sealed class PriorityGate(int normalAgeMs = PriorityGate.NormalAgeMs, int backgroundAgeMs = PriorityGate.BackgroundAgeMs)
{
    public enum Lane { Urgent = 0, Normal = 1, Background = 2 }
    public const int NormalAgeMs = 200, BackgroundAgeMs = 1000;
    public sealed class Waiter
    {
        // Continuations run on the thread pool, never inside Release or Cancel: a superseded play must not run its
        // clean-up on the thread that is delivering the newer command.
        internal readonly TaskCompletionSource<bool> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
        internal long Since;
        /// <summary>True when the gate is held by this waiter; false when it was cancelled while still waiting.</summary>
        public Task<bool> Entered => Source.Task;
    }
    private readonly object sync = new();
    private readonly LinkedList<Waiter>[] lanes = [new(), new(), new()];
    private bool held, lastTurnWasAged;

    /// <summary>Takes the gate only if it is free right now.</summary>
    public bool TryEnter()
    {
        lock (sync) { if (held) return false; held = true; return true; }
    }
    /// <summary>
    /// Queues for the gate. When it is free the returned waiter is already complete, so the caller carries on
    /// without leaving its thread (commands keep their arrival order up to their first real wait).
    /// </summary>
    public Waiter Enter(Lane lane)
    {
        var waiter = new Waiter { Since = Environment.TickCount64 };
        lock (sync)
        {
            if (!held) { held = true; waiter.Source.SetResult(true); }
            else waiter.Node = lanes[(int)lane].AddLast(waiter);
        }
        return waiter;
    }
    /// <summary>Takes a waiter out of the queue. False when it already holds the gate (the holder must release it).</summary>
    public bool Cancel(Waiter waiter)
    {
        lock (sync)
        {
            if (waiter.Node == null) return false;
            waiter.Node.List!.Remove(waiter.Node); waiter.Node = null;
        }
        waiter.Source.TrySetResult(false);
        return true;
    }
    public void Release()
    {
        Waiter? next = null;
        lock (sync)
        {
            if (!held) throw new SemaphoreFullException();
            long now = Environment.TickCount64;
            var urgent = lanes[0].First; var normal = lanes[1].First; var background = lanes[2].First;
            bool mayAge = urgent == null || !lastTurnWasAged;
            LinkedListNode<Waiter>? chosen;
            if (mayAge && background != null && now - background.Value.Since >= backgroundAgeMs) { chosen = background; lastTurnWasAged = urgent != null; }
            else if (mayAge && normal != null && now - normal.Value.Since >= normalAgeMs) { chosen = normal; lastTurnWasAged = urgent != null; }
            else { chosen = urgent ?? normal ?? background; lastTurnWasAged = false; }
            if (chosen == null) { held = false; return; }
            next = chosen.Value; chosen.List!.Remove(chosen); next.Node = null;
        }
        next.Source.TrySetResult(true);
    }
    /// <summary>Waits for the gate in a lane; for callers that are never cancelled.</summary>
    public async Task EnterAsync(Lane lane) => await Enter(lane).Entered;
    /// <summary>Waits at most <paramref name="milliseconds"/> for the gate, then gives up (false).</summary>
    public async Task<bool> EnterWithin(Lane lane, int milliseconds)
    {
        var waiter = Enter(lane);
        if (waiter.Entered.IsCompleted) return waiter.Entered.Result;
        if (await Task.WhenAny(waiter.Entered, Task.Delay(milliseconds)) != waiter.Entered) Cancel(waiter);
        return await waiter.Entered;
    }
}
