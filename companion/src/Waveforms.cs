using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NVorbis;
namespace SoundpadWeb;

public record WaveLevel(int Bins, int[] Peaks);
public record Waveform(string SoundId, string Version, string Status, long? DurationMs = null, WaveLevel[]? Levels = null, string? Error = null, long? AnalyzedMs = null);
public sealed class Waveforms(string directory) : IDisposable
{
    private sealed record Job(string Id, string Path, string Version, TaskCompletionSource<Waveform> Completion);
    private readonly PriorityQueue<Job, (int, long)> jobs = new();
    private readonly Dictionary<string, Task<Waveform>> pending = [];
    private readonly object sync = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource stopped = new();
    private readonly Dictionary<string, Waveform> latest = [];
    public event Action<Waveform>? Changed;
    public Waveform GetSnapshot(string id, string path, int priority = 10)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return new(id, "", "unavailable", Error: "Audio is not a local file.");
        try { var version = Key(id, new FileInfo(path)); lock (sync) { if (latest.TryGetValue(id, out var value) && value.Version == version) return value; } } catch (IOException) { }
        var task = Request(id, path, priority);
        if (task.IsCompletedSuccessfully) return task.Result;
        lock (sync) return latest.GetValueOrDefault(id) ?? new(id, "", "loading");
    }
    private void Update(Waveform value) { lock (sync) latest[value.SoundId] = value; Changed?.Invoke(value); }
    private Task? worker;
    private long sequence;
    public long DecodeCount { get; private set; }
    private string Folder => Path.Combine(directory, "waveforms");
    public Task<Waveform> Request(string id, string path, int priority = 10)
    {
        if (id.Length != 64 || id.Any(c => !char.IsAsciiHexDigit(c))) return Task.FromResult(new Waveform(id, "", "unavailable", Error: "Invalid sound identity."));
        try
        {
            // Only paths resolved from the trusted native library are passed here. No URLs, devices or shares.
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !File.Exists(path)) return Task.FromResult(new Waveform(id, "", "unavailable", Error: "Audio file is missing or not a local file."));
            var file = new FileInfo(path); if (file.Length > 512L * 1024 * 1024) throw new InvalidDataException("Audio exceeds the 512 MiB analysis limit.");
            var version = Key(id, file);
            lock (sync)
            {
                if (pending.TryGetValue(version, out var existing)) return existing;
                if (pending.Count >= 16) return Task.FromResult(new Waveform(id, version, "unavailable", Error: "Analysis queue is full. Try again shortly."));
                var completion = new TaskCompletionSource<Waveform>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[version] = completion.Task; jobs.Enqueue(new(id, path, version, completion), (priority, sequence++));
                worker ??= Task.Run(Work); signal.Release(); return completion.Task;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return Task.FromResult(new Waveform(id, "", "unavailable", Error: "Audio file cannot be analyzed.")); }
    }
    private static string Key(string id, FileInfo file) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"wave-v3:{id}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"))).ToLowerInvariant();
    private async Task Work()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                await signal.WaitAsync(stopped.Token); Job job; lock (sync) { job = jobs.Dequeue(); }
                Waveform result;
                try
                {
                    Directory.CreateDirectory(Folder); var cache = Path.Combine(Folder, job.Version + ".br");
                    var cached = ReadCache(cache, job.Id, job.Version); if (cached != null) result = cached; else { DecodeCount++; result = Analyze(job.Id, job.Path, job.Version, stopped.Token, Update); }
                    if (result.Status == "ready" && cached == null)
                    {
                        using (var stream = File.Create(cache + ".tmp")) using (var compressed = new BrotliStream(stream, CompressionLevel.Optimal)) JsonSerializer.Serialize(compressed, result, Json.Options);
                        File.Move(cache + ".tmp", cache, true); Cleanup();
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { result = new(job.Id, job.Version, "unavailable", Error: "This file could not be decoded. Normal Soundpad playback is still available."); }
                Update(result); lock (sync) pending.Remove(job.Version); job.Completion.TrySetResult(result);
            }
        }
        catch (OperationCanceledException) { }
        finally { lock (sync) { foreach (var task in jobs.UnorderedItems) task.Element.Completion.TrySetCanceled(); jobs.Clear(); pending.Clear(); } }
    }
    private static Waveform? ReadCache(string path, string id, string version)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2097152) return null;
            using var stream = File.OpenRead(path); using var br = new BrotliStream(stream, CompressionMode.Decompress);
            // Bounded decompression protects corrupted cache files.
            using var output = new MemoryStream(); var buffer = new byte[8192]; int read; while ((read = br.Read(buffer)) > 0) { output.Write(buffer, 0, read); if (output.Length > 2097152) return null; }
            var value = JsonSerializer.Deserialize<Waveform>(output.ToArray(), Json.Options);
            return value is { Status: "ready", DurationMs: > 0, Levels.Length: 3 } && value.SoundId == id && value.Version == version && value.Levels.All(l => l.Bins is >= 1 and <= 32768 && l.Peaks.Length == l.Bins * 2 && l.Peaks.All(p => p is >= -32767 and <= 32767)) ? value : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
    public static Waveform Analyze(string id, string path, string version, CancellationToken cancel = default, Action<Waveform>? progress = null)
    {
        var before = new FileInfo(path); var originalKey = Key(id, before);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var peaks = new List<(float Min, float Max)>(); int blockFrames = 32, inBlock = 0; long frames = 0; float low = float.PositiveInfinity, high = float.NegativeInfinity, maxAmplitude = 0;
        WaveLevel Level(int size)
        {
            var scale = maxAmplitude > 0 ? 32767f / maxAmplitude : 0;
            int bins = (int)Math.Min(size, frames); var values = new int[bins * 2]; for (int i = 0; i < bins; i++)
            {
                int a = (int)((i * frames / bins) / blockFrames), b = (int)(((i + 1) * frames / bins - 1) / blockFrames); float min = float.PositiveInfinity, max = float.NegativeInfinity;
                for (int j = Math.Min(a, peaks.Count - 1); j <= Math.Min(b, peaks.Count - 1); j++) { min = Math.Min(min, peaks[j].Min); max = Math.Max(max, peaks[j].Max); }
                values[i * 2] = (int)Math.Clamp(min * scale, -32767, 32767); values[i * 2 + 1] = (int)Math.Clamp(max * scale, -32767, 32767);
            }
            return new(bins, values);
        }
        using var audio = new DecodedAudio(path);
        int channels = audio.Channels, rate = audio.Rate; Func<float[], int> read = audio.Read;
        long lastProgress = 0;

        {
            if (channels is < 1 or > 16 || rate is < 8000 or > 384000) throw new InvalidDataException("Unsupported audio geometry.");
            var data = new float[4096 * channels]; int count;
            while ((count = read(data)) > 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                for (int i = 0; i + channels <= count; i += channels)
                {
                    for (int c = 0; c < channels; c++) { float v = float.IsFinite(data[i + c]) ? data[i + c] : 0; low = Math.Min(low, v); high = Math.Max(high, v); maxAmplitude = Math.Max(maxAmplitude, Math.Abs(v)); }
                    frames++; inBlock++;
                    if (frames / (double)rate > 1800) throw new InvalidDataException("Audio exceeds the 30-minute analysis limit.");
                    if (inBlock == blockFrames)
                    {
                        peaks.Add((low, high)); low = float.PositiveInfinity; high = float.NegativeInfinity; inBlock = 0;
                        if (peaks.Count == 65536) { for (int j = 0; j < 32768; j++) peaks[j] = (Math.Min(peaks[j * 2].Min, peaks[j * 2 + 1].Min), Math.Max(peaks[j * 2].Max, peaks[j * 2 + 1].Max)); peaks.RemoveRange(32768, 32768); blockFrames *= 2; }
                    }
                }
                if (peaks.Count > 0 && Environment.TickCount64 - lastProgress >= 250)
                {
                    lastProgress = Environment.TickCount64;
                    var prefix = Level(512);
                    progress?.Invoke(new(id, version, "loading", Math.Max(1, (long)audio.Duration.TotalMilliseconds), [prefix, prefix, prefix], AnalyzedMs: Math.Max(1, frames * 1000 / rate)));
                }
            }
        }
        if (inBlock > 0) peaks.Add((low, high)); if (frames == 0) throw new InvalidDataException("Empty audio.");
        var after = new FileInfo(path); if (Key(id, after) != originalKey) throw new InvalidDataException("Audio changed during analysis.");
        return new(id, version, "ready", Math.Max(1, (long)(frames * 1000.0 / rate)), [Level(512), Level(4096), Level(32768)]);
    }
    private void Cleanup() { var files = new DirectoryInfo(Folder).GetFiles("*.br").OrderByDescending(f => f.LastWriteTimeUtc).ToArray(); long bytes = 0; for (int i = 0; i < files.Length; i++) { bytes += files[i].Length; if (i >= 2048 || bytes > 64L * 1024 * 1024) files[i].Delete(); } }
    public void Dispose() { stopped.Cancel(); }
}
