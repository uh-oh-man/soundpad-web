using System.Diagnostics;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Shared by the History test classes: a deterministic signal, a settable clock and file systems that misbehave on request.</summary>
internal static class HistoryFixture
{
    public const long T0 = 1_700_000_000_000;

    /// <summary>A full-scale 16-bit value that depends only on the frame, so any read can be checked against where it claims to come from.</summary>
    public static short Pcm(long frame) => (short)((frame * 7919 + 13) % 65535 - 32767);

    public static float[] Signal(long start, int count, Func<long, short>? pcm = null)
    {
        pcm ??= Pcm;
        var samples = new float[count];
        for (int i = 0; i < count; i++) samples[i] = pcm(start + i) / 32767f;
        return samples;
    }

    public static short[] Expected(long start, int count, Func<long, short>? pcm = null)
    {
        pcm ??= Pcm;
        var samples = new short[count];
        for (int i = 0; i < count; i++) samples[i] = pcm(start + i);
        return samples;
    }

    public sealed class Clock
    {
        private long now = T0;
        public long Now { get => Interlocked.Read(ref now); set => Interlocked.Exchange(ref now, value); }
    }

    /// <summary>
    /// Appends the fixture signal in one-second blocks as one run, moving the clock with the audio. It waits for
    /// the writer every few seconds: a test produces audio thousands of times faster than real time, and without
    /// the wait that would (correctly) be an overrun.
    /// </summary>
    public static void Feed(HistoryStore store, Clock clock, double seconds, Func<long, short>? pcm = null, int flushEverySeconds = 10)
    {
        long total = (long)Math.Round(seconds * store.Rate), sinceFlush = 0;
        while (total > 0)
        {
            int count = (int)Math.Min(store.Rate, total);
            Assert.Equal(count, store.Append(Signal(store.LiveFrame, count, pcm)));
            clock.Now += count * 1000L / store.Rate; total -= count; sinceFlush += count;
            if (sinceFlush >= (long)flushEverySeconds * store.Rate) { Assert.True(store.Flush(30000)); sinceFlush = 0; }
        }
    }

    public static HistoryUnavailableException Refused(Action action, string reason)
    {
        var ex = Assert.Throws<HistoryUnavailableException>(action);
        Assert.Equal(reason, ex.Reason);
        return ex;
    }

    public static (short Min, short Max) Brute(Func<long, short> pcm, long from, long to)
    {
        short min = short.MaxValue, max = short.MinValue;
        for (long f = from; f < to; f++) { var s = pcm(f); if (s < min) min = s; if (s > max) max = s; }
        return (min, max);
    }

    /// <summary>[from, to) widened to the 64-frame peak blocks it touches. Blocks are counted from each segment's start.</summary>
    public static (long From, long To) Outward(long from, long to, long segmentFrames)
    {
        long lowSegment = from / segmentFrames * segmentFrames, highSegment = (to - 1) / segmentFrames * segmentFrames;
        return (lowSegment + (from - lowSegment) / 64 * 64, highSegment + Math.Min(segmentFrames, (to - highSegment + 63) / 64 * 64));
    }

    public static string[] SegmentFiles(string folder) => [.. Directory.GetFiles(folder, "seg-*.pcm").Select(p => Path.GetFileName(p)!).Order()];
}

/// <summary>The real disk, with switches: block or slow down writes, fail them, count calls, run a hook around reads.</summary>
internal sealed class TestFiles : IHistoryFiles
{
    private readonly IHistoryFiles inner = HistoryDiskFiles.Instance;
    /// <summary>Reset to make every segment write wait, as a stalled disk would.</summary>
    public readonly ManualResetEventSlim WriteGate = new(true);
    /// <summary>Set once a write is waiting at the gate.</summary>
    public readonly ManualResetEventSlim WriteWaiting = new(false);
    public volatile int WriteDelayMs;
    public volatile bool FailWrites;
    public int Reads, Writes;
    public Action<string>? BeforeRead, AfterRead;

