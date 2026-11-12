using System.Security.Cryptography;
namespace SoundpadWeb;

public sealed record SlotPoolOptions
{
    /// <summary>A slot acquired this long ago counts as free again even if nobody released it (a lost caller must not hold it for ever).</summary>
    public int MaxHoldMs { get; init; } = 60000;
    /// <summary>How many times a write tries to open a file another program still has open.</summary>
    public int WriteAttempts { get; init; } = 4;
    /// <summary>Wait before the second attempt; it doubles each time (10, 20, 40 ms by default).</summary>
    public int RetryDelayMs { get; init; } = 10;
    /// <summary>What <see cref="SlotPool.EnsureFiles"/> puts in a new slot: this much digital silence.</summary>
    public int SilenceMs { get; init; } = 100;
    public int SilenceRate { get; init; } = 48000;
    public int SilenceChannels { get; init; } = 2;
    /// <summary>Milliseconds from a steady clock; tests replace it. Null uses <see cref="Environment.TickCount64"/>.</summary>
    public Func<long>? Clock { get; init; }
}

/// <summary>
/// What a slot file holds, as far as the pool knows. <c>Hash</c> is the SHA-256 of the whole file (null when its
/// contents are unknown, for example after a write failed half way), and <c>Generation</c> rises with every
/// change to any slot, so "is my slice still in there" is one comparison.
/// </summary>
public sealed record SlotInfo(int Index, string Path, long Generation, string? Hash, long DurationMs, long Frames, int SampleRate, int Channels, long Bytes, bool InUse);

/// <summary>The right to write one slot, from <see cref="SlotPool.Acquire"/> until <see cref="SlotPool.Release"/> or until the slot is handed to someone else.</summary>
public sealed record SlotLease(int Index, string Path, long LeaseId);

/// <summary>
/// The outcome of a slot write. <c>Stale</c>: the lease no longer owns the slot and nothing was written.
/// Otherwise a failure means the file could not be opened in time (still in use): release the lease and acquire
/// another slot.
/// </summary>
public sealed record SlotWriteResult(bool Ok, int Attempts, SlotInfo Slot, string? Error, bool Stale = false);

/// <summary>
/// A few reusable WAV files ("slot-1.wav" ...) that are registered in Soundpad once and then rewritten with
/// whatever exact slice should play next, so playing a word does not add a library entry per click. This class
/// only manages the files: which slot to use next, replacing a slot's contents without changing its path, and
/// what each slot currently holds. It never talks to Soundpad.
/// </summary>
public sealed class SlotPool
{
    public const int DefaultCount = 4;

    private sealed class Slot(int index, string path)
    {
        public readonly int Index = index;
        public readonly string Path = path;
        public readonly SemaphoreSlim Writing = new(1, 1);
        public bool Held;
        public long HeldAt, LeaseId, LastUsed, Generation, Frames, Bytes, DurationMs;
        public int Rate, Channels;
        public string? Hash;
    }

    private readonly Slot[] slots;
    private readonly SlotPoolOptions options;
    private readonly Func<long> clock;
    private readonly object sync = new();
    private long generation, leases, uses;
    private int lastHandedOut;

