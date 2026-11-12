namespace SoundpadWeb;

/// <summary>
/// Decoded audio files kept in memory so playing a word does not decode its whole recording again.
///
/// Entries are keyed by full path, length and last-write time, so a file that changes is decoded afresh and its
/// old audio is dropped. Requests for the same file share one decode, which runs on the thread pool. The cache
/// holds at most <see cref="ByteBudget"/> bytes of samples and forgets the least recently used files first.
/// Audio that is already in memory (rolling history) needs no cache: wrap it in a <see cref="PcmSliceSource"/>
/// or implement <see cref="ISliceSource"/>.
/// </summary>
public sealed class PcmCache : IDisposable
{
    /// <summary>About 11 minutes of 48 kHz stereo (samples are 32-bit floats).</summary>
    public const long DefaultByteBudget = 256L * 1024 * 1024;

    private sealed class Entry(string key, string path)
    {
        public readonly string Key = key, Path = path;
        public readonly CancellationTokenSource Cancel = new();
        public Task<PcmSliceSource> Task = null!;
        public int Waiters;
        public long Bytes, Stamp;
        public bool Cached;
    }

    private readonly Dictionary<string, Entry> entries = [];
    private readonly object sync = new();
    private readonly Func<string, long, CancellationToken, PcmSliceSource> decode;
    private long bytes, clock, decodes;
    private bool disposed;

    public PcmCache(long byteBudget = DefaultByteBudget) : this(byteBudget, null) { }