    public void CreateDirectory(string path) => inner.CreateDirectory(path);
    public IReadOnlyList<HistoryFileEntry> Entries(string directory) => inner.Entries(directory);
    public long Length(string path) => inner.Length(path);
    public void Write(string path, long offset, ReadOnlySpan<byte> data)
    {
        Interlocked.Increment(ref Writes);
        if (!WriteGate.IsSet) { WriteWaiting.Set(); WriteGate.Wait(); }
        if (WriteDelayMs > 0) Thread.Sleep(WriteDelayMs);
        if (FailWrites) throw new IOException("There is not enough space on the disk.");
        inner.Write(path, offset, data);
    }
    public int Read(string path, long offset, Span<byte> destination)
    {
        Interlocked.Increment(ref Reads);
        BeforeRead?.Invoke(Path.GetFileName(path));
        int read = inner.Read(path, offset, destination);
        AfterRead?.Invoke(Path.GetFileName(path));
        return read;
    }
    public void Truncate(string path, long length) => inner.Truncate(path, length);
    public void ReplaceAtomically(string path, ReadOnlySpan<byte> data) => inner.ReplaceAtomically(path, data);
    public void Delete(string path) => inner.Delete(path);
    public bool RemoveDirectoryIfEmpty(string path) => inner.RemoveDirectoryIfEmpty(path);
}

/// <summary>No disk at all, for the hour-long test: it would otherwise write and sync 58 MB.</summary>
internal sealed class MemoryFiles : IHistoryFiles
{
    private readonly Dictionary<string, byte[]> data = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
    public int Reads;

    public void CreateDirectory(string path) { lock (data) folders.Add(path); }
    public IReadOnlyList<HistoryFileEntry> Entries(string directory)
    {
        lock (data)
            return [.. data.Keys.Where(p => string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase)).Select(p => new HistoryFileEntry(Path.GetFileName(p), HistoryEntryKind.File)),
                .. folders.Where(p => string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase)).Select(p => new HistoryFileEntry(Path.GetFileName(p), HistoryEntryKind.Directory))];
    }
    public long Length(string path) { lock (data) return data.TryGetValue(path, out var bytes) ? bytes.Length : -1; }
    public void Write(string path, long offset, ReadOnlySpan<byte> bytes)
    {
        lock (data)
        {
            data.TryGetValue(path, out var file);
            if (file is null || file.Length < offset + bytes.Length) Array.Resize(ref file, (int)(offset + bytes.Length));
            bytes.CopyTo(file.AsSpan((int)offset));
            data[path] = file;
        }
    }
    public int Read(string path, long offset, Span<byte> destination)
    {
        Interlocked.Increment(ref Reads);
        lock (data)
        {
            if (!data.TryGetValue(path, out var file)) throw new FileNotFoundException(path);
            int count = (int)Math.Clamp(file.Length - offset, 0, destination.Length);
            file.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }
    }
    public void Truncate(string path, long length) { lock (data) { var file = data[path]; Array.Resize(ref file, (int)length); data[path] = file; } }
    public void ReplaceAtomically(string path, ReadOnlySpan<byte> bytes) { var copy = bytes.ToArray(); lock (data) data[path] = copy; }
    public void Delete(string path) { lock (data) data.Remove(path); }
    public bool RemoveDirectoryIfEmpty(string path)
    {
        lock (data)
        {
            if (data.Keys.Any(p => string.Equals(Path.GetDirectoryName(p), path, StringComparison.OrdinalIgnoreCase))) return false;
            folders.Remove(path); return true;
        }
    }
}

