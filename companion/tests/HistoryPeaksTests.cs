using System.Diagnostics;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Levels: the peak pyramid against brute-force scans, the sample path for deep zoom, and clipping.</summary>
[Collection("History")]
public sealed class HistoryPeaksTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "soundpad-history-test-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryFixture.Clock clock = new();
    private readonly List<HistoryStore> stores = [];
    public void Dispose()
    {
        foreach (var store in stores) store.Close(true);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private HistoryStore Create(HistoryOptions options)
    {
        var store = HistoryStore.Create(root, options with { UtcNowMs = () => clock.Now });
        stores.Add(store);
        return store;
    }

    /// <summary>Full-scale noise under a slow envelope, so neighbouring blocks have clearly different extremes and a misplaced block shows.</summary>
    private static short Shaped(long frame) => (short)(HistoryFixture.Pcm(frame) * (frame % 9973 / 9973.0));

    /// <summary>What Levels promises for the pyramid path: each bin is the exact extremes of its frames widened to the 64-frame blocks they touch, up to live.</summary>
    private static void AssertPyramid(HistoryStore store, long live, long segmentFrames, long start, long end, int bins)
    {
        var levels = store.Levels(HistoryAccess.Host, start, end, bins);
        Assert.Equal(bins * 2, levels.Length);
        for (int b = 0; b < bins; b++)
        {
            long from = start + (end - start) * b / bins, to = Math.Min(start + (end - start) * (b + 1) / bins, live);
            (short Min, short Max) wanted = (0, 0);
            if (from < to)
            {
                var (lo, hi) = HistoryFixture.Outward(from, to, segmentFrames);
                wanted = HistoryFixture.Brute(Shaped, lo, Math.Min(hi, live));
            }
            Assert.True(wanted == (levels[b * 2], levels[b * 2 + 1]), $"bin {b} of {bins} over [{start}, {end}): wanted {wanted}, got ({levels[b * 2]}, {levels[b * 2 + 1]})");
        }
    }

    [Fact]
    public void PeaksEqualABruteForceScanAtEveryLevelWithoutReadingSamples()
    {
        const int rate = 16000; const long segment = 10 * rate;
        var files = new TestFiles();
        var store = Create(new HistoryOptions { Rate = rate, Files = files }); var host = HistoryAccess.Host;
        HistoryFixture.Feed(store, clock, 25, Shaped);
        Assert.Equal(37, store.Append(HistoryFixture.Signal(store.LiveFrame, 37, Shaped))); // live is not on a block boundary
        Assert.True(store.Flush(30000)); // segments 0 and 1 are now on disk only
        long live = store.LiveFrame;
        int readsBefore = files.Reads;

        // Each pyramid level: bins exactly one block of that level wide, lined up with it, across segment boundaries.
        foreach (int block in new[] { 64, 512, 4096, 32768 })
        {
            long start = 120000 / block * block; int bins = (int)Math.Min(2048, (live - start) / block);
            var levels = store.Levels(host, start, start + (long)bins * block, bins);
            for (int b = 0; b < bins; b++)
                Assert.True(HistoryFixture.Brute(Shaped, start + (long)b * block, start + (long)(b + 1) * block) == (levels[b * 2], levels[b * 2 + 1]), $"block size {block}, bin {b}");
        }

        // The main view: the newest ten seconds at 2048 bins, and one bin for everything.
        AssertPyramid(store, live, segment, live - 10 * rate, live, 2048);
        AssertPyramid(store, live, segment, 0, live, 1);
        // Arbitrary windows and bin counts, including windows that reach past the live edge.
        var random = new Random(11);
        for (int i = 0; i < 150; i++)
        {
            int bins = random.Next(1, 2049);
            long start = random.NextInt64(0, live), length = random.NextInt64((long)bins * 64, Math.Max((long)bins * 64 + 1, live + 50000));
            AssertPyramid(store, live, segment, start, start + length, bins);
        }
        Assert.Equal(readsBefore, files.Reads); // none of that touched a sample on disk

        // Deep zoom: bins narrower than a block come from the samples and are exact per bin.
        var zoom = store.Levels(host, 150000, 153000, 1000);
        for (int b = 0; b < 1000; b++) Assert.True(HistoryFixture.Brute(Shaped, 150000 + 3 * b, 150003 + 3 * b) == (zoom[b * 2], zoom[b * 2 + 1]), $"zoom bin {b}");
        Assert.True(files.Reads > readsBefore);
        // More bins than frames, across a segment boundary: every bin still shows at least its one frame.
        var tiny = store.Levels(host, 159950, 160050, 300);
        for (int b = 0; b < 300; b++)
        {
            long from = 159950 + 100L * b / 300, to = Math.Max(from + 1, 159950 + 100L * (b + 1) / 300);
            Assert.True(HistoryFixture.Brute(Shaped, from, to) == (tiny[b * 2], tiny[b * 2 + 1]), $"tiny bin {b}");
        }

        Assert.All(store.Levels(host, live + 1000, live + 1640, 10), v => Assert.Equal(0, v)); // nothing recorded there
        Assert.All(store.Levels(host, 0, live, 4096), v => Assert.InRange(v, (short)-32767, (short)32767));
        Assert.Throws<InvalidDataException>(() => store.Levels(host, 0, live, 0));
        Assert.Throws<InvalidDataException>(() => store.Levels(host, 0, live, 4097));
        Assert.Throws<InvalidDataException>(() => store.Levels(host, 100, 100, 10));
        Assert.Throws<InvalidDataException>(() => store.Levels(host, -64, 640, 10));
    }

    [Theory]
    [InlineData(48000)] // production
    [InlineData(44100)] // ten seconds is not a whole number of 64-frame blocks: blocks restart at each segment
    public void OtherSampleRatesKeepTheSameExactness(int rate)
    {
        long segment = 10L * rate;
        var store = Create(new HistoryOptions { Rate = rate });
        HistoryFixture.Feed(store, clock, 12, Shaped);
        long live = store.LiveFrame;
        Assert.Equal(12L * rate, live);
        Assert.Equal(HistoryFixture.Expected(live - rate, rate, Shaped), store.ReadPcm16(HistoryAccess.Host, live - rate, rate));

        AssertPyramid(store, live, segment, live - 10 * rate, live, 2048); // the ten-second main view
        var random = new Random(rate);
        for (int i = 0; i < 40; i++)
        {
            int bins = random.Next(1, 2049);
            long start = random.NextInt64(0, live - (long)bins * 64);
            AssertPyramid(store, live, segment, start, start + random.NextInt64((long)bins * 64, live - start + 1), bins);
        }
    }

    [Fact]
    public void ASixtyMinuteOverviewIsAnsweredFromThePyramidAlone()
    {
        const int rate = 8000; const long segment = 10 * rate;
        var files = new MemoryFiles();
        var store = Create(new HistoryOptions { Rate = rate, WindowSeconds = 3600, TailSeconds = 120, Files = files }); var host = HistoryAccess.Host;
        HistoryFixture.Feed(store, clock, 3600, Shaped, flushEverySeconds: 60);
        Assert.True(store.Flush(30000));
        long live = store.LiveFrame;
        Assert.Equal(new HistoryRange[] { new(0, 3600L * rate) }, store.Timeline(host).Available); // the whole hour is held
        Assert.Equal(3600L * rate * 2, store.Totals(host).WindowBytes);

        int readsBefore = files.Reads;
        var watch = Stopwatch.StartNew();
        var overview = store.Levels(host, 0, live, 2048);
        watch.Stop();
        Assert.Equal(readsBefore, files.Reads);
        Assert.True(watch.ElapsedMilliseconds < 500, $"The hour overview took {watch.ElapsedMilliseconds} ms.");
        Assert.Equal(4096, overview.Length);
        AssertPyramid(store, live, segment, 0, live, 2048);
        AssertPyramid(store, live, segment, 0, live, 1);
        AssertPyramid(store, live, segment, 1234567, 23456789, 777);
        Assert.Equal(readsBefore, files.Reads);
    }

    [Fact]
    public void LevelsClipToWhatIsStillHeldInsteadOfRefusing()
    {
        const int rate = 16000;
        var store = Create(new HistoryOptions { Rate = rate, WindowSeconds = 60 }); var host = HistoryAccess.Host;
        HistoryFixture.Feed(store, clock, 100, Shaped);
        store.Pin(host, "kept", 95 * rate, 96 * rate);
        HistoryFixture.Feed(store, clock, 100, Shaped); // held now: 90-100 s (pinned segment) and 140-200 s
        var levels = store.Levels(host, 0, 200 * rate, 200); // one bin per second
        for (int b = 0; b < 200; b++)
        {
            bool held = b is >= 90 and < 100 or >= 140;
            if (!held) Assert.True(levels[b * 2] == 0 && levels[b * 2 + 1] == 0, $"second {b} is no longer held");
            else Assert.True(HistoryFixture.Brute(Shaped, (long)b * rate, (long)(b + 1) * rate) == (levels[b * 2], levels[b * 2 + 1]), $"second {b}");
        }
        // The same question as a read is refused, with the ranges that would work.
        var ex = HistoryFixture.Refused(() => store.Read(host, 130 * rate, 20 * rate), HistoryUnavailableException.Expired);
        Assert.Equal(new HistoryRange[] { new(90 * rate, 100 * rate), new(140 * rate, 200 * rate) }, ex.Available);
    }
}