    public SlotPool(string folder, int count = DefaultCount, SlotPoolOptions? options = null)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\")) throw new ArgumentException("The slot folder must be a local folder.", nameof(folder));
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 64);
        this.options = options ?? new(); clock = this.options.Clock ?? (() => Environment.TickCount64);
        Folder = Path.GetFullPath(folder); Count = count;
        slots = Enumerable.Range(1, count).Select(i => new Slot(i, Path.Combine(Folder, FileName(i)))).ToArray();
    }

    public string Folder { get; }
    public int Count { get; }
    /// <summary>The file name of slot <paramref name="index"/> (1-based): "slot-1.wav". ASCII, so Soundpad's ANSI commands can carry it.</summary>
    public static string FileName(int index) => $"slot-{index}.wav";
    /// <summary>The full path Soundpad is given for slot <paramref name="index"/> (1-based).</summary>
    public string PathOf(int index) => Find(index).Path;
    /// <summary>Whether a path is named like a slot file. <see cref="SlotProbe"/> writes to nothing else.</summary>
    public static bool IsSlotFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("slot-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(5, name.Length - 9), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) && index is >= 1 and <= 64;
    }

    /// <summary>Every slot's state, in index order.</summary>
    public IReadOnlyList<SlotInfo> Slots { get { lock (sync) { long now = clock(); return slots.Select(s => View(s, now)).ToArray(); } } }
    public SlotInfo Info(int index) { var slot = Find(index); lock (sync) return View(slot, clock()); }

    /// <summary>
    /// Makes sure every slot file exists and is a playable WAV: missing (or unreadable) ones become a short
    /// silent WAV, existing ones are kept and their contents recorded. Call it before registering the paths in
    /// Soundpad, which needs the files to exist. Throws <see cref="IOException"/> if a slot cannot be prepared.
    /// </summary>
    public async Task<IReadOnlyList<SlotInfo>> EnsureFiles(CancellationToken cancel = default)
    {
        Directory.CreateDirectory(Folder);
        foreach (var slot in slots)
        {
            await slot.Writing.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                ManagedFiles.RequireOwnedPath(slot.Path, Folder);
                var existing = ReadShared(slot.Path);
                if (existing != null && SliceWav.TryRead(existing, out var format) && format.Frames > 0)
                {
                    lock (sync) if (slot.Hash != HashOf(existing)) Record(slot, existing, format);
                    continue;
                }
                var silence = SliceWav.Silence(Math.Max(1, SliceRenderer.FrameOf(options.SilenceMs, options.SilenceRate)), options.SilenceRate, options.SilenceChannels);
                var written = await WriteInPlace(slot.Path, silence, options.WriteAttempts, options.RetryDelayMs, cancel).ConfigureAwait(false);
                lock (sync)
                {
                    if (written.Ok) { SliceWav.TryRead(silence, out format); Record(slot, silence, format); }
                    else if (written.ContentUnknown) Forget(slot);
                }
                if (!written.Ok) throw new IOException($"Slot {slot.Index} could not be prepared: {written.Error}");
            }
            finally { slot.Writing.Release(); }
        }
        return Slots;
    }

    /// <summary>
    /// Hands out the least recently used free slot, or null when there is none. Never the slot handed out last
    /// (the player may still be reading it), never one that is in use (acquired, not released, and younger than
    /// the maximum hold time), and none of <paramref name="exclude"/>. With a single slot the "not the last one"
    /// rule cannot apply and is skipped.
    /// </summary>
    public SlotLease? Acquire(IReadOnlyCollection<int>? exclude = null)
    {
        lock (sync)
        {
            long now = clock();
            Slot? best = null;
            foreach (var slot in slots)
            {
                if (InUse(slot, now) || (slots.Length > 1 && slot.Index == lastHandedOut) || exclude?.Contains(slot.Index) == true) continue;
                if (best == null || slot.LastUsed < best.LastUsed) best = slot;
            }
            if (best == null) return null;
            best.Held = true; best.HeldAt = now; best.LeaseId = ++leases; best.LastUsed = ++uses; lastHandedOut = best.Index;
            return new(best.Index, best.Path, best.LeaseId);
        }
    }

    /// <summary>Marks the slot free again (its audio finished or was replaced). An outdated lease does nothing.</summary>
    public void Release(SlotLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var slot = Find(lease.Index);
        lock (sync) if (slot.LeaseId == lease.LeaseId) slot.Held = false;
    }

    /// <summary>
    /// Replaces the slot file's contents with <paramref name="wav"/> without deleting or renaming it, so the
    /// path registered in the player stays the same file. If another program has the file open in a way that
    /// forbids writing, it retries with a short doubling wait and then gives up with <c>Ok = false</c>, leaving
    /// the file and its record as they were. A lease that no longer owns the slot (released, or expired and
    /// handed to a newer request) writes nothing, so a slow render cannot overwrite a newer one's audio.
    /// </summary>
    public async Task<SlotWriteResult> Write(SlotLease lease, byte[] wav, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(lease); ArgumentNullException.ThrowIfNull(wav);
        if (!SliceWav.TryRead(wav, out var format) || format.Frames == 0) throw new ArgumentException("A slot can only hold a WAV file with audio in it.", nameof(wav));
        var slot = Find(lease.Index);
        await slot.Writing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            lock (sync) if (!slot.Held || slot.LeaseId != lease.LeaseId) return new(false, 0, View(slot, clock()), "The slot was handed to a newer request.", true);
            ManagedFiles.RequireOwnedPath(slot.Path, Folder);
            var written = await WriteInPlace(slot.Path, wav, options.WriteAttempts, options.RetryDelayMs, cancel).ConfigureAwait(false);
            lock (sync)
            {
                if (written.Ok) Record(slot, wav, format);
                else if (written.ContentUnknown) Forget(slot);
                return new(written.Ok, written.Attempts, View(slot, clock()), written.Error);
            }
        }
        finally { slot.Writing.Release(); }
    }

    /// <summary>Whether the slot file on disk still has the contents the pool recorded (false when unknown or unreadable).</summary>
    public bool Verify(int index)
    {
        var slot = Find(index); string? expected;
        lock (sync) expected = slot.Hash;
        var bytes = ReadShared(slot.Path);
        return expected != null && bytes != null && HashOf(bytes) == expected;
    }

    internal readonly record struct FileWrite(bool Ok, int Attempts, bool ContentUnknown, string? Error);

    /// <summary>
    /// Overwrites a file in place: same path, same file, new bytes. Other programs may keep reading it meanwhile
    /// only if they opened it allowing writers; one that did not makes the open fail, which is retried
    /// <paramref name="attempts"/> times in all. Nothing is changed until the file could be opened (and, when the
    /// new contents are shorter, shortened), so a refusal leaves the old contents whole.
    /// </summary>
    internal static async Task<FileWrite> WriteInPlace(string path, ReadOnlyMemory<byte> bytes, int attempts, int retryDelayMs, CancellationToken cancel)
    {
        int delay = Math.Max(1, retryDelayMs);
        for (int attempt = 1; ; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            bool changed = false;
            try
            {
                // FileShare.Read: nobody else may write meanwhile, and a player that opens the file without
                // allowing a writer waits (its open fails) instead of reading a half-written file.
                using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.None);
                if (file.Length > bytes.Length) file.SetLength(bytes.Length); // refused while a program has the file mapped
                changed = true;
                file.Write(bytes.Span); file.Flush();
                return new(true, attempt, false, null);
            }
            catch (IOException ex) when (!changed && InUseElsewhere(ex))
            {
                if (attempt >= Math.Max(1, attempts)) return new(false, attempt, false, "The slot file is in use by another program.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new(false, attempt, changed, "The slot file could not be written.");
            }
            await Task.Delay(delay, cancel).ConfigureAwait(false);
            delay *= 2;
        }
    }

    // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION, ERROR_USER_MAPPED_FILE: someone else has it; trying again can work.
    private static bool InUseElsewhere(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33 or 1224;

    /// <summary>Reads a file without getting in a player's way. Null when it is missing, unreadable or implausibly large.</summary>
    private static byte[]? ReadShared(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > ManagedAudioCodec.MaxVariantBytes) return null;
            var bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private Slot Find(int index) => index >= 1 && index <= slots.Length ? slots[index - 1] : throw new ArgumentOutOfRangeException(nameof(index));

    private bool InUse(Slot slot, long now) => slot.Held && now - slot.HeldAt < options.MaxHoldMs;

    private SlotInfo View(Slot slot, long now) => new(slot.Index, slot.Path, slot.Generation, slot.Hash, slot.DurationMs, slot.Frames, slot.Rate, slot.Channels, slot.Bytes, InUse(slot, now));

    /// <summary>Called with the lock held, after the file was written or found on disk.</summary>
    private void Record(Slot slot, byte[] wav, SliceWavInfo format)
    {
        slot.Hash = HashOf(wav); slot.Bytes = wav.Length; slot.Frames = format.Frames; slot.Rate = format.SampleRate; slot.Channels = format.Channels;
        slot.DurationMs = (long)Math.Round(format.DurationMs, MidpointRounding.AwayFromZero); slot.Generation = ++generation;
    }

    /// <summary>Called with the lock held: the file may hold part of a write, so nothing recorded about it is true any more.</summary>
    private void Forget(Slot slot)
    {
        slot.Hash = null; slot.Bytes = slot.Frames = slot.DurationMs = 0; slot.Rate = slot.Channels = 0; slot.Generation = ++generation;
    }
}
