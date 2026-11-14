using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>What is left on disk after a crash, an update or a quit: Recover, Open and Discard.</summary>
[Collection("History")]
public sealed class HistoryRecoveryTests : IDisposable
{
    private const int Rate = 16000;
    private const long T0 = HistoryFixture.T0;
    private readonly string root = Path.Combine(Path.GetTempPath(), "soundpad-history-test-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryFixture.Clock clock = new();
    private readonly List<HistoryStore> stores = [];
    public void Dispose()
    {
        foreach (var store in stores) store.Abandon(); // releases the folder whatever state a failed test left
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private static long S(double seconds) => (long)Math.Round(seconds * Rate);
    private HistoryOptions Options => new() { Rate = Rate, UtcNowMs = () => clock.Now };
    private HistoryStore Create(string id) { var store = HistoryStore.Create(root, Options, id); stores.Add(store); return store; }
    private HistoryStore Open(string id, string reason, HistoryOptions? options = null) { var store = HistoryStore.Open(root, id, reason, options ?? Options); stores.Add(store); return store; }
    private string Segment(string id, int index) => Path.Combine(root, "history", id, $"seg-{index:D8}.pcm");
    private string Index(string id) => Path.Combine(root, "history", id, "index.json");
    private static readonly HistoryAccess Host = HistoryAccess.Host;

    [Fact]
    public void ACrashKeepsEverythingUpToTheLastIndexAndRestoresPins()
    {
        var store = Create("crash-1");
        HistoryFixture.Feed(store, clock, 23);
        store.Pin(Host, "draft-1", S(5), S(12));
        Assert.True(store.Flush(30000));
        HistoryFixture.Feed(store, clock, 2); // only in memory when the process dies
        store.Abandon(); // killed: no Close, no flush

        var found = Assert.Single(HistoryStore.Recover(root));
        Assert.Equal(("crash-1", true, false, false), (found.SessionId, found.Valid, found.CleanlyClosed, found.InUse));
        Assert.Equal((Rate, S(23), S(23), 1), (found.Rate, found.Frames, found.RetainedFrames, found.Pins));
        Assert.Equal((S(23) * 2, T0, T0 + 23000), (found.Bytes, found.CreatedUtcMs, found.UpdatedUtcMs));
        // Bytes the index never described (a write that was cut off by the crash) must not become audio.
        using (var file = new FileStream(Segment("crash-1", 2), FileMode.Append)) file.Write(new byte[500]);

        var reopened = Open("crash-1", HistoryGapReason.Crash);
        var recovery = reopened.Recovery!;
        Assert.Equal((S(23), 0L, 0, false, "crash"), (recovery.RecoveredFrames, recovery.LostFrames, recovery.DroppedSegments, recovery.TornTail, recovery.GapReason));
        Assert.Empty(recovery.DroppedPins);
        Assert.Equal(S(23), reopened.LiveFrame);
        Assert.Equal(HistoryFixture.Expected(0, (int)S(23)), reopened.ReadPcm16(Host, 0, (int)S(23)));
        Assert.Equal(new HistoryRange(S(5), S(12)), Assert.Single(reopened.Pins(Host)).Value);
        Assert.Equal(S(3) * 2, new FileInfo(Segment("crash-1", 2)).Length);
        var t = reopened.Timeline(Host);
        Assert.False(t.Recording);
        Assert.Equal(new HistorySpan(0, S(23), T0), Assert.Single(t.Spans));
        Assert.Equal(new HistoryGap(S(23), T0 + 23000, null, "crash"), Assert.Single(t.Gaps));
        // Peaks were rebuilt from the audio.
        var levels = reopened.Levels(Host, 0, 640 * 500, 500);
        for (int b = 0; b < 500; b++) Assert.True(HistoryFixture.Brute(HistoryFixture.Pcm, 640L * b, 640L * (b + 1)) == (levels[b * 2], levels[b * 2 + 1]), $"bin {b}");

        // While it is open it cannot be opened again or discarded.
        Assert.True(Assert.Single(HistoryStore.Recover(root)).InUse);
        Assert.Throws<InvalidDataException>(() => HistoryStore.Open(root, "crash-1", HistoryGapReason.Crash, Options));
        Assert.Throws<InvalidDataException>(() => HistoryStore.Discard(root, "crash-1"));
        Assert.Throws<InvalidDataException>(() => HistoryStore.Create(root, Options, "crash-1"));

        // Recording continues on the same frame axis, inside the same segment file, after an honest gap.
        clock.Now = T0 + 85000;
        HistoryFixture.Feed(reopened, clock, 7);
        Assert.True(reopened.Flush(30000));
        t = reopened.Timeline(Host);
        Assert.Equal(new HistorySpan[] { new(0, S(23), T0), new(S(23), S(30), T0 + 85000) }, t.Spans);
        Assert.Equal(new HistoryGap(S(23), T0 + 23000, T0 + 85000, "crash"), Assert.Single(t.Gaps));
        Assert.Equal(HistoryFixture.Expected(S(20), (int)S(10)), reopened.ReadPcm16(Host, S(20), (int)S(10)));

        // And the result survives another restart byte for byte.
        Assert.True(reopened.Close(false));
        var again = Open("crash-1", HistoryGapReason.Update);
        Assert.Equal(HistoryFixture.Expected(0, (int)S(30)), again.ReadPcm16(Host, 0, (int)S(30)));
        Assert.Equal(2, again.Timeline(Host).Gaps.Length);
        Assert.True(again.Close(true));
        Assert.Empty(HistoryStore.Recover(root));
    }

    [Fact]
    public void ATornNewestSegmentIsDroppedAndTheTimelineEndsWhereIntactAudioEnds()
    {
        var store = Create("torn-1");
        HistoryFixture.Feed(store, clock, 25);
        store.Pin(Host, "in-tail", S(21), S(24));
        store.Pin(Host, "early", S(2), S(4));
        store.Pin(Host, "straddle", S(15), S(23));
        Assert.True(store.Flush(30000));
        store.Abandon();
        using (var file = new FileStream(Segment("torn-1", 2), FileMode.Open)) file.SetLength(1000); // the index promised five seconds

        var reopened = Open("torn-1", HistoryGapReason.Crash);
        var recovery = reopened.Recovery!;
        Assert.Equal((S(20), S(5), 1, true, "crash"), (recovery.RecoveredFrames, recovery.LostFrames, recovery.DroppedSegments, recovery.TornTail, recovery.GapReason));
        Assert.Equal(new[] { "in-tail" }, recovery.DroppedPins);
        Assert.Equal(S(20), reopened.LiveFrame);
        Assert.False(File.Exists(Segment("torn-1", 2)));
        var pins = reopened.Pins(Host);
        Assert.Equal(2, pins.Count);
        Assert.Equal(new HistoryRange(S(2), S(4)), pins["early"]);
        Assert.Equal(new HistoryRange(S(15), S(20)), pins["straddle"]); // cut back to the audio that survived
        Assert.Equal(HistoryFixture.Expected(0, (int)S(20)), reopened.ReadPcm16(Host, 0, (int)S(20)));
        HistoryFixture.Refused(() => reopened.Read(Host, S(19), 2 * Rate), HistoryUnavailableException.NotRecorded);
        Assert.Equal(new HistoryGap(S(20), T0 + 20000, null, "crash"), Assert.Single(reopened.Timeline(Host).Gaps));

        HistoryFixture.Feed(reopened, clock, 5);
        Assert.True(reopened.Flush(30000));
        Assert.Equal(HistoryFixture.Expected(S(15), (int)S(10)), reopened.ReadPcm16(Host, S(15), (int)S(10)));
    }

    [Fact]
    public void AMissingOlderSegmentLeavesAHoleNotAShift()
    {
        var store = Create("hole-1");
        HistoryFixture.Feed(store, clock, 35);
        store.Pin(Host, "early", S(2), S(4));
        Assert.True(store.Flush(30000));
        store.Abandon();
        File.Delete(Segment("hole-1", 1));

        var reopened = Open("hole-1", HistoryGapReason.Crash);
        var recovery = reopened.Recovery!;
        Assert.Equal((S(35), 0L, 1, false), (recovery.RecoveredFrames, recovery.LostFrames, recovery.DroppedSegments, recovery.TornTail));
        Assert.Empty(recovery.DroppedPins);
        Assert.Equal(new HistoryRange[] { new(0, S(10)), new(S(20), S(35)) }, reopened.Timeline(Host).Available);
        Assert.Equal(S(20), reopened.Totals(Host).OldestFrame);
        Assert.Equal(HistoryFixture.Expected(S(1), (int)S(8)), reopened.ReadPcm16(Host, S(1), (int)S(8))); // kept by its pin
        Assert.Equal(HistoryFixture.Expected(S(20), (int)S(15)), reopened.ReadPcm16(Host, S(20), (int)S(15)));
        HistoryFixture.Refused(() => reopened.Read(Host, S(15), Rate), HistoryUnavailableException.Expired);
        HistoryFixture.Refused(() => reopened.Read(Host, S(5), (int)S(20)), HistoryUnavailableException.Expired);
        // Once nothing pins it, the island before the hole goes like any expired audio.
        Assert.True(reopened.Unpin("early"));
        Assert.True(reopened.Flush(30000));
        Assert.False(File.Exists(Segment("hole-1", 0)));
    }

    [Fact]
    public void ADamagedIndexIsReportedAndCanOnlyBeDiscarded()
    {
        var store = Create("bad-1");
        HistoryFixture.Feed(store, clock, 12);
        Assert.True(store.Flush(30000));
        store.Abandon();
        var good = File.ReadAllText(Index("bad-1"));

        // Well-formed JSON that contradicts itself is as unusable as garbage.
        foreach (var damaged in new[] { "{ not json", "null", good.Replace($"\"live\":{S(12)}", "\"live\":5"), good.Replace("\"format\":1", "\"format\":2"), good.Replace("\"sessionId\":\"bad-1\"", "\"sessionId\":\"other\"") })
        {
            Assert.NotEqual(good, damaged);
            File.WriteAllText(Index("bad-1"), damaged);
            var found = Assert.Single(HistoryStore.Recover(root));
            Assert.False(found.Valid); Assert.NotNull(found.Problem); Assert.Equal(S(12) * 2, found.Bytes);
            Assert.Throws<InvalidDataException>(() => HistoryStore.Open(root, "bad-1", HistoryGapReason.Crash, Options));
        }
        Assert.Equal(2, HistoryFixture.SegmentFiles(Path.GetDirectoryName(Index("bad-1"))!).Length); // a failed Open deletes nothing
        HistoryStore.Discard(root, "bad-1");
        Assert.False(Directory.Exists(Path.GetDirectoryName(Index("bad-1"))));
        Assert.Empty(HistoryStore.Recover(root));
    }

    [Fact]
    public void AnUpdateKeepsHistoryAndMarksTheGap()
    {
        var store = Create("update-1");
        HistoryFixture.Feed(store, clock, 12);
        store.Pin(Host, "draft", S(1), S(2));
        Assert.True(store.Close(false)); // capture was running when the update began
        var found = Assert.Single(HistoryStore.Recover(root));
        Assert.Equal((true, true, S(12), false), (found.Valid, found.CleanlyClosed, found.Frames, found.InUse));

        clock.Now += 30000;
        var next = Open("update-1", HistoryGapReason.Update, Options with { WindowSeconds = 60 });
        Assert.Equal(60, next.WindowSeconds); // settings may change across the restart; the rate is the session's
        Assert.Equal(Rate, next.Rate);
        Assert.Equal("update", next.Recovery!.GapReason);
        Assert.Equal(new HistoryGap(S(12), T0 + 12000, null, "update"), Assert.Single(next.Timeline(Host).Gaps));
        Assert.Equal(new HistoryRange(S(1), S(2)), next.Pins(Host)["draft"]);
        // The index stops saying "cleanly closed" as soon as the session is live again.
        Assert.Equal((false, true), (HistoryStore.Recover(root)[0].CleanlyClosed, HistoryStore.Recover(root)[0].InUse));
        HistoryFixture.Feed(next, clock, 3);
        Assert.Equal(new HistoryGap(S(12), T0 + 12000, T0 + 42000, "update"), Assert.Single(next.Timeline(Host).Gaps));
        Assert.Equal(HistoryFixture.Expected(0, (int)S(15)), next.ReadPcm16(Host, 0, (int)S(15)));

        // Capture that was already stopped keeps its own reason: the update lost no audio.
        Assert.True(next.EndRun(HistoryGapReason.Stopped));
        Assert.True(next.Close(false));
        clock.Now += 5000;
        var third = Open("update-1", HistoryGapReason.Update);
        Assert.Equal("stopped", third.Recovery!.GapReason);
        Assert.Equal(new HistoryGap(S(15), T0 + 45000, null, "stopped"), third.Timeline(Host).Gaps[^1]);
        Assert.Equal(2, third.Timeline(Host).Spans.Length);
        Assert.Throws<ArgumentException>(() => HistoryStore.Open(root, "update-1", "restart", Options));
        Assert.True(third.Close(true));
        Assert.Empty(HistoryStore.Recover(root));
    }

    [Fact]
    public void DiscardDeletesOnlyAFolderThatHoldsNothingButThisModulesFiles()
    {
        var store = Create("old-1");
        HistoryFixture.Feed(store, clock, 12);
        Assert.True(store.Flush(30000));
        store.Abandon();
        var folder = Path.GetDirectoryName(Index("old-1"))!;
        string[] Listing() => [.. Directory.GetFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).Order()];
        var ours = Listing();
        Assert.Equal(new[] { "index.json", "seg-00000000.pcm", "seg-00000001.pcm" }, ours);

        // Any foreign file stops the whole deletion, also one that merely resembles ours.
        foreach (var foreign in new[] { "notes.txt", "seg-1.pcm", "seg-0000000a.pcm", "seg-00000000.pcm.bak", "index.json.old" })
        {
            File.WriteAllText(Path.Combine(folder, foreign), "someone else's");
            var ex = Assert.Throws<InvalidDataException>(() => HistoryStore.Discard(root, "old-1"));
            Assert.Contains(foreign, ex.Message);
            Assert.Equal(ours.Append(foreign).Order(), Listing()); // nothing was deleted
            File.Delete(Path.Combine(folder, foreign));
        }
        // So does a folder inside it: the module never recurses.
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "seg-00000005.pcm"), "x");
        Assert.Throws<InvalidDataException>(() => HistoryStore.Discard(root, "old-1"));
        Assert.Equal(ours.Append("sub").Order(), Listing());
        Directory.Delete(Path.Combine(folder, "sub"), true);

