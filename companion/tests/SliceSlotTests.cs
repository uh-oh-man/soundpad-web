using System.Security.Cryptography;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class SliceSlotPoolTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-slice-slot-test-" + Guid.NewGuid().ToString("N"));
    private long now = 1000;
    public SliceSlotPoolTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    private SlotPool Pool(int count = 4, SlotPoolOptions? options = null) => new(folder, count, (options ?? new()) with { Clock = () => now });
    private static byte[] Slice(int frames, int from = 0) => SliceRenderer.Render(SliceFixture.Mono(48000, 30000), from, from + frames);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task EnsureFilesMakesPlayableSilentSlotsAndKeepsWhatIsThere()
    {
        var pool = Pool();
        Assert.Equal(Path.Combine(folder, "slot-3.wav"), pool.PathOf(3));
        var slots = await pool.EnsureFiles();
        Assert.Equal(new[] { "slot-1.wav", "slot-2.wav", "slot-3.wav", "slot-4.wav" }, Directory.GetFiles(folder).Select(file => Path.GetFileName(file)).Order().ToArray());
        foreach (var slot in slots)
        {
            var bytes = File.ReadAllBytes(slot.Path);
            Assert.True(SliceWav.IsDigitalSilence(bytes));
            Assert.Equal(4800 * 2, SliceFixture.Samples(bytes, out int rate, out int channels).Length); // NAudio reads it: 100 ms, stereo
            Assert.Equal((48000, 2), (rate, channels));
            Assert.Equal((Hash(bytes), 100L, 4800L, (long)bytes.Length, false), (slot.Hash, slot.DurationMs, slot.Frames, slot.Bytes, slot.InUse));
            Assert.True(pool.Verify(slot.Index));
        }
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, slots.Select(s => s.Generation).ToArray());

        // Running it again changes nothing, and a new pool adopts what a previous run left in the files.
        var lease = pool.Acquire()!; var slice = Slice(1234);
        Assert.True((await pool.Write(lease, slice)).Ok);
        Assert.Equal(slots.Select(s => s.Generation).Max() + 1, (await pool.EnsureFiles())[lease.Index - 1].Generation);
        var later = Pool(); var adopted = await later.EnsureFiles();
        Assert.Equal(slice, File.ReadAllBytes(lease.Path));
        Assert.Equal((Hash(slice), 1234L, 26L), (adopted[lease.Index - 1].Hash, adopted[lease.Index - 1].Frames, adopted[lease.Index - 1].DurationMs)); // 25.7 ms
        Assert.True(later.Verify(lease.Index));

        // A missing slot comes back, and one that is not a WAV any more is made playable again.
        File.Delete(pool.PathOf(2)); File.WriteAllBytes(pool.PathOf(3), [1, 2, 3]);
        await later.EnsureFiles();
        Assert.True(SliceWav.IsDigitalSilence(File.ReadAllBytes(pool.PathOf(2)))); Assert.True(SliceWav.IsDigitalSilence(File.ReadAllBytes(pool.PathOf(3))));

        Assert.True(SlotPool.IsSlotFile(pool.PathOf(1))); Assert.True(SlotPool.IsSlotFile(@"C:\x\SLOT-12.WAV"));
        Assert.False(SlotPool.IsSlotFile(@"C:\x\slot-.wav")); Assert.False(SlotPool.IsSlotFile(@"C:\x\slot-1.mp3")); Assert.False(SlotPool.IsSlotFile(@"C:\x\Airhorn.wav")); Assert.False(SlotPool.IsSlotFile(@"C:\x\slot-1a.wav"));
        Assert.Throws<ArgumentException>(() => new SlotPool("relative")); Assert.Throws<ArgumentException>(() => new SlotPool(@"\\server\share"));
    }

    [Fact]
    public async Task AcquireRotatesThroughTheLeastRecentlyUsedFreeSlot()
    {
        var pool = Pool(); await pool.EnsureFiles();
        // Released at once each time: the pool still moves on, and never hands back the slot it handed out last.
        var order = new List<int>();
        for (int i = 0; i < 9; i++) { var lease = pool.Acquire()!; order.Add(lease.Index); pool.Release(lease); }
        Assert.Equal(new[] { 1, 2, 3, 4, 1, 2, 3, 4, 1 }, order.ToArray());

        // Slots in use are skipped. After the loop slot 1 was handed out last and 2 is the least recently used.
        var a = pool.Acquire()!; var b = pool.Acquire()!; var c = pool.Acquire()!;
        Assert.Equal((2, 3, 4), (a.Index, b.Index, c.Index));
        Assert.True(pool.Info(3).InUse); Assert.False(pool.Info(1).InUse);
        var d = pool.Acquire()!; Assert.Equal(1, d.Index);
        Assert.Null(pool.Acquire());                       // everything is in use
        pool.Release(d); Assert.Null(pool.Acquire());      // free, but it was the last one handed out
        pool.Release(b);
        Assert.Equal(3, pool.Acquire()!.Index);
        pool.Release(a); pool.Release(c);
        Assert.Equal(4, pool.Acquire(exclude: [2])!.Index); // the caller can rule slots out (say, one whose write just failed)
        Assert.Equal(2, pool.Acquire()!.Index);

        // One slot cannot avoid itself, so it only waits for its release.
        var single = Pool(1); var only = single.Acquire()!;
        Assert.Null(single.Acquire()); single.Release(only); Assert.Equal(1, single.Acquire()!.Index);
    }

    [Fact]
    public async Task ASlotNobodyReleasedIsFreeAgainAfterTheMaximumHoldAndItsOldLeaseCannotWrite()
    {
        var pool = Pool(2, new SlotPoolOptions { MaxHoldMs = 5000 }); await pool.EnsureFiles();
        var lost = pool.Acquire()!; var other = pool.Acquire()!;
        Assert.Equal((1, 2), (lost.Index, other.Index));
        now += 4999; Assert.Null(pool.Acquire());
        now += 1;                                           // slot 1 has been held for the maximum time
        var next = pool.Acquire()!;
        Assert.Equal(1, next.Index); Assert.NotEqual(lost.LeaseId, next.LeaseId);

        // The newer request's audio goes in; the lost caller's late render is refused without touching the file.
        var wanted = Slice(500, 100);
        Assert.True((await pool.Write(next, wanted)).Ok);
        var late = await pool.Write(lost, Slice(700, 9000));
        Assert.False(late.Ok); Assert.True(late.Stale); Assert.Equal(0, late.Attempts);
        Assert.Equal(wanted, File.ReadAllBytes(next.Path)); Assert.Equal(Hash(wanted), pool.Info(1).Hash);
        pool.Release(lost); Assert.True(pool.Info(1).InUse); // an outdated lease cannot free the slot either

        // A released lease cannot write.
        pool.Release(next);
        Assert.True((await pool.Write(next, Slice(10))).Stale);
        Assert.Equal(wanted, File.ReadAllBytes(next.Path));
    }

    [Fact]
    public async Task WritesReplaceTheContentsOfTheSameFileInPlace()
    {
        var pool = Pool(); await pool.EnsureFiles();
        var lease = pool.Acquire()!;
        var created = File.GetCreationTimeUtc(lease.Path);
        // A reader that allows writers, opened before the write: if the file were deleted or renamed and a new one
        // put at the path, this handle would still show the old file.
        using var reader = new FileStream(lease.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        byte[] Seen() { var bytes = new byte[RandomAccess.GetLength(reader.SafeFileHandle)]; RandomAccess.Read(reader.SafeFileHandle, bytes, 0); return bytes; }

        var longer = Slice(20000);
        var first = await pool.Write(lease, longer);
        Assert.True(first.Ok); Assert.Equal(1, first.Attempts); Assert.Null(first.Error);
        Assert.Equal(longer, Seen()); Assert.Equal(longer, File.ReadAllBytes(lease.Path));

        var shorter = Slice(300, 5000); // shorter than what was there: nothing of the old audio may be left behind it
        Assert.True((await pool.Write(lease, shorter)).Ok);
        Assert.Equal(shorter, Seen()); Assert.Equal(shorter, File.ReadAllBytes(lease.Path));
        var samples = SliceFixture.Samples(File.ReadAllBytes(lease.Path), out _, out _);
        Assert.Equal(300, samples.Length); Assert.Equal(SliceFixture.Low(5000), samples[0]); Assert.Equal(SliceFixture.Low(5299), samples[^1]);

        Assert.Equal(created, File.GetCreationTimeUtc(lease.Path));
        Assert.Equal(4, Directory.GetFileSystemEntries(folder).Length); // no temporary or renamed files next to the slots
        await Assert.ThrowsAsync<ArgumentException>(() => pool.Write(lease, [1, 2, 3])); // only WAVs go into a slot
        Assert.Equal(shorter, File.ReadAllBytes(lease.Path));
    }

    [Fact]
    public async Task AFileThePlayerStillHoldsIsRetriedThenReportedSoAnotherSlotCanBeUsed()
    {
        var pool = Pool(4, new SlotPoolOptions { WriteAttempts = 3, RetryDelayMs = 5 }); await pool.EnsureFiles();
        var lease = pool.Acquire()!; var slice = Slice(2000);
        var before = File.ReadAllBytes(lease.Path); var record = pool.Info(lease.Index);
        using (new FileStream(lease.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) // a player that does not allow writers
        {
            var blocked = await pool.Write(lease, slice);
            Assert.False(blocked.Ok); Assert.False(blocked.Stale); Assert.Equal(3, blocked.Attempts); Assert.NotNull(blocked.Error);
            Assert.Equal(record, blocked.Slot); // still what it was: same generation, hash and length
            // The caller's way out: give the slot back and take another.
            pool.Release(lease);
            var other = pool.Acquire()!;
            Assert.NotEqual(lease.Index, other.Index);
            var written = await pool.Write(other, slice);
            Assert.True(written.Ok); Assert.Equal(1, written.Attempts);
        }
        Assert.Equal(before, File.ReadAllBytes(lease.Path)); Assert.True(pool.Verify(lease.Index));

        // The holder lets go while the write is still retrying: it goes through.
        var patient = Pool(4, new SlotPoolOptions { WriteAttempts = 9, RetryDelayMs = 20 }); await patient.EnsureFiles();
        var held = patient.Acquire()!;
        var holder = new FileStream(held.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pending = patient.Write(held, slice); // the first attempt has already failed when this returns
        Assert.False(pending.IsCompleted);
        await Task.Delay(50); holder.Dispose();
        var result = await pending;
        Assert.True(result.Ok); Assert.InRange(result.Attempts, 2, 9);
        Assert.Equal(slice, File.ReadAllBytes(held.Path));
    }

    [Fact]
    public async Task EachSlotRecordsWhatItHoldsUnderARisingGeneration()
    {
        var pool = Pool(); await pool.EnsureFiles(); // generations 1 to 4
        var first = pool.Acquire()!; var second = pool.Acquire()!;
        var a = Slice(4800, 0); var b = Slice(9600, 1000); var c = Slice(480, 2000);
        var one = (await pool.Write(first, a)).Slot; var two = (await pool.Write(second, b)).Slot; var three = (await pool.Write(first, c)).Slot;
        Assert.Equal(new[] { 5L, 6L, 7L }, new[] { one.Generation, two.Generation, three.Generation }); // one counter across all slots
        Assert.Equal((Hash(a), 100L, 4800L, 48000, 1, (long)a.Length), (one.Hash, one.DurationMs, one.Frames, one.SampleRate, one.Channels, one.Bytes));
        Assert.Equal((Hash(b), 200L, 9600L), (two.Hash, two.DurationMs, two.Frames));
        Assert.Equal((Hash(c), 10L, 480L), (three.Hash, three.DurationMs, three.Frames));
        Assert.Equal(three, pool.Info(first.Index)); Assert.Equal(two, pool.Slots[second.Index - 1]);
        Assert.True(pool.Verify(first.Index)); Assert.True(pool.Verify(second.Index));
        Assert.Equal(3L, pool.Info(3).Generation); // untouched slots keep theirs

        // Writing the same bytes again still counts as a new generation: the caller asked for a new play.
        Assert.Equal(8L, (await pool.Write(first, c)).Slot.Generation);

        // Something else changed the file: the record no longer matches what is on disk, and Verify says so.
        File.WriteAllBytes(first.Path, a);
        Assert.False(pool.Verify(first.Index)); Assert.True(pool.Verify(second.Index));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Info(5));
    }
}

public sealed class SliceSlotProbeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-slice-probe-test-" + Guid.NewGuid().ToString("N"));
    public SliceSlotProbeTests() => Directory.CreateDirectory(folder);
    public void Dispose() { player.Held?.Dispose(); Directory.Delete(folder, true); }

    private static readonly SlotProbeOptions Fast = new() { PollMs = 1, StopTimeoutMs = 2000, WriteAttempts = 2, RetryDelayMs = 1 };
    private readonly FakePlayer player = new();
    private string Slot => Path.Combine(folder, "slot-1.wav");

    /// <summary>
    /// A stand-in for the player's remote control. It never makes a sound; it only looks at the file the way a
    /// player would: either every time it is told to play, or once per path.
    /// </summary>
    private sealed class FakePlayer
    {
        private readonly Dictionary<string, long> remembered = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Diagnostics.Stopwatch playing = new();
        private long playingForMs;
        private int playingPolls;
        /// <summary>RealTime: busy for as long as the audio it loaded lasts, instead of for three looks.</summary>
        public bool Rereads = true, HoldsFile, ReportsDuration = true, RealTime;
        public string Idle = "STOPPED";
        public int Plays, StatusReads;
        public long? Duration;
        public FileStream? Held;
        public readonly List<byte[]> Heard = [];
        public Task Play(string path)
        {
            Plays++;
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[file.Length]; file.ReadExactly(bytes); }
            Heard.Add(bytes);
            Assert.True(SliceWav.TryRead(bytes, out var info));
            long ms = (long)Math.Round(info.DurationMs);
            if (!Rereads) { if (!remembered.TryGetValue(path, out long first)) remembered[path] = first = ms; ms = first; }
            Duration = ReportsDuration ? ms : null;
            playingPolls = 3; playingForMs = ms; playing.Restart();
            if (HoldsFile) Held ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        }
        public Task<long?> ReadDuration() => Task.FromResult(Duration);
        public Task<string> ReadStatus()
        {
            StatusReads++;
            if (RealTime) return Task.FromResult(playing.IsRunning && playing.ElapsedMilliseconds < playingForMs ? "PLAYING" : Idle);
            return Task.FromResult(playingPolls-- > 0 ? "PLAYING" : Idle);
        }
    }

    private Task<SlotProbeResult> Run(string? path = null) => SlotProbe.Run(player.Play, player.ReadDuration, player.ReadStatus, path ?? Slot, Fast);

    [Fact]
    public async Task APlayerThatReadsTheFileOnEveryPlayIsReportedAsRereading()
    {
        File.WriteAllBytes(Slot, SliceWav.Silence(4800, 48000, 2));
        var result = await Run();
        Assert.Equal(SlotProbeOutcome.Reread, result.Outcome); Assert.True(result.Reread);
        Assert.Equal((400L, 900L, 400L, 900L), (result.FirstWrittenMs, result.SecondWrittenMs, result.FirstReportedMs, result.SecondReportedMs));
        Assert.Equal((1, 1, "STOPPED"), (result.FirstWriteAttempts, result.SecondWriteAttempts, result.StatusBefore));
        Assert.Equal(2, player.Plays);
        // Everything that was ever in the file while it played was pure digital silence, of the two lengths.
        Assert.All(player.Heard, bytes => Assert.True(SliceWav.IsDigitalSilence(bytes)));
        Assert.Equal(new[] { 19200L, 43200L }, player.Heard.Select(bytes => { SliceWav.TryRead(bytes, out var info); return info.Frames; }).ToArray());
        Assert.True(SliceWav.IsDigitalSilence(File.ReadAllBytes(Slot)));
        Assert.Equal(new[] { "slot-1.wav" }, Directory.GetFiles(folder).Select(file => Path.GetFileName(file)).ToArray());
    }

    [Fact]
    public async Task APlayerThatKeepsItsFirstReadIsReportedAsNotRereading()
    {
        player.Rereads = false;
        var result = await Run(); // the slot file did not exist yet; the probe makes it
        Assert.Equal(SlotProbeOutcome.NotReread, result.Outcome); Assert.False(result.Reread);
        Assert.Equal((400L, 400L), (result.FirstReportedMs, result.SecondReportedMs));
        Assert.Equal(2, player.Plays);
        Assert.All(player.Heard, bytes => Assert.True(SliceWav.IsDigitalSilence(bytes)));
    }

    [Theory]
    [InlineData("PLAYING")]
    [InlineData("PAUSED")]
    [InlineData("")]
    public async Task ItRefusesToRunUnlessThePlayerIsStopped(string status)
    {
        var existing = SliceRenderer.Render(SliceFixture.Mono(48000, 1000), 0, 1000);
        File.WriteAllBytes(Slot, existing);
        player.Idle = status;
        var result = await Run();
        Assert.Equal(SlotProbeOutcome.Refused, result.Outcome); Assert.Equal(status, result.StatusBefore);
        Assert.Equal(0, player.Plays); Assert.Equal(1, player.StatusReads);
        Assert.Equal(existing, File.ReadAllBytes(Slot)); // nothing was written
    }

    [Fact]
    public async Task ItOnlyEverWritesToASlotFile()
    {
        var sound = Path.Combine(folder, "Airhorn.wav"); var audio = SliceRenderer.Render(SliceFixture.Mono(48000, 1000), 0, 1000);
        File.WriteAllBytes(sound, audio);
        var result = await Run(sound);
        Assert.Equal(SlotProbeOutcome.Refused, result.Outcome);
        Assert.Equal(audio, File.ReadAllBytes(sound)); Assert.Equal(0, player.Plays); Assert.Equal(0, player.StatusReads);
        Assert.Equal(SlotProbeOutcome.Refused, (await Run("slot-1.wav")).Outcome); // not a full path
        Assert.Equal("stopped", (await SlotProbe.Run(player.Play, player.ReadDuration, () => Task.FromResult(" stopped\r\n"), Slot, Fast with { FirstMs = 40, SecondMs = 200, ToleranceMs = 20 })).StatusBefore!.Trim()); // status text is matched loosely
        await Assert.ThrowsAsync<ArgumentException>(() => SlotProbe.Run(player.Play, player.ReadDuration, player.ReadStatus, Slot, Fast with { SecondMs = 450 }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HowLongThePlayerStayedBusyIsRecordedAsASecondSign(bool rereads)
    {
        // The duration a player reports could come from what it remembers about the entry; how long it actually
        // stays busy cannot. Here the stand-in is busy for exactly as long as the audio it loaded.
        player.Rereads = rereads; player.RealTime = true;
        var result = await SlotProbe.Run(player.Play, player.ReadDuration, player.ReadStatus, Slot, Fast with { FirstMs = 60, SecondMs = 600, ToleranceMs = 20 });
        Assert.Equal(rereads ? SlotProbeOutcome.Reread : SlotProbeOutcome.NotReread, result.Outcome);
        Assert.InRange(result.FirstPlayedMs!.Value, 40, 500);
        if (rereads) Assert.InRange(result.SecondPlayedMs!.Value, 580, 5000); // the full 600 ms of the new file
        else Assert.InRange(result.SecondPlayedMs!.Value, 40, 500);           // still the 60 ms it remembered
        Assert.Contains("Busy for", result.Detail);
    }

    [Fact]
    public async Task APlayerThatKeepsTheFileOpenShowsUpAsABlockedWrite()
    {
        player.HoldsFile = true;
        var result = await Run();
        Assert.Equal(SlotProbeOutcome.WriteBlocked, result.Outcome); Assert.False(result.Reread);
        Assert.Equal((1, 2), (result.FirstWriteAttempts, result.SecondWriteAttempts)); Assert.Equal(400L, result.FirstReportedMs); Assert.Null(result.SecondReportedMs);
        Assert.Equal(1, player.Plays);
        player.Held!.Dispose(); player.Held = null;
        Assert.True(SliceWav.TryRead(File.ReadAllBytes(Slot), out var info)); Assert.Equal(19200, info.Frames); // the first silence is still whole
    }

    [Fact]
    public async Task DurationsThatProveNothingAreInconclusive()
    {
        player.ReportsDuration = false;
        var result = await Run();
        Assert.Equal(SlotProbeOutcome.Inconclusive, result.Outcome); Assert.False(result.Reread);
        Assert.Null(result.FirstReportedMs); Assert.Equal(2, player.Plays);

        // A player that never returns to STOPPED after playing: the probe stops after the first play.
        var stuck = new FakePlayer(); int reads = 0;
        var never = await SlotProbe.Run(stuck.Play, stuck.ReadDuration, () => Task.FromResult(reads++ == 0 ? "STOPPED" : "PLAYING"), Slot, Fast with { StopTimeoutMs = 20, FirstMs = 30, SecondMs = 300 });
        Assert.Equal(SlotProbeOutcome.Inconclusive, never.Outcome); Assert.Equal(1, stuck.Plays); Assert.Contains("STOPPED", never.Detail);
    }
}
