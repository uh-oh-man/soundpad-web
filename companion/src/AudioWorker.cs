using System.Collections.Concurrent;
namespace SoundpadWeb;

/// <summary>
/// Heavy audio work (2.41): decoding, effects (keep-pitch speed above all), WAV encoding and history reads for
/// copies, previews and clips. It runs here, on at most two below-normal threads, instead of on the thread that
/// handed the command to <see cref="Bridge.Execute"/>: that thread is the loopback socket's receive loop, and every
/// Play, Stop and Panic behind a 40 s keep-pitch render used to wait for it (review, 2.41).
/// <para>
/// Only the CPU-bound part goes here. Its caller awaits the answer, which continues on the thread pool at normal
/// priority (the completion runs continuations asynchronously), so a job that later takes the native gate or the
/// pipe never does so on a low-priority thread. The queue is bounded; a full queue is answered as busy.
/// </para>
/// </summary>
public sealed class AudioWorker(int threads = 2, int capacity = 32) : IDisposable
{
    private readonly BlockingCollection<Action> queue = new(capacity);
    private readonly object startGate = new();
    private bool started;
    private int running;

    /// <summary>Jobs being worked on right now (tests and the bench).</summary>
    public int Running => Volatile.Read(ref running);

    /// <summary>Runs <paramref name="work"/> on a worker thread. Throws <see cref="InvalidDataException"/> (an answer, not a crash) when the queue is full.</summary>
    public Task<T> Run<T>(Func<T> work, CancellationToken cancel = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Start();
        bool queued;
        try
        {
            queued = queue.TryAdd(() =>
            {
                if (cancel.IsCancellationRequested) { done.TrySetCanceled(cancel); return; }
                Interlocked.Increment(ref running);
                try { done.TrySetResult(work()); }
                catch (OperationCanceledException ex) { done.TrySetCanceled(ex.CancellationToken); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { Interlocked.Decrement(ref running); }
            });
        }
        catch (InvalidOperationException) { return Task.FromException<T>(new ObjectDisposedException(nameof(AudioWorker))); }
        return queued ? done.Task : Task.FromException<T>(new InvalidDataException("The companion is busy with other audio. Try again in a moment."));
    }

    private void Start()
    {
        if (Volatile.Read(ref started)) return;
        lock (startGate)
        {
            if (started) return;
            for (int i = 0; i < Math.Max(1, threads); i++)
                new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Soundpad Web audio work " + (i + 1) }.Start();
            Volatile.Write(ref started, true);
        }
    }

    private void Work()
    {
        try { foreach (var job in queue.GetConsumingEnumerable()) job(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Takes no new work; jobs already queued still finish (their callers are waiting for them).</summary>
    public void Dispose() { try { queue.CompleteAdding(); } catch (ObjectDisposedException) { } }
}
