using NAudio.Wave;
using Xunit;
namespace SoundpadWeb.Tests;

public sealed class SlicePcmCacheTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-slice-cache-test-" + Guid.NewGuid().ToString("N"));
    public SlicePcmCacheTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    /// <summary>A mono 16-bit WAV whose frame f holds Low(f + shift). 4,800 frames decode to 19,200 bytes of floats.</summary>
    private string Wave(string name, int frames = 4800, int shift = 0, int rate = 48000)
    {
        var path = Path.Combine(folder, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        var bytes = new byte[frames * 2];
        for (int f = 0; f < frames; f++) BitConverter.TryWriteBytes(bytes.AsSpan(f * 2), SliceFixture.Low(f + shift));
        writer.Write(bytes, 0, bytes.Length);
        return path;
    }

    private static float First(ISliceSource source) { var frame = new float[source.Channels]; Assert.Equal(1, source.Read(0, frame)); return frame[0]; }

    [Fact]
    public async Task DecodesEveryKindOfWavToExactFloatsAtItsOwnRateAndChannels()
    {
        using var cache = new PcmCache();
        // 16-bit stereo: every frame comes back exactly, so a slice of it is bit-exact end to end.
        var stereo = Path.Combine(folder, "stereo.wav");
        using (var writer = new WaveFileWriter(stereo, new WaveFormat(44100, 16, 2)))
        {
            var bytes = new byte[3000 * 4];
            for (int f = 0; f < 3000; f++) { BitConverter.TryWriteBytes(bytes.AsSpan(f * 4), SliceFixture.Low(f)); BitConverter.TryWriteBytes(bytes.AsSpan(f * 4 + 2), SliceFixture.High(f)); }
            writer.Write(bytes, 0, bytes.Length);
        }
        var source = await cache.Get(stereo);
        Assert.Equal((44100, 2, 3000L), (source.SampleRate, source.Channels, source.FrameCount));
        var samples = SliceFixture.Samples(SliceRenderer.Render(source, 1234, 1300), out _, out _);
        for (int i = 0; i < 66; i++) { Assert.Equal(SliceFixture.Low(1234 + i), samples[i * 2]); Assert.Equal(SliceFixture.High(1234 + i), samples[i * 2 + 1]); }

        // 32-bit float and 24-bit files go through the same reader.
        var floats = Path.Combine(folder, "float.wav"); var values = Enumerable.Range(0, 2000).Select(i => (i - 1000) / 1024f).ToArray();
        using (var writer = new WaveFileWriter(floats, WaveFormat.CreateIeeeFloatWaveFormat(22050, 1))) writer.WriteSamples(values, 0, values.Length);
        var decoded = await cache.Get(floats);
        Assert.Equal((22050, 1, 2000L), (decoded.SampleRate, decoded.Channels, decoded.FrameCount));
        var all = new float[2000]; Assert.Equal(2000, decoded.Read(0, all)); Assert.Equal(values, all);
        var deep = Path.Combine(folder, "deep.wav");
        using (var writer = new WaveFileWriter(deep, new WaveFormat(96000, 24, 2))) for (int i = 0; i < 1000; i++) writer.WriteSample(i % 2 == 0 ? 0.25f : -0.5f);
        var wide = await cache.Get(deep);
        Assert.Equal((96000, 2, 500L), (wide.SampleRate, wide.Channels, wide.FrameCount));
        var pair = new float[2]; Assert.Equal(1, wide.Read(499, pair)); Assert.Equal(0.25f, pair[0], 4); Assert.Equal(-0.5f, pair[1], 4);

        // Reads past the end are short, never invented.
        Assert.Equal(0, wide.Read(500, pair)); Assert.Equal(0, wide.Read(-1, pair));
        Assert.Equal(3, cache.Count); Assert.Equal(3, cache.DecodeCount);
        Assert.Equal((3000 * 2 + 2000 + 1000) * 4L, cache.Bytes);
    }

    [Fact]
    public async Task DecodesCompressedAudioThroughMediaFoundation()
    {
        // The same decoder as waveforms and reverse use. No audio device is opened.
        var wav = Path.Combine(folder, "tone.wav"); var mp3 = Path.Combine(folder, "tone.mp3");
        using (var writer = new WaveFileWriter(wav, new WaveFormat(48000, 16, 2))) foreach (var sample in SliceFixture.Tone(48000, 440, 0.5, 48000)) { writer.WriteSample(sample); writer.WriteSample(sample); }
        await Task.Run(() => { using var reader = new WaveFileReader(wav); MediaFoundationEncoder.EncodeToMp3(reader, mp3, 128000); });
        using var cache = new PcmCache();
        var source = await cache.Get(mp3);
        Assert.Equal((48000, 2), (source.SampleRate, source.Channels));
        Assert.InRange(source.FrameCount, 43000, 63000); // MP3 pads the ends
        var middle = new float[24000 * 2]; Assert.Equal(24000, source.Read(12000, middle));
        var left = Enumerable.Range(0, 24000).Select(i => middle[i * 2]).ToArray();
        Assert.InRange(SliceFixture.Hertz(left, 48000), 435, 445);
    }

    [Fact]
    public async Task TwoRequestsForOneFileDecodeOnceAndOffTheCallersThread()
    {
        var path = Wave("a.wav");
        using var release = new ManualResetEventSlim(); using var started = new ManualResetEventSlim();
        int decodeThread = 0, callerThread = Environment.CurrentManagedThreadId;
        using var cache = new PcmCache(1 << 20, (file, max, cancel) => { decodeThread = Environment.CurrentManagedThreadId; started.Set(); release.Wait(cancel); return PcmCache.Decode(file, max, cancel); });
        var first = cache.Get(path); var second = cache.Get(Path.Combine(folder, ".", "A.WAV")); // the same file, spelled differently
        Assert.True(started.Wait(Patience));
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); // the callers got their tasks back while the decode is still running
        Assert.NotEqual(callerThread, decodeThread);
        release.Set();
        var a = await first; var b = await second;
        Assert.Same(a, b); Assert.Equal(1, cache.DecodeCount);
        Assert.Same(a, await cache.Get(path)); Assert.Equal(1, cache.DecodeCount); Assert.True(cache.Contains(path));
        Assert.Equal(SliceFixture.Low(0) / 32768f, First(a));

        // With the real decoder and a burst of requests: still one decode.
        using var real = new PcmCache();
        var burst = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => real.Get(path))));
        Assert.All(burst, source => Assert.Same(burst[0], source)); Assert.Equal(1, real.DecodeCount);
    }

    [Fact]
    public async Task CancellingOneCallerLeavesTheRestAndCancellingAllStopsTheDecode()
    {
        var path = Wave("a.wav");
        using var release = new ManualResetEventSlim(); using var started = new SemaphoreSlim(0);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new PcmCache(1 << 20, (file, max, cancel) =>
        {
            started.Release();
            try { release.Wait(cancel); } catch (OperationCanceledException) { stopped.TrySetResult(); throw; }
            return PcmCache.Decode(file, max, cancel);
        });
        using var impatient = new CancellationTokenSource();
        var first = cache.Get(path, impatient.Token); var second = cache.Get(path);
        Assert.True(await started.WaitAsync(Patience));
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted); Assert.False(stopped.Task.IsCompleted); // someone still wants it
        release.Set();
        Assert.Equal(4800, (await second).FrameCount); Assert.Equal(1, cache.DecodeCount);

        // The only caller gives up: the decode is cancelled and nothing is kept, so the next request starts over.
        cache.Clear(); release.Reset();
        using var only = new CancellationTokenSource();
        var third = cache.Get(path, only.Token);
        Assert.True(await started.WaitAsync(Patience));
        only.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        await stopped.Task.WaitAsync(Patience);
        Assert.Equal(0, cache.Count); Assert.False(cache.Contains(path));
        release.Set();
        Assert.Equal(4800, (await cache.Get(path)).FrameCount); Assert.Equal(3, cache.DecodeCount);

        // A request that is already cancelled does no work at all.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.Get(Wave("b.wav"), new CancellationToken(true)));
        Assert.Equal(3, cache.DecodeCount);
    }

    [Fact]
    public async Task TheLeastRecentlyUsedFilesAreForgottenWhenTheBudgetIsFull()
    {
        string a = Wave("a.wav", shift: 100), b = Wave("b.wav", shift: 200), c = Wave("c.wav", shift: 300);
        using var cache = new PcmCache(2 * 19200 + 100); // room for two of them
        var heldA = await cache.Get(a); await cache.Get(b);
        Assert.Equal(38400, cache.Bytes);
        await cache.Get(a);                               // a is now the more recently used
        await cache.Get(c);
        Assert.True(cache.Contains(a)); Assert.False(cache.Contains(b)); Assert.True(cache.Contains(c));
        Assert.Equal((2, 38400L, 3L), (cache.Count, cache.Bytes, cache.DecodeCount));
        var again = await cache.Get(b);                   // decoded afresh, and now a is the oldest
        Assert.Equal(SliceFixture.Low(200) / 32768f, First(again));
        Assert.False(cache.Contains(a)); Assert.True(cache.Contains(c)); Assert.True(cache.Contains(b));
        Assert.Equal((2, 38400L, 4L), (cache.Count, cache.Bytes, cache.DecodeCount));
        Assert.Equal(SliceFixture.Low(100) / 32768f, First(heldA)); // a source already handed out keeps working

        // One file bigger than the whole budget is refused instead of blowing the limit.
        using var tiny = new PcmCache(1000);
        await Assert.ThrowsAsync<InvalidDataException>(() => tiny.Get(a));
        Assert.Equal((0, 0L), (tiny.Count, tiny.Bytes));

        cache.Invalidate(c); Assert.False(cache.Contains(c)); Assert.Equal(19200, cache.Bytes);
        cache.Clear(); Assert.Equal((0, 0L), (cache.Count, cache.Bytes));
    }

    [Fact]
    public async Task AChangedFileIsDecodedAgainAndItsOldAudioDropped()
    {
        var path = Wave("a.wav", 4800, shift: 100);
        using var cache = new PcmCache();
        var old = await cache.Get(path);
        Assert.Equal(SliceFixture.Low(100) / 32768f, First(old));

        Wave("a.wav", 9600, shift: 500); // different length
        var longer = await cache.Get(path);
        Assert.Equal(9600, longer.FrameCount); Assert.Equal(SliceFixture.Low(500) / 32768f, First(longer));
        Assert.Equal((1, 9600 * 4L, 2L), (cache.Count, cache.Bytes, cache.DecodeCount)); // the first version no longer takes room

        Wave("a.wav", 9600, shift: 900); // same length, new contents: the write time tells them apart
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        var rewritten = await cache.Get(path);
        Assert.Equal(SliceFixture.Low(900) / 32768f, First(rewritten));
        Assert.Equal((1, 9600 * 4L, 3L), (cache.Count, cache.Bytes, cache.DecodeCount));
        Assert.Same(rewritten, await cache.Get(path));
        Assert.Equal(SliceFixture.Low(100) / 32768f, First(old)); // what a caller already holds is not changed under it
    }

    [Fact]
    public async Task AudioThatCanNeverBePreparedIsNotDecodedAgainUntilTheFileChanges()
    {
        // Review, 2.37: every cue start on a sound too long for the cache decoded it again, 256 MiB each time.
        var path = Wave("long.wav");
        int calls = 0;
        using var cache = new PcmCache(1 << 20, (file, max, cancel) => { Interlocked.Increment(ref calls); throw new InvalidDataException("This sound is too long to prepare for exact playback."); });
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.Get(path));
        var again = await Assert.ThrowsAsync<InvalidDataException>(() => cache.Get(path));
        Assert.Equal(1, calls); Assert.Contains("too long", again.Message);
        // A changed file is another file: tried again.
        Wave("long.wav", frames: 4801);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.Get(path));
        Assert.Equal(2, calls);
        // A file that could not be opened (in use, say) is not remembered.
        int locked = 0;
        using var busy = new PcmCache(1 << 20, (file, max, cancel) => ++locked == 1 ? throw new IOException("in use") : PcmCache.Decode(file, max, cancel));
        await Assert.ThrowsAsync<InvalidDataException>(() => busy.Get(path));
        Assert.Equal(4801, (await busy.Get(path)).FrameCount);
    }

    [Fact]
    public void AFileLongerThanTheBudgetIsRefusedBeforeItsAudioIsAllocatedOrRead()
    {
        // 12 s of 48 kHz stereo is 4.6 MB of floats; the budget is 2 MB.
        var path = Path.Combine(folder, "twelve.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) writer.Write(new byte[48000 * 4 * 12], 0, 48000 * 4 * 12);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => PcmCache.Decode(path, 2 * 1024 * 1024));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("too long", error.Message);
        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes to refuse it");
    }

    [Fact]
    public void ACancelledRenderStops()
    {
        var source = new PcmSliceSource(new float[48000 * 2 * 4], 48000, 2);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SliceRenderer.Render(source, 0, 48000 * 4, null, stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SliceWav.Encode(new float[1000], 48000, 2, stop.Token));
        Assert.Equal(SliceRenderer.Render(source, 0, 4800), SliceRenderer.Render(source, 0, 4800, null, CancellationToken.None));
    }

    [Fact]
    public async Task FailuresAreReportedOneWayAndNotRemembered()
    {
        using var cache = new PcmCache();
        var path = Path.Combine(folder, "broken.wav"); File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.Get(path));
        Assert.Equal(0, cache.Count);
        Wave("broken.wav");
        Assert.Equal(4800, (await cache.Get(path)).FrameCount); // the repaired file works; the failure was not cached

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.Get(Path.Combine(folder, "missing.wav")));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.Get(@"\\server\share\sound.wav")); // local files only
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.Get("relative.wav"));

        // A decoder that fails in its own way still surfaces as the one documented error, and is retried next time.
        int calls = 0;
        using var flaky = new PcmCache(1 << 20, (file, max, cancel) => ++calls == 1 ? throw new FormatException("codec") : PcmCache.Decode(file, max, cancel));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => flaky.Get(path));
        Assert.IsType<FormatException>(error.InnerException);
        Assert.Equal(4800, (await flaky.Get(path)).FrameCount); Assert.Equal(2, calls);

        cache.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.Get(path));
    }
}