/// <summary>Timeline, retention, pins, round trip, runtime window, and the append path under a slow or failing disk.</summary>
[Collection("History")]
public sealed class HistoryStoreTests : IDisposable
{
    private const int Rate = 16000;
    private readonly string root = Path.Combine(Path.GetTempPath(), "soundpad-history-test-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryFixture.Clock clock = new();
    private readonly List<HistoryStore> stores = [];
    public void Dispose()
    {
        foreach (var store in stores) store.Close(true);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private HistoryStore Create(HistoryOptions? options = null)
    {
        var store = HistoryStore.Create(root, (options ?? new()) with { Rate = Rate, UtcNowMs = () => clock.Now });
        stores.Add(store);
        return store;
    }
    private static long S(double seconds) => (long)Math.Round(seconds * Rate);
    private const long T0 = HistoryFixture.T0;

    [Fact]
    public void OneFrameAxisAcrossStopsWithGapsAndWallClockMapping()
    {
        var store = Create(); var host = HistoryAccess.Host;
        Assert.Empty(store.Timeline(host).Spans);
        Assert.Null(store.Timeline(host).LiveWallUtcMs);
        Assert.False(store.EndRun(HistoryGapReason.Stopped)); // nothing to stop yet

        HistoryFixture.Feed(store, clock, 3);
        Assert.True(store.Timeline(host).Recording);
        Assert.True(store.EndRun(HistoryGapReason.Stopped));
        Assert.False(store.EndRun(HistoryGapReason.DeviceLost)); // the first reason stands
        var stopped = store.Timeline(host);
        Assert.False(stopped.Recording);
        Assert.Equal(new HistorySpan(0, S(3), T0), Assert.Single(stopped.Spans));
        Assert.Equal(new HistoryGap(S(3), T0 + 3000, null, "stopped"), Assert.Single(stopped.Gaps)); // open until audio returns
        Assert.Equal(T0 + 3000, stopped.LiveWallUtcMs);

        clock.Now += 5000; HistoryFixture.Feed(store, clock, 2);
        Assert.True(store.EndRun(HistoryGapReason.DeviceLost));
        clock.Now += 7000; HistoryFixture.Feed(store, clock, 1);

        var t = store.Timeline(host);
        Assert.Equal(S(6), t.LiveFrame); // gaps take no frames: 3 + 2 + 1 seconds of audio
        Assert.True(t.Recording);
        Assert.Equal(new HistorySpan[] { new(0, S(3), T0), new(S(3), S(5), T0 + 8000), new(S(5), S(6), T0 + 17000) }, t.Spans);
        Assert.Equal(new HistoryGap[] { new(S(3), T0 + 3000, T0 + 8000, "stopped"), new(S(5), T0 + 10000, T0 + 17000, "device-lost") }, t.Gaps);
        Assert.Equal(new HistoryRange[] { new(0, S(6)) }, t.Available);
        Assert.Equal(T0 + 18000, t.LiveWallUtcMs);

        Assert.Equal(T0, store.WallTimeOf(host, 0));
        Assert.Equal(T0 + 1000, store.WallTimeOf(host, S(1)));
        Assert.Equal(T0 + 2999, store.WallTimeOf(host, S(3) - 1));
        Assert.Equal(T0 + 8000, store.WallTimeOf(host, S(3))); // the frame after a gap belongs to the run that follows
        Assert.Equal(T0 + 8500, store.WallTimeOf(host, S(3.5)));
        Assert.Equal(T0 + 18000, store.WallTimeOf(host, S(6))); // the live edge
        HistoryFixture.Refused(() => store.WallTimeOf(host, S(6) + 1), HistoryUnavailableException.NotRecorded);

        Assert.Equal(0, store.FrameAtOrAfterWall(host, T0 - 5));
        Assert.Equal(S(1), store.FrameAtOrAfterWall(host, T0 + 1000));
        Assert.Equal(S(1) + 16, store.FrameAtOrAfterWall(host, T0 + 1001)); // 16 frames per millisecond
        Assert.True(store.WallTimeOf(host, S(1) + 16) >= T0 + 1001 && store.WallTimeOf(host, S(1) + 15) < T0 + 1001);
        Assert.Equal(S(3), store.FrameAtOrAfterWall(host, T0 + 5000)); // a time inside the gap: the first frame after it
        Assert.Equal(S(3), store.FrameAtOrAfterWall(host, T0 + 8000));
        Assert.Equal(S(5), store.FrameAtOrAfterWall(host, T0 + 12000));
        Assert.Equal(S(6), store.FrameAtOrAfterWall(host, T0 + 99999));
        Assert.Equal(S(6), store.FrameAtOrAfterWall(host, long.MaxValue));

        // A read across both gaps is the three runs back to back.
        Assert.Equal(HistoryFixture.Expected(S(2.5), (int)S(3)), store.ReadPcm16(host, S(2.5), (int)S(3)));

        // A clock that jumps backwards cannot make wall time run backwards along the frame axis.
        Assert.True(store.EndRun(HistoryGapReason.Stopped));
        clock.Now -= 60000; HistoryFixture.Feed(store, clock, 1);
        t = store.Timeline(host);
        Assert.Equal(new HistoryGap(S(6), T0 + 18000, T0 + 18000, "stopped"), t.Gaps[^1]);
        Assert.Equal(T0 + 18000, t.Spans[^1].WallStartUtcMs);

        Assert.Throws<ArgumentException>(() => store.EndRun("paused"));
    }

    [Fact]
    public void TenMinutesWithASixtySecondWindowExpiresUnlessPinnedWithinTheBudget()
    {
        const long segmentBytes = 10 * Rate * 2;
        var store = Create(new HistoryOptions { WindowSeconds = 60, PinBudgetBytes = 3 * segmentBytes }); var host = HistoryAccess.Host;

        HistoryFixture.Feed(store, clock, 100);
        store.Pin(host, "draft-a", S(85), S(99)); // segments 8 and 9
        HistoryFixture.Feed(store, clock, 200);
        var full = Assert.Throws<HistoryPinBudgetException>(() => store.Pin(host, "draft-b", S(270), S(295))); // three more segments
        Assert.Equal(5 * segmentBytes, full.RequiredBytes); Assert.Equal(3 * segmentBytes, full.BudgetBytes);
        Assert.Contains("full", full.Message);
        Assert.Equal(new[] { "draft-a" }, store.Pins(host).Keys);
        store.Pin(host, "draft-b", S(290), S(295)); // one segment fits
        HistoryFixture.Feed(store, clock, 300);
        Assert.True(store.Flush(30000));

        Assert.Equal(S(600), store.LiveFrame);
        var totals = store.Totals(host);
        Assert.Equal(S(540), totals.OldestFrame);
        Assert.Equal(60 * Rate * 2, totals.WindowBytes);
        Assert.Equal(3 * segmentBytes, totals.PinnedBytes);
        Assert.Equal(3 * segmentBytes, totals.PinnedOutsideWindowBytes);
        Assert.Equal(0, totals.UnflushedFrames); Assert.Equal(0, totals.Overruns); Assert.Null(totals.WriteError);
        // On disk: the six window segments and the three pinned ones, nothing else.
        Assert.Equal(new[] { 8, 9, 29, 54, 55, 56, 57, 58, 59 }.Select(i => $"seg-{i:D8}.pcm"), HistoryFixture.SegmentFiles(store.Folder));
        Assert.Equal(new HistoryRange[] { new(S(80), S(100)), new(S(290), S(300)), new(S(540), S(600)) }, store.Timeline(host).Available);

        Assert.Equal(HistoryFixture.Expected(S(85), (int)S(14)), store.ReadPcm16(host, S(85), (int)S(14))); // pinned, nine minutes old
        Assert.Equal(HistoryFixture.Expected(S(290), (int)S(5)), store.ReadPcm16(host, S(290), (int)S(5)));
        Assert.Equal(HistoryFixture.Expected(S(541), (int)S(58)), store.ReadPcm16(host, S(541), (int)S(58)));
        var expired = HistoryFixture.Refused(() => store.Read(host, S(100), Rate), HistoryUnavailableException.Expired);
        Assert.Equal(new HistoryRange(S(540), S(600)), expired.Window);
        Assert.Equal(3, expired.Available.Count);
        Assert.Contains("scrolled out", expired.Message);
        HistoryFixture.Refused(() => store.Read(host, S(99), 2 * Rate), HistoryUnavailableException.Expired); // half pinned is not enough
        HistoryFixture.Refused(() => store.Read(host, S(599), 2 * Rate), HistoryUnavailableException.NotRecorded);

        // Unpinning releases the audio and the budget.
        Assert.True(store.Unpin("draft-a")); Assert.False(store.Unpin("draft-a"));
        Assert.True(store.Flush(30000));
        Assert.DoesNotContain("seg-00000008.pcm", HistoryFixture.SegmentFiles(store.Folder));
        Assert.DoesNotContain("seg-00000009.pcm", HistoryFixture.SegmentFiles(store.Folder));
        HistoryFixture.Refused(() => store.Read(host, S(85), Rate), HistoryUnavailableException.Expired);
        Assert.Equal(segmentBytes, store.Totals(host).PinnedBytes);
        Assert.Throws<HistoryPinBudgetException>(() => store.Pin(host, "draft-c", S(545), S(565))); // 1 + 3 segments
        store.Pin(host, "draft-c", S(545), S(555)); // 1 + 2

        // Moving a pin: all or nothing, and only for a pin that exists.
        store.ReplacePin(host, "draft-b", S(290), S(296));
        Assert.Throws<InvalidDataException>(() => store.ReplacePin(host, "never-pinned", S(590), S(591)));
        store.ReplacePin(host, "draft-c", S(580), S(590));
        HistoryFixture.Refused(() => store.ReplacePin(host, "draft-c", S(100), S(101)), HistoryUnavailableException.Expired);
        HistoryFixture.Refused(() => store.Pin(host, "draft-c", S(595), S(601)), HistoryUnavailableException.NotRecorded);
        Assert.Equal(new HistoryRange(S(580), S(590)), store.Pins(host)["draft-c"]); // the failed moves changed nothing
        Assert.Equal(2 * segmentBytes, store.Totals(host).PinnedBytes);

        HistoryFixture.Feed(store, clock, 70);
        Assert.Equal(S(610), store.Totals(host).OldestFrame);
        Assert.Equal(2 * segmentBytes, store.Totals(host).PinnedOutsideWindowBytes);
        Assert.Equal(HistoryFixture.Expected(S(580), (int)S(10)), store.ReadPcm16(host, S(580), (int)S(10)));
        Assert.True(store.Unpin("draft-c"));
        HistoryFixture.Refused(() => store.Read(host, S(580), Rate), HistoryUnavailableException.Expired);

        // One run, never stopped: still one span and no gap after eleven minutes.
        var t = store.Timeline(host);
        Assert.Equal(new HistorySpan(0, S(670), T0), Assert.Single(t.Spans));
        Assert.Empty(t.Gaps);
        Assert.Equal(T0 + 670_000, store.WallTimeOf(host, S(670)));
    }

    [Fact]
    public void SamplesRoundTripWithin16BitQuantisationBeforeAndAfterTheyReachTheDisk()
    {
        var store = Create(); var host = HistoryAccess.Host;
        var random = new Random(7);
        var input = new float[25 * Rate + 311];
        for (int i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 2.4 - 1.2); // some of it clips
        float[] special = [1f, -1f, 0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.4f / 32767, 0.6f / 32767, -0.6f / 32767];
        special.CopyTo(input, 0);
        // Odd block sizes, so blocks straddle segment boundaries.
        for (int at = 0; at < input.Length;) { int count = Math.Min(777 + at % 5, input.Length - at); Assert.Equal(count, store.Append(input.AsSpan(at, count))); at += count; }

        var unflushed = store.Read(host, 0, input.Length); // partly still in memory
        Assert.True(store.Flush(30000));
        Assert.Equal(0, store.Totals(host).UnflushedFrames);
        var flushed = store.Read(host, 0, input.Length);
        Assert.Equal(unflushed, flushed);
        for (int i = 0; i < input.Length; i++)
        {
            float wanted = float.IsNaN(input[i]) ? 0 : Math.Clamp(input[i], -1f, 1f);
            Assert.True(Math.Abs(flushed[i] - wanted) <= 0.5f / 32767 + 1e-6f, $"sample {i}: {input[i]} came back as {flushed[i]}");
        }
        Assert.Equal(new[] { 1f, -1f, 0f, 0f, 1f, -1f, 0f, 1f / 32767, -1f / 32767 }, flushed[..special.Length]);
        var pcm = store.ReadPcm16(host, 0, input.Length);
        for (int i = 0; i < input.Length; i++) Assert.Equal(pcm[i] / 32767f, flushed[i]);
        Assert.Equal(-32767, pcm.Min()); // never -32768, so a waveform is symmetric

        Assert.Equal(0, store.Append([]));
        Assert.Throws<InvalidDataException>(() => store.Read(host, 0, 0));
        Assert.Throws<InvalidDataException>(() => store.Read(host, -1, 10));
        Assert.Throws<InvalidDataException>(() => store.Read(host, 0, 601 * Rate)); // MaxReadSeconds
    }

    [Fact]
    public void SilenceKeepsTheTimelineMovingWithoutABuffer()
    {
        var store = Create(new HistoryOptions { TailSeconds = 20 }); var host = HistoryAccess.Host;
        HistoryFixture.Feed(store, clock, 2);
        // The source went quiet for three seconds, then played again: one run, no gap, wall time still lines up.
        Assert.Equal(S(3), store.AppendSilence(S(3))); clock.Now += 3000;
        HistoryFixture.Feed(store, clock, 1);
        var t = store.Timeline(host);
        Assert.Equal(new HistorySpan(0, S(6), T0), Assert.Single(t.Spans));
        Assert.Empty(t.Gaps);
        Assert.Equal(T0 + 5500, store.WallTimeOf(host, S(5.5)));
        Assert.All(store.ReadPcm16(host, S(2), (int)S(3)), v => Assert.Equal(0, v));
        Assert.Equal(HistoryFixture.Expected(S(5), Rate), store.ReadPcm16(host, S(5), Rate));
        Assert.All(store.Levels(host, S(2), S(5), 300), v => Assert.Equal(0, v));
        Assert.Equal(0, store.AppendSilence(0)); Assert.Equal(0, store.AppendSilence(-5));
        // It crosses segment boundaries and obeys the same in-memory cap as audio.
        Assert.True(store.Flush(30000));
        Assert.Equal(S(20), store.AppendSilence(S(120)));
        Assert.Equal(S(26), store.LiveFrame);
        Assert.Equal("overrun", Assert.Single(store.Timeline(host).Gaps).Reason);
        Assert.Equal(S(100), store.Totals(host).DroppedFrames);
        Assert.True(store.Flush(30000));
        Assert.All(store.ReadPcm16(host, S(6), (int)S(20)), v => Assert.Equal(0, v));
    }

    [Fact]
    public void TheWindowCanChangeWhileRecordingAndOldRunsLeaveTheTimeline()
    {
        var store = Create(new HistoryOptions { WindowSeconds = 120 }); var host = HistoryAccess.Host;
        HistoryFixture.Feed(store, clock, 20);
        store.EndRun(HistoryGapReason.Stopped);
        clock.Now += 4000; HistoryFixture.Feed(store, clock, 180);
        Assert.Equal(120, store.WindowSeconds);
        Assert.Equal(S(80), store.Totals(host).OldestFrame);
        // The first run and the gap after it describe nothing that is still held.
        Assert.Equal(new HistorySpan(S(20), S(200), T0 + 24000), Assert.Single(store.Timeline(host).Spans));
        Assert.Empty(store.Timeline(host).Gaps);

        store.SetWindow(60);
        Assert.Equal(S(140), store.Totals(host).OldestFrame);
        HistoryFixture.Refused(() => store.Read(host, S(100), Rate), HistoryUnavailableException.Expired);
        Assert.True(store.Flush(30000));
        Assert.Equal(6, HistoryFixture.SegmentFiles(store.Folder).Length);
        store.SetWindow(3600); // growing keeps what is there and stops expiring
        HistoryFixture.Feed(store, clock, 30);
        Assert.Equal(S(140), store.Totals(host).OldestFrame);
        Assert.Equal(3600, store.Totals(host).WindowSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetWindow(59));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetWindow(3601));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryStore.Create(root, new HistoryOptions { WindowSeconds = 30 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryStore.Create(root, new HistoryOptions { TailSeconds = 15 }));
        Assert.Equal(600, new HistoryOptions().WindowSeconds); // the plan's default: ten minutes
    }

    [Fact]
    public void AStalledDiskNeverBlocksAppendAndEndsInAnHonestOverrunGap()
    {
        var files = new TestFiles(); files.WriteGate.Reset();
        var store = Create(new HistoryOptions { TailSeconds = 20, Files = files }); var host = HistoryAccess.Host;
        var watch = new Stopwatch(); double worstMs = 0;
        for (int i = 0; i < 10; i++) Assert.Equal(Rate, store.Append(HistoryFixture.Signal(S(i), Rate)));
        clock.Now += 10000;
        Assert.True(files.WriteWaiting.Wait(10000)); // the writer is now stuck inside the first segment's write

        // Nine more seconds arrive while the disk is stuck. Every append returns at once.
        for (int i = 10; i < 19; i++)
        {
            var block = HistoryFixture.Signal(S(i), Rate);
            watch.Restart(); Assert.Equal(Rate, store.Append(block)); worstMs = Math.Max(worstMs, watch.Elapsed.TotalMilliseconds);
            clock.Now += 1000;
        }
        Assert.True(worstMs < 250, $"Append took {worstMs} ms while the disk was stalled.");
        Assert.False(files.WriteGate.IsSet);
        Assert.Equal(S(19), store.Totals(host).UnflushedFrames);
        // Readers see all of it although nothing has reached the disk.
        Assert.Equal(HistoryFixture.Expected(0, (int)S(19)), store.ReadPcm16(host, 0, (int)S(19)));
        Assert.Empty(HistoryFixture.SegmentFiles(store.Folder));

        // The tail is capped at 20 s: of the next two seconds only one fits. The rest is dropped, not queued.
        watch.Restart();
        Assert.Equal(Rate, store.Append(HistoryFixture.Signal(S(19), 2 * Rate)));
        Assert.Equal(0, store.Append(HistoryFixture.Signal(S(21), Rate)));
        Assert.True(watch.Elapsed.TotalMilliseconds < 250);
        clock.Now += 3000;
        var totals = store.Totals(host);
        Assert.Equal(1, totals.Overruns); Assert.Equal(2 * Rate, totals.DroppedFrames); Assert.Equal(S(20), totals.UnflushedFrames);
        var stalled = store.Timeline(host);
        Assert.False(stalled.Recording);
        Assert.Equal(new HistoryGap(S(20), T0 + 20000, null, "overrun"), Assert.Single(stalled.Gaps));
        Assert.False(store.Flush(200)); // and Flush says so instead of hanging

        // The disk comes back: everything held is written, and recording resumes as a new run after the gap.
        files.WriteGate.Set();
        Assert.True(store.Flush(30000));
        Assert.Equal(0, store.Totals(host).UnflushedFrames);
        clock.Now += 5000;
        Assert.Equal(Rate, store.Append(HistoryFixture.Signal(S(500), Rate)));
        var t = store.Timeline(host);
        Assert.Equal(new HistorySpan[] { new(0, S(20), T0), new(S(20), S(21), T0 + 27000) }, t.Spans);
        Assert.Equal(new HistoryGap(S(20), T0 + 20000, T0 + 27000, "overrun"), Assert.Single(t.Gaps));
        Assert.Equal(HistoryFixture.Expected(0, (int)S(20)), store.ReadPcm16(host, 0, (int)S(20)));
        Assert.Equal(HistoryFixture.Expected(S(500), Rate), store.ReadPcm16(host, S(20), Rate)); // the dropped seconds take no frames
    }

    [Fact]
    public void ASlowDiskDelaysOnlyTheWriterNotTheAppender()
    {
        var files = new TestFiles { WriteDelayMs = 1000 };
        var store = Create(new HistoryOptions { Files = files }); var host = HistoryAccess.Host;
        var watch = new Stopwatch(); double worstMs = 0; var whole = Stopwatch.StartNew();
        // 15 s in 10 ms packets, the size a capture device delivers.
        for (long at = 0; at < S(15); at += Rate / 100)
        {
            var packet = HistoryFixture.Signal(at, Rate / 100);
            watch.Restart(); Assert.Equal(packet.Length, store.Append(packet)); worstMs = Math.Max(worstMs, watch.Elapsed.TotalMilliseconds);
        }
        Assert.True(worstMs < 250, $"Append took {worstMs} ms with a one-second disk.");
        Assert.True(whole.ElapsedMilliseconds < 900, "The appender waited for the slow write."); // the first write alone takes 1000 ms
        Assert.Equal(HistoryFixture.Expected(0, (int)S(15)), store.ReadPcm16(host, 0, (int)S(15)));
        Assert.True(store.Flush(30000));
        Assert.True(files.Writes >= 2);
        Assert.Equal(0, store.Totals(host).Overruns);
        Assert.Equal(HistoryFixture.Expected(0, (int)S(15)), store.ReadPcm16(host, 0, (int)S(15))); // now from disk and memory
    }

    [Fact]
    public void WriteErrorsKeepTheAudioInMemoryAndTheWriterRetries()
    {
        var files = new TestFiles { FailWrites = true };
        var store = Create(new HistoryOptions { Files = files }); var host = HistoryAccess.Host;
        HistoryFixture.Feed(store, clock, 9);
        Assert.Equal(Rate, store.Append(HistoryFixture.Signal(S(9), Rate))); // completes the first segment; its write fails
        Assert.Equal(2 * Rate, store.Append(HistoryFixture.Signal(S(10), 2 * Rate)));
        Assert.False(store.Flush(400));
        Assert.Contains("space", store.Totals(host).WriteError);
        Assert.Equal(S(12), store.Totals(host).UnflushedFrames);
        Assert.Equal(HistoryFixture.Expected(0, (int)S(12)), store.ReadPcm16(host, 0, (int)S(12)));

        files.FailWrites = false;
        Assert.True(store.Flush(30000));
        Assert.Null(store.Totals(host).WriteError);
        Assert.Equal(0, store.Totals(host).UnflushedFrames);
        Assert.Equal(10 * Rate * 2, new FileInfo(Path.Combine(store.Folder, "seg-00000000.pcm")).Length);
        Assert.Equal(2 * Rate * 2, new FileInfo(Path.Combine(store.Folder, "seg-00000001.pcm")).Length);
        Assert.Equal(HistoryFixture.Expected(0, (int)S(12)), store.ReadPcm16(host, 0, (int)S(12)));
    }

    [Fact]
    public void AnOrdinaryQuitRemovesTheSessionFolderAndOnlyItsOwnFiles()
    {
        var store = HistoryStore.Create(root, new HistoryOptions { Rate = Rate });
        HistoryFixture.Feed(store, clock, 25);
        Assert.True(Directory.Exists(store.Folder));
        Assert.StartsWith(Path.Combine(root, "history"), store.Folder);
        Assert.True(store.Close(true));
        Assert.False(Directory.Exists(store.Folder));
        Assert.Empty(HistoryStore.Recover(root));
        // A closed store refuses politely; a late capture packet is ignored.
        Assert.Equal(0, store.Append(new float[10]));
        HistoryFixture.Refused(() => store.Read(HistoryAccess.Host, 0, 10), HistoryUnavailableException.Closed);
        HistoryFixture.Refused(() => store.Timeline(HistoryAccess.Host), HistoryUnavailableException.Closed);
        Assert.False(store.Flush(10));
        Assert.True(store.Close(true)); // closing twice is harmless

        // Something else in the folder is not ours to delete: it stays, our files go, and Close reports it.
        var second = HistoryStore.Create(root, new HistoryOptions { Rate = Rate });
        HistoryFixture.Feed(second, clock, 12);
        File.WriteAllText(Path.Combine(second.Folder, "notes.txt"), "not ours");
        Assert.False(second.Close(true));
        Assert.Equal(new[] { "notes.txt" }, Directory.GetFileSystemEntries(second.Folder).Select(p => Path.GetFileName(p)!));
    }
}