        // Other things under history/ are neither listed nor touched.
        Directory.CreateDirectory(Path.Combine(root, "history", "not a session!"));
        File.WriteAllText(Path.Combine(root, "history", "stray.txt"), "x");
        Assert.Equal("old-1", Assert.Single(HistoryStore.Recover(root)).SessionId);

        HistoryStore.Discard(root, "old-1");
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Path.Combine(root, "history", "not a session!")));
        Assert.True(File.Exists(Path.Combine(root, "history", "stray.txt")));
        HistoryStore.Discard(root, "old-1"); // already gone: fine
        HistoryStore.Discard(root, "never-existed");

        // A session id cannot name anything outside history/.
        foreach (var id in new[] { "..", "a/b", @"a\b", "", "con:1", new string('x', 65) })
        {
            Assert.Throws<ArgumentException>(() => HistoryStore.Discard(root, id));
            Assert.Throws<ArgumentException>(() => HistoryStore.Open(root, id, HistoryGapReason.Crash));
            Assert.Throws<ArgumentException>(() => HistoryStore.Create(root, Options, id));
        }
    }

    // ---- 2.41: discarding only what a crash recovered ----------------------------------------------------------

    [Fact]
    public void DiscardBeforeKeepsNewAudioPinsAndFramesAndAnOlderReaderOnlyFindsSilence()
    {
        var store = Create("discard-1");
        HistoryFixture.Feed(store, clock, 23);
        store.Pin(Host, "old", S(1), S(3));
        Assert.True(store.Flush(30000));
        store.Abandon();
        var reopened = Open("discard-1", HistoryGapReason.Crash);
        long boundary = reopened.LiveFrame;
        HistoryFixture.Feed(reopened, clock, 4);
        reopened.Pin(Host, "new", boundary + S(1), boundary + S(2));
        reopened.Pin(Host, "across", S(22), boundary + S(1.5)); // reaches from the recovered part into the new audio
        Assert.Equal(["across", "old"], reopened.DiscardBefore(boundary).Order());
        Assert.Empty(reopened.DiscardBefore(boundary - 5)); // never moves back
        Assert.Equal(["new"], reopened.Pins(Host).Keys);
        var t = reopened.Timeline(Host);
        Assert.Equal([new HistoryRange(boundary, boundary + S(4))], t.Available);
        Assert.Equal(boundary, t.Spans[0].StartFrame);
        Assert.Empty(t.Gaps);
        Assert.Throws<HistoryUnavailableException>(() => reopened.ReadPcm16(Host, boundary - 1, 2));
        Assert.Throws<HistoryUnavailableException>(() => reopened.Pin(Host, "late", boundary - 1, boundary + 1));
        Assert.Equal(HistoryFixture.Expected((int)S(23), (int)S(4)), reopened.ReadPcm16(Host, boundary, (int)S(4)));
        var levels = reopened.Levels(Host, S(20), boundary, 50);
        Assert.All(levels, v => Assert.Equal(0, v));
        Assert.True(reopened.Flush(30000));
        Assert.False(File.Exists(Segment("discard-1", 0)));
        Assert.False(File.Exists(Segment("discard-1", 1)));
        var bytes = File.ReadAllBytes(Segment("discard-1", 2));
        Assert.All(bytes.Take((int)(boundary - S(20)) * 2), b => Assert.Equal(0, b));
        // A crash now: the next launch (or an older companion reading the same index) starts at the boundary.
        reopened.Abandon();
        var again = Open("discard-1", HistoryGapReason.Crash);
        Assert.Equal(boundary, again.Timeline(Host).Available[0].StartFrame);
        Assert.Equal(["new"], again.Pins(Host).Keys);
        Assert.Throws<HistoryUnavailableException>(() => again.ReadPcm16(Host, S(20), 1));
    }

    [Fact]
    public void DiscardingEverythingWhileStoppedLeavesAnEmptyHistoryThatRecordsAgain()
    {
        var store = Create("discard-2");
        HistoryFixture.Feed(store, clock, 15);
        Assert.True(store.Flush(30000));
        store.Abandon();
        var reopened = Open("discard-2", HistoryGapReason.Crash);
        long boundary = reopened.LiveFrame;
        reopened.DiscardBefore(boundary);
        var t = reopened.Timeline(Host);
        Assert.Empty(t.Available); Assert.Empty(t.Spans); Assert.Empty(t.Gaps);
        HistoryFixture.Feed(reopened, clock, 3);
        Assert.Equal([new HistoryRange(boundary, boundary + S(3))], reopened.Timeline(Host).Available);
        Assert.True(reopened.Flush(30000));
        reopened.Abandon();
        var again = Open("discard-2", HistoryGapReason.Crash);
        Assert.Equal([new HistoryRange(boundary, boundary + S(3))], again.Timeline(Host).Available);
    }
}
