using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// Guest scoping on every call: a guest learns no audio, peaks, spans or gap times from before their join
/// point, except in ranges the host shared. Also what happens when history expires in the middle of a read.
/// </summary>
[Collection("History")]
public sealed class HistoryAccessTests : IDisposable
{
    private const int Rate = 16000;
    private const long T0 = HistoryFixture.T0;
    /// <summary>The guest's join point: 40 s in, deliberately not on a 64-frame peak block boundary.</summary>
    private static readonly long Join = S(40) + 37;
    private readonly string root = Path.Combine(Path.GetTempPath(), "soundpad-history-test-" + Guid.NewGuid().ToString("N"));
    private readonly HistoryFixture.Clock clock = new();
    private readonly List<HistoryStore> stores = [];
    public void Dispose()
    {
        foreach (var store in stores) store.Close(true);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private static long S(double seconds) => (long)Math.Round(seconds * Rate);

    /// <summary>Loud before the join point, quiet (a tenth) from it on: anything from before the join stands out in a guest's answer.</summary>
    private static short Marked(long frame) => frame < Join ? HistoryFixture.Pcm(frame) : (short)(HistoryFixture.Pcm(frame) / 10);
    private const int Quiet = 32767 / 10;

    /// <summary>0-30 s recorded at T0, stopped for a minute, 30-60 s recorded from T0 + 90 s. The guest joins at 40 s.</summary>
    private HistoryStore Recorded(HistoryOptions? options = null)
    {
        var store = HistoryStore.Create(root, (options ?? new()) with { Rate = Rate, UtcNowMs = () => clock.Now });
        stores.Add(store);
        HistoryFixture.Feed(store, clock, 30, Marked);
        store.EndRun(HistoryGapReason.Stopped);
        clock.Now += 60000;
        HistoryFixture.Feed(store, clock, 30, Marked);
        return store;
    }

    [Fact]
    public void AGuestLearnsNothingFromBeforeTheirJoinPoint()
    {
        var store = Recorded(); var host = HistoryAccess.Host; var guest = HistoryAccess.Guest(Join);
        long live = store.LiveFrame;
        Assert.True(guest.Allows(Join, Join + 1)); Assert.False(guest.Allows(Join - 1, Join + 1)); Assert.True(host.Allows(0, live));

        // Audio: refused when any part is before the join, whole and equal to the host's from there on.
        var refused = HistoryFixture.Refused(() => store.Read(guest, 0, 100), HistoryUnavailableException.NotShared);
        Assert.Equal(new HistoryRange[] { new(Join, live) }, refused.Available); // even the refusal names nothing earlier
        Assert.Equal(new HistoryRange(Join, live), refused.Window);
        Assert.DoesNotContain("scrolled", refused.Message);
        HistoryFixture.Refused(() => store.Read(guest, Join - 1, 10), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.ReadPcm16(guest, Join - 1, 10), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.ReadPcm16(guest, S(35), 10), HistoryUnavailableException.NotShared);
        Assert.Equal(store.ReadPcm16(host, Join, 5000), store.ReadPcm16(guest, Join, 5000));
        Assert.Equal(store.Read(host, Join, (int)(live - Join)), store.Read(guest, Join, (int)(live - Join)));
        HistoryFixture.Refused(() => store.Read(guest, live - 10, 20), HistoryUnavailableException.NotRecorded);

        // Peaks from the pyramid, at bin sizes whose edges fall anywhere: nothing loud, nothing before the join.
        foreach (int bins in new[] { 1, 7, 500, 2048, 4096 })
        {
            var mine = store.Levels(guest, 0, live, bins); var hosts = store.Levels(host, 0, live, bins);
            for (int b = 0; b < bins; b++)
            {
                long from = live * b / bins, to = live * (b + 1) / bins;
                Assert.InRange(mine[b * 2], -Quiet, Quiet); Assert.InRange(mine[b * 2 + 1], -Quiet, Quiet);
                if (to <= Join)
                {
                    Assert.True(mine[b * 2] == 0 && mine[b * 2 + 1] == 0, $"{bins} bins: bin {b} ends before the join but is not empty");
                    Assert.True(hosts[b * 2] < -Quiet && hosts[b * 2 + 1] > Quiet); // the host does see the loud part there
                }
                else if (from >= Join + 64) Assert.Equal((hosts[b * 2], hosts[b * 2 + 1]), (mine[b * 2], mine[b * 2 + 1]));
            }
        }
        // The peak block that contains the join point mixes frames from both sides, so a guest does not get it at all.
        long block = Join / 64 * 64;
        var edge = store.Levels(guest, block, block + 64 * 8, 8); var hostEdge = store.Levels(host, block, block + 64 * 8, 8);
        Assert.True(edge[0] == 0 && edge[1] == 0 && hostEdge[1] > Quiet);
        Assert.Equal(hostEdge[2..], edge[2..]);
        // Deep zoom works on samples, so the cut is exact to the frame.
        var zoom = store.Levels(guest, Join - 500, Join + 500, 1000);
        for (int b = 0; b < 1000; b++)
        {
            short wanted = b < 500 ? (short)0 : Marked(Join - 500 + b);
            Assert.True(zoom[b * 2] == wanted && zoom[b * 2 + 1] == wanted, $"zoom bin {b}");
        }
        Assert.All(store.Levels(guest, Join - 500, Join + 500, 250), v => Assert.InRange(v, -Quiet, Quiet));

        // Metadata: the timeline starts at the join point. The stop a minute before it is not there.
        var t = store.Timeline(guest);
        Assert.Equal(new HistorySpan(Join, live, store.WallTimeOf(host, Join)), Assert.Single(t.Spans));
        Assert.Equal(T0 + 100002, t.Spans[0].WallStartUtcMs); // 10.0023 s into the second run, which began at T0 + 90 s
        Assert.Empty(t.Gaps);
        Assert.Equal(new HistoryRange[] { new(Join, live) }, t.Available);
        Assert.Equal(live, t.LiveFrame);
        Assert.Equal(T0 + 120000, t.LiveWallUtcMs);
        Assert.Single(store.Timeline(host).Gaps); // the host sees it
        Assert.Equal(2, store.Timeline(host).Spans.Length);

        HistoryFixture.Refused(() => store.WallTimeOf(guest, Join - 1), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.WallTimeOf(guest, S(30)), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.WallTimeOf(guest, 0), HistoryUnavailableException.NotShared);
        Assert.Equal(store.WallTimeOf(host, Join), store.WallTimeOf(guest, Join));
        Assert.Equal(store.WallTimeOf(host, live), store.WallTimeOf(guest, live));
        // Searching by time never lands before the join, whatever time is asked.
        Assert.Equal(Join, store.FrameAtOrAfterWall(guest, 0));
        Assert.Equal(Join, store.FrameAtOrAfterWall(guest, T0));
        Assert.Equal(Join, store.FrameAtOrAfterWall(guest, T0 + 30000)); // inside the hidden gap
        Assert.Equal(Join, store.FrameAtOrAfterWall(guest, T0 + 95000)); // inside the run they joined, before they joined
        Assert.Equal(store.FrameAtOrAfterWall(host, T0 + 110000), store.FrameAtOrAfterWall(guest, T0 + 110000));

        // Protecting audio and storage figures follow the same rule.
        HistoryFixture.Refused(() => store.Pin(guest, "guest-draft", Join - 10, Join + 10), HistoryUnavailableException.NotShared);
        store.Pin(guest, "guest-draft", Join, Join + 100);
        HistoryFixture.Refused(() => store.ReplacePin(guest, "guest-draft", S(5), S(6)), HistoryUnavailableException.NotShared);
        Assert.Equal(new HistoryRange(Join, Join + 100), store.Pins(host)["guest-draft"]);
        HistoryFixture.Refused(() => store.Totals(guest), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.Pins(guest), HistoryUnavailableException.NotShared);

        // Whether hidden audio still exists is not observable: the answer is the same before and after it expires.
        store.SetWindow(60);
        HistoryFixture.Feed(store, clock, 30, Marked); // 0-30 s is gone now, 30-40 s still exists
        HistoryFixture.Refused(() => store.Read(host, 0, 100), HistoryUnavailableException.Expired);
        Assert.Equal(100, store.Read(host, S(35), 100).Length);
        var gone = HistoryFixture.Refused(() => store.Read(guest, 0, 100), HistoryUnavailableException.NotShared);
        var hidden = HistoryFixture.Refused(() => store.Read(guest, S(35), 100), HistoryUnavailableException.NotShared);
        Assert.Equal(gone.Message, hidden.Message); Assert.Equal(gone.Available, hidden.Available);
    }

    [Fact]
    public void AGuestWhoJoinsWhileCaptureIsStoppedDoesNotLearnWhenItStopped()
    {
        var store = Recorded(); var host = HistoryAccess.Host;
        store.EndRun(HistoryGapReason.Stopped); // at T0 + 120 s
        long live = store.LiveFrame;
        clock.Now += 10000; // they join ten seconds into the stop
        var guest = HistoryAccess.Guest(live); var guestWithTime = HistoryAccess.Guest(live, null, clock.Now);

        var t = store.Timeline(guest);
        Assert.Empty(t.Spans); Assert.Empty(t.Gaps); Assert.Empty(t.Available);
        Assert.Null(t.LiveWallUtcMs); // that would be the moment capture stopped
        Assert.False(t.Recording);
        HistoryFixture.Refused(() => store.WallTimeOf(guest, live), HistoryUnavailableException.NotRecorded);
        HistoryFixture.Refused(() => store.WallTimeOf(guest, live - 1), HistoryUnavailableException.NotShared);
        Assert.Equal(live, store.FrameAtOrAfterWall(guest, 0));
        Assert.Equal(T0 + 120000, store.WallTimeOf(host, live)); // the host knows
        // Told when they joined, the store reports the stop from that moment on.
        Assert.Equal(new HistoryGap(live, T0 + 130000, null, "stopped"), Assert.Single(store.Timeline(guestWithTime).Gaps));

        clock.Now += 20000;
        HistoryFixture.Feed(store, clock, 30, Marked);
        Assert.Equal(new HistorySpan(live, live + S(30), T0 + 150000), Assert.Single(store.Timeline(guest).Spans));
        Assert.Empty(store.Timeline(guest).Gaps);
        Assert.Equal(new HistoryGap(live, T0 + 130000, T0 + 150000, "stopped"), Assert.Single(store.Timeline(guestWithTime).Gaps));
        Assert.Equal(new HistoryGap(live, T0 + 120000, T0 + 150000, "stopped"), store.Timeline(host).Gaps[^1]);
        // Someone who joined earlier was there for that stop and sees it whole, but still not the one before their time.
        Assert.Equal(new HistoryGap(live, T0 + 120000, T0 + 150000, "stopped"), Assert.Single(store.Timeline(HistoryAccess.Guest(Join)).Gaps));
    }

    [Fact]
    public void SharedRangesOpenExactlyTheSharedAudioAndNothingAroundIt()
    {
        var store = Recorded(); var host = HistoryAccess.Host;
        long live = store.LiveFrame;
        var guest = HistoryAccess.Guest(Join, [new(S(10), S(20))]);

        Assert.Equal(HistoryFixture.Expected(S(10), (int)S(10), Marked), store.ReadPcm16(guest, S(10), (int)S(10)));
        HistoryFixture.Refused(() => store.Read(guest, S(10) - 1, 10), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.Read(guest, S(20) - 5, 10), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.Read(guest, S(25), 10), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.Read(guest, S(15), (int)S(30)), HistoryUnavailableException.NotShared); // shared, hidden, joined: not one range

        var t = store.Timeline(guest);
        Assert.Equal(new HistorySpan[] { new(S(10), S(20), T0 + 10000), new(Join, live, T0 + 100002) }, t.Spans);
        Assert.Equal(new HistoryRange[] { new(S(10), S(20)), new(Join, live) }, t.Available);
        Assert.Empty(t.Gaps); // the stop at 30 s lies between the two, in what was not shared
        Assert.Equal(T0 + 10000, store.WallTimeOf(guest, S(10)));
        Assert.Equal(T0 + 20000, store.WallTimeOf(guest, S(20))); // where the shared audio ends
        HistoryFixture.Refused(() => store.WallTimeOf(guest, S(10) - 1), HistoryUnavailableException.NotShared);
        HistoryFixture.Refused(() => store.WallTimeOf(guest, S(25)), HistoryUnavailableException.NotShared);
        Assert.Equal(S(10), store.FrameAtOrAfterWall(guest, T0));
        Assert.Equal(S(15), store.FrameAtOrAfterWall(guest, T0 + 15000));
        Assert.Equal(Join, store.FrameAtOrAfterWall(guest, T0 + 25000));

        var mine = store.Levels(guest, 0, live, 600); var hosts = store.Levels(host, 0, live, 600); // a tenth of a second per bin
        for (int b = 0; b < 600; b++)
        {
            bool shared = b is >= 100 and < 200;
            if (shared) Assert.True(mine[b * 2] == hosts[b * 2] && mine[b * 2 + 1] == hosts[b * 2 + 1] && mine[b * 2 + 1] > Quiet, $"shared bin {b}");
            else if (b < 400) Assert.True(mine[b * 2] == 0 && mine[b * 2 + 1] == 0, $"hidden bin {b}");
            else { Assert.InRange(mine[b * 2], -Quiet, Quiet); Assert.InRange(mine[b * 2 + 1], -Quiet, Quiet); }
        }
        store.Pin(guest, "from-shared", S(12), S(13));
        HistoryFixture.Refused(() => store.Pin(guest, "from-hidden", S(22), S(23)), HistoryUnavailableException.NotShared);

        // A gap on the edge of a shared range carries times from the hidden side; one inside it is theirs to see.
        Assert.Empty(store.Timeline(HistoryAccess.Guest(Join, [new(S(20), S(30))])).Gaps);
        Assert.Empty(store.Timeline(HistoryAccess.Guest(Join, [new(S(30), S(35))])).Gaps);
        var across = store.Timeline(HistoryAccess.Guest(Join, [new(S(25), S(35))]));
        Assert.Equal(new HistoryGap(S(30), T0 + 30000, T0 + 90000, "stopped"), Assert.Single(across.Gaps));
        Assert.Equal(new HistorySpan[] { new(S(25), S(30), T0 + 25000), new(S(30), S(35), T0 + 90000), new(Join, live, T0 + 100002) }, across.Spans);

        // A range shared right up to the join point reads as one piece with what follows.
        var joined = HistoryAccess.Guest(Join, [new(Join - 1000, Join)]);
        Assert.Equal(HistoryFixture.Expected(Join - 500, 1000, Marked), store.ReadPcm16(joined, Join - 500, 1000));
        HistoryFixture.Refused(() => store.Read(joined, Join - 1001, 10), HistoryUnavailableException.NotShared);

        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryAccess.Guest(-1));
        Assert.Throws<ArgumentException>(() => HistoryAccess.Guest(0, [new(5, 5)]));
        Assert.Throws<ArgumentException>(() => HistoryAccess.Guest(0, Enumerable.Range(0, 65).Select(i => new HistoryRange(i * 10, i * 10 + 5))));
    }