    /// <summary>For tests: a stand-in decoder (path, most bytes it may return, cancellation).</summary>
    internal PcmCache(long byteBudget, Func<string, long, CancellationToken, PcmSliceSource>? decoder)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteBudget, 1);
        ByteBudget = byteBudget; decode = decoder ?? Decode;
    }

    /// <summary>Most sample bytes kept. One file larger than this cannot be prepared at all.</summary>
    public long ByteBudget { get; }
    /// <summary>Sample bytes held now.</summary>
    public long Bytes { get { lock (sync) return bytes; } }
    /// <summary>Files held or being decoded.</summary>
    public int Count { get { lock (sync) return entries.Count; } }
    /// <summary>Decodes started since the cache was made (a second request for a cached file adds none).</summary>
    public long DecodeCount => Interlocked.Read(ref decodes);

    /// <summary>
    /// The decoded audio of a local file, in any format <see cref="DecodedAudio"/> reads. Completes at once when
    /// the file is cached. Cancelling abandons only this caller's wait; the decode itself stops when no caller is
    /// left waiting for it. Throws <see cref="InvalidDataException"/> for a file that is missing, not local, not
    /// audio, larger than the budget once decoded, or changed while it was being read.
    /// </summary>
    public async Task<ISliceSource> Get(string path, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        ManagedFiles.LocalSource(path);
        var full = Path.GetFullPath(path); var file = new FileInfo(full);
        long length = file.Length, written = file.LastWriteTimeUtc.Ticks;
        var key = KeyOf(full, length, written);
        Entry? entry;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // The file changed: its earlier audio can never be asked for again, so it would only use up budget.
            foreach (var stale in entries.Values.Where(e => e.Key != key && e.Path.Equals(full, StringComparison.OrdinalIgnoreCase)).ToArray()) Drop(stale);
            if (!entries.TryGetValue(key, out entry))
            {
                var started = entry = new Entry(key, full); entries[key] = entry;
                entry.Task = Task.Run(() => Load(started, length, written));
                // A decode that fails after everyone stopped waiting has no one to report to.
                _ = entry.Task.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            entry.Waiters++; entry.Stamp = ++clock;
        }
        try { return await entry.Task.WaitAsync(cancel).ConfigureAwait(false); }
        finally
        {
            lock (sync)
            {
                if (--entry.Waiters == 0 && !entry.Task.IsCompleted)
                {
                    // Nobody is waiting any more, so stop decoding; the next request starts again.
                    entry.Cancel.Cancel();
                    if (entries.TryGetValue(key, out var current) && current == entry) entries.Remove(key);
                }
            }
        }
    }

    /// <summary>Whether the file's current contents are decoded and held.</summary>
    public bool Contains(string path)
    {
        try
        {
            var full = Path.GetFullPath(path); var file = new FileInfo(full);
            if (!file.Exists) return false;
            var key = KeyOf(full, file.Length, file.LastWriteTimeUtc.Ticks);
            lock (sync) return entries.TryGetValue(key, out var entry) && entry.Cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>Forgets a file (for example when its Temp sound is removed).</summary>
    public void Invalidate(string path)
    {
        var full = Path.GetFullPath(path);
        lock (sync) foreach (var entry in entries.Values.Where(e => e.Path.Equals(full, StringComparison.OrdinalIgnoreCase)).ToArray()) Drop(entry);
    }

    /// <summary>Forgets everything. Sources already handed out stay usable.</summary>
    public void Clear() { lock (sync) foreach (var entry in entries.Values.ToArray()) Drop(entry); }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            foreach (var entry in entries.Values.ToArray()) { Drop(entry); entry.Cancel.Cancel(); }
        }
    }

    /// <summary>
    /// Decodes a whole file to interleaved floats at its own rate and channel count, reading through
    /// <see cref="DecodedAudio"/> (WAV, Ogg Vorbis, and whatever Media Foundation plays). Stops with
    /// <see cref="InvalidDataException"/> once the audio passes <paramref name="maxBytes"/> of samples.
    /// </summary>
    public static PcmSliceSource Decode(string path, long maxBytes, CancellationToken cancel = default)
    {
        using var audio = new DecodedAudio(path);
        int channels = audio.Channels, rate = audio.Rate, chunk = channels * 8192;
        long limit = Math.Min(maxBytes / sizeof(float), Array.MaxLength);
        // The stated duration sizes the buffer once for most files; it may be missing or slightly off, so it still grows.
        long expected = (long)Math.Ceiling(Math.Max(0, audio.Duration.TotalSeconds) * rate) * channels + chunk;
        var samples = new float[Math.Clamp(expected, chunk, Math.Max(chunk, limit))];
        var buffer = new float[chunk];
        int total = 0, count;
        while ((count = audio.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            if ((long)total + count > limit) throw new InvalidDataException("This sound is too long to prepare for exact playback.");
            if (total + count > samples.Length) Array.Resize(ref samples, (int)Math.Min(Math.Max(samples.Length * 3L / 2, (long)total + count), Math.Max(chunk, limit)));
            buffer.AsSpan(0, count).CopyTo(samples.AsSpan(total)); total += count;
        }
        total -= total % channels;
        if (total == 0) throw new InvalidDataException("Empty audio.");
        if (total != samples.Length) Array.Resize(ref samples, total);
        for (int i = 0; i < samples.Length; i++) if (!float.IsFinite(samples[i])) samples[i] = 0;
        return new(samples, rate, channels);
    }

    private static string KeyOf(string fullPath, long length, long writtenTicks) => $"{fullPath.ToUpperInvariant()}|{length}|{writtenTicks}";

    private PcmSliceSource Load(Entry entry, long length, long written)
    {
        try
        {
            Interlocked.Increment(ref decodes);
            PcmSliceSource pcm;
            try { pcm = decode(entry.Path, ByteBudget, entry.Cancel.Token); }
            // Decoders fail in their own ways (COM errors from Media Foundation, format exceptions); callers get one kind.
            catch (Exception ex) when (ex is not (InvalidDataException or OperationCanceledException or OutOfMemoryException)) { throw new InvalidDataException("This file could not be decoded.", ex); }
            var after = new FileInfo(entry.Path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc.Ticks != written) throw new InvalidDataException("Audio changed while it was being read. Try again.");
            lock (sync)
            {
                // Still the entry for this file (not invalidated, replaced or abandoned meanwhile): keep it.
                if (!disposed && entries.TryGetValue(entry.Key, out var current) && current == entry)
                {
                    if (pcm.ByteSize > ByteBudget) entries.Remove(entry.Key);
                    else { entry.Bytes = pcm.ByteSize; entry.Cached = true; bytes += entry.Bytes; Evict(entry); }
                }
            }
            return pcm;
        }
        catch
        {
            // A failure is not remembered, so the next request tries again.
            lock (sync) if (entries.TryGetValue(entry.Key, out var current) && current == entry) entries.Remove(entry.Key);
            throw;
        }
    }

    /// <summary>Called with the lock held. Drops the least recently used files until the budget holds.</summary>
    private void Evict(Entry keep)
    {
        while (bytes > ByteBudget)
        {
            var victim = entries.Values.Where(e => e.Cached && e != keep).MinBy(e => e.Stamp);
            if (victim == null) break;
            Drop(victim);
        }
    }

    /// <summary>Called with the lock held.</summary>
    private void Drop(Entry entry)
    {
        if (entries.TryGetValue(entry.Key, out var current) && current == entry) entries.Remove(entry.Key);
        if (entry.Cached) { bytes -= entry.Bytes; entry.Cached = false; }
        // Callers still waiting get their audio; it just is not kept afterwards.
        if (!entry.Task.IsCompleted && entry.Waiters == 0) entry.Cancel.Cancel();
    }
}