    [Fact]
    public void AReadThatRacesWithExpiryIsCompleteOrRefusedNeverTorn()
    {
        var files = new TestFiles();
        var store = HistoryStore.Create(root, new HistoryOptions { Rate = Rate, WindowSeconds = 60, Files = files, UtcNowMs = () => clock.Now });
        stores.Add(store); var host = HistoryAccess.Host;
        HistoryFixture.Feed(store, clock, 70);
        Assert.True(store.Flush(30000)); // held: 10-70 s, all of it on disk except the newest segment's memory copy
        string? trigger = null; int more = 0;
        void Expire(string name)
        {
            if (name != trigger) return;
            trigger = null;
            HistoryFixture.Feed(store, clock, more); Assert.True(store.Flush(30000)); // the window moves on and the files are deleted
        }

        // Expiry lands after the range was checked but before its first segment is read.
        files.BeforeRead = Expire; trigger = "seg-00000001.pcm"; more = 20;
        var early = HistoryFixture.Refused(() => store.ReadPcm16(host, S(10), (int)S(15)), HistoryUnavailableException.Expired);
        Assert.Null(trigger);
        Assert.Equal(new HistoryRange(S(30), S(90)), early.Window); // it says what would work now

        // Expiry lands between the two segments of one read: the half already copied is not returned.
        files.BeforeRead = null; files.AfterRead = Expire; trigger = "seg-00000003.pcm"; more = 20;
        HistoryFixture.Refused(() => store.ReadPcm16(host, S(35), (int)S(10)), HistoryUnavailableException.Expired);
        Assert.Null(trigger);

        // Expiry lands just after the last byte was read: the caller gets the complete, correct audio one last time.
        trigger = "seg-00000006.pcm"; more = 30;
        Assert.Equal(HistoryFixture.Expected(S(62), (int)S(5)), store.ReadPcm16(host, S(62), (int)S(5)));
        Assert.Null(trigger);
        HistoryFixture.Refused(() => store.ReadPcm16(host, S(62), (int)S(5)), HistoryUnavailableException.Expired);

        // The same race under a deep-zoom waveform: the bins are empty, as if it had been asked a moment later.
        files.AfterRead = null; files.BeforeRead = Expire; trigger = "seg-00000008.pcm"; more = 20;
        Assert.All(store.Levels(host, S(85), S(85) + 2000, 500), v => Assert.Equal(0, v));
        Assert.Null(trigger);
        files.BeforeRead = null;
        Assert.Equal(HistoryFixture.Expected(S(100), (int)S(60)), store.ReadPcm16(host, S(100), (int)S(60)));
    }

    [Fact]
    public async Task ReadersNeverSeeTornAudioWhileRecordingRollsPastThem()
    {
        var store = HistoryStore.Create(root, new HistoryOptions { Rate = Rate, WindowSeconds = 60, UtcNowMs = () => clock.Now });
        stores.Add(store); var host = HistoryAccess.Host;
        HistoryFixture.Feed(store, clock, 60);
        using var stop = new CancellationTokenSource();
        long complete = 0, refused = 0; string? failure = null;
        // One reader chases the oldest edge (where expiry happens), one the newest (still in memory, being appended to).
        var readers = Enumerable.Range(0, 2).Select(which => Task.Run(() =>
        {
            var random = new Random(which);
            while (!stop.IsCancellationRequested && failure is null)
            {
                try
                {
                    var t = store.Timeline(host);
                    int count = random.Next(1, 12 * Rate);
                    long start = which == 0 ? t.Available[0].StartFrame + random.Next(0, 2 * Rate) : Math.Max(0, t.LiveFrame - count - random.Next(0, Rate));
                    var audio = store.ReadPcm16(host, start, count);
                    for (int i = 0; i < audio.Length; i++)
                        if (audio[i] != HistoryFixture.Pcm(start + i)) { failure = $"frame {start + i} read as {audio[i]}"; break; }
                    store.Levels(host, t.Available[0].StartFrame, t.LiveFrame, 512);
                    Interlocked.Increment(ref complete);
                }
                catch (HistoryUnavailableException) { Interlocked.Increment(ref refused); }
                catch (Exception ex) { failure = ex.ToString(); }
            }
        })).ToArray();
        HistoryFixture.Feed(store, clock, 240); // recording never pauses for them
        stop.Cancel();
        await Task.WhenAll(readers);
        Assert.Null(failure);
        Assert.True(complete > 0, $"No read completed ({refused} refused).");
        Assert.Equal(S(300), store.LiveFrame);
        Assert.Equal(0, store.Totals(host).Overruns);
    }
}
