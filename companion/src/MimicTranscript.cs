using Whisper.net;
using Whisper.net.Ggml;
namespace SoundpadWeb;

// Live transcript for Mimic, using the same local Whisper base.en model and settings as Voice Replay
// Studio (docs/MIMIC.md). Speech is recognized on this PC; only the resulting text reaches the session.

public sealed record TranscriptSegment(long Start, long End, string Text);
public sealed record TranscriptLine(long StartMs, long EndMs, string Text);
public sealed record TranscribedSpan(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Speech recognizer for 16 kHz mono samples. Tests use a fixture engine; production uses Whisper.</summary>
public interface ITranscriptEngine : IDisposable
{
    bool Available { get; }
    string Status { get; }
    Task Prepare(CancellationToken cancel);
    Task<IReadOnlyList<TranscribedSpan>> Transcribe(float[] samples16k, CancellationToken cancel);
}

public sealed class WhisperEngine(string modelPath, string? sharedModelPath = null, string? nativeDirectory = null) : ITranscriptEngine
{
    private static readonly string[] NativeLibraries = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll"];
    private static readonly object nativeGate = new();
    /// <summary>
    /// Single-file publishing hides the native libraries from Whisper.net's loader. When they are embedded
    /// (release builds), write them once in the runtimes/ layout the loader expects and point it there.
    /// </summary>
    private void UseEmbeddedNativeLibraries()
    {
        if (nativeDirectory == null) return;
        var assembly = typeof(WhisperEngine).Assembly;
        lock (nativeGate)
        {
            foreach (var name in NativeLibraries)
            {
                using var resource = assembly.GetManifestResourceStream("WhisperNative." + name);
                if (resource == null) return; // not embedded: the default runtimes/ probing applies
                using var memory = new MemoryStream(); resource.CopyTo(memory);
                var bytes = memory.ToArray(); var path = Path.Combine(nativeDirectory, "runtimes", "win-x64", name);
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true);
            }
            // The loader probes <folder of LibraryPath>/runtimes/win-x64, the same layout as a normal build output.
            Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(nativeDirectory, "whisper.dll");
        }
    }
    public const long MinimumModelBytes = 100_000_000;
    private WhisperFactory? factory;
    private WhisperProcessor? processor;
    private string? Model => File.Exists(modelPath) ? modelPath : sharedModelPath != null && File.Exists(sharedModelPath) ? sharedModelPath : null;
    public bool Available => Model != null;
    public string Status { get; private set; } = "";
    public async Task Prepare(CancellationToken cancel)
    {
        if (Available) return;
        var temporary = modelPath + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            Status = "Downloading the speech model (~148 MB)…";
            using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn, cancellationToken: cancel))
            await using (var file = File.Create(temporary))
            {
                var buffer = new byte[1 << 20]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                    total += read;
                    Status = $"Downloading the speech model… {total / 1_000_000} of ~148 MB";
                }
            }
            if (new FileInfo(temporary).Length < MinimumModelBytes) throw new InvalidDataException("The speech model download was incomplete.");
            File.Move(temporary, modelPath, true);
            Status = "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "Speech model setup failed: " + ex.Message; throw new InvalidDataException(Status); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<IReadOnlyList<TranscribedSpan>> Transcribe(float[] samples16k, CancellationToken cancel)
    {
        if (factory == null) UseEmbeddedNativeLibraries();
        processor ??= (factory ??= WhisperFactory.FromPath(Model ?? throw new InvalidDataException("Download the speech model first.")))
            .CreateBuilder().WithLanguage("en").WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4)).WithProbabilities().WithNoSpeechThreshold(.5f).Build();
        var result = new List<TranscribedSpan>();
        await foreach (var segment in processor.ProcessAsync(samples16k, cancel))
            if (segment.Probability >= .45f && segment.Text.Trim() is { Length: > 0 } text && !text.StartsWith('[') && !text.StartsWith('('))
                result.Add(new(segment.Start, segment.End, text));
        return result;
    }
    public void Dispose() { processor?.Dispose(); processor = null; factory?.Dispose(); factory = null; }
}

/// <summary>Turns the rolling Mimic buffer into timed transcript lines, chunk by chunk, behind live audio.</summary>
public sealed class MimicTranscriber(ITranscriptEngine engine) : IDisposable
{
    public const int SpeechRate = 16000;
    private const double MinChunkSeconds = 2, PreferredChunkSeconds = 4, MaxChunkSeconds = 15, PauseSeconds = 0.6;
    private readonly object gate = new();
    private readonly List<TranscriptSegment> segments = [];
    private MimicRing? ring;
    private long committed;
    private CancellationTokenSource? loop;
    private string status = "Off";
    public bool Enabled { get; private set; }
    public bool ModelReady => engine.Available;
    public string Status { get { lock (gate) return engine.Status is { Length: > 0 } s ? s : status; } }

    private Task? download;
    public bool Downloading => download is { IsCompleted: false };
    /// <summary>Downloads the model in the background (it can take minutes), then turns transcripts on.</summary>
    public void BeginDownload(Func<MimicRing?> currentRing, Action changed)
    {
        lock (gate)
        {
            if (Downloading) return;
            download = Task.Run(async () =>
            {
                try { await Enable(currentRing, true); }
                catch (InvalidDataException) { /* engine.Status explains the failure */ }
                finally { changed(); }
            });
        }
    }
    /// <summary>Starts transcribing the given buffer from now on. Downloads the model only when asked to.</summary>
    public async Task Enable(Func<MimicRing?> currentRing, bool download, CancellationToken cancel = default)
    {
        if (!engine.Available)
        {
            if (!download) throw new InvalidDataException("Download the speech model (about 148 MB, once) to turn on transcripts.");
            await engine.Prepare(cancel);
        }
        Disable();
        var stop = new CancellationTokenSource();
        lock (gate) { loop = stop; Enabled = true; status = "Waiting for speech"; }
        _ = Task.Run(() => Run(currentRing, stop.Token));
    }
    public void Disable()
    {
        lock (gate) { loop?.Cancel(); loop = null; Enabled = false; ring = null; segments.Clear(); status = "Off"; }
    }

    /// <summary>Lines overlapping [start, end), in milliseconds relative to <paramref name="start"/>.</summary>
    public TranscriptLine[] Lines(MimicRing source, long start, long end)
    {
        lock (gate)
        {
            if (!ReferenceEquals(source, ring)) return [];
            return segments.Where(s => s.End > start && s.Start < end)
                .Select(s => new TranscriptLine((s.Start - start) * 1000 / source.Rate, (s.End - start) * 1000 / source.Rate, s.Text)).ToArray();
        }
    }

    /// <summary>One step of the loop; public so tests can drive it deterministically.</summary>
    public async Task<bool> Step(MimicRing? current, bool force, CancellationToken cancel)
    {
        if (current == null) { lock (gate) { ring = null; segments.Clear(); } return false; }
        long start, end;
        lock (gate)
        {
            if (!ReferenceEquals(current, ring)) { ring = current; segments.Clear(); committed = current.Written; }
            committed = Math.Max(committed, current.Oldest);
            start = committed; end = current.Written;
            segments.RemoveAll(s => s.End < current.Oldest);
        }
        long rate = current.Rate, length = end - start;
        if (length < MinChunkSeconds * rate) return false;
        end = start + Math.Min(length, (long)(MaxChunkSeconds * rate));
        var audio = current.Read(start, end);
        bool forced = force || end - start >= MaxChunkSeconds * rate;
        bool pause = Quiet(audio.AsSpan(Math.Max(0, audio.Length - (int)(PauseSeconds * rate))));
        if (!forced && !pause && end - start < PreferredChunkSeconds * rate) return false;
        if (Quiet(audio)) { lock (gate) if (ReferenceEquals(current, ring)) committed = end; return true; }
        lock (gate) status = "Transcribing";
        var spans = await engine.Transcribe(Resample(audio, (int)rate, SpeechRate), cancel);
        long next = end;
        var accepted = spans.ToList();
        // A sentence still being spoken at the chunk edge is held back and retried with what follows.
        if (!forced && !pause && accepted.Count > 0 && (end - start) - (long)(accepted[^1].End.TotalSeconds * rate) < 0.4 * rate)
        {
            next = start + (long)(accepted[^1].Start.TotalSeconds * rate);
            accepted.RemoveAt(accepted.Count - 1);
            if (next <= start) { next = end; accepted = spans.ToList(); }
        }
        lock (gate)
        {
            if (!ReferenceEquals(current, ring)) return false;
            foreach (var s in accepted)
                segments.Add(new(start + (long)(s.Start.TotalSeconds * rate), Math.Min(end, start + (long)(s.End.TotalSeconds * rate)), s.Text.Trim()));
            committed = next;
            status = "Listening for speech";
        }
        return true;
    }

    private async Task Run(Func<MimicRing?> currentRing, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try { if (!await Step(currentRing(), false, cancel)) await Task.Delay(500, cancel); }
                catch (InvalidDataException) { /* The chunk scrolled out while waiting; the next step resyncs. */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { lock (gate) { Enabled = false; status = "Transcripts stopped: " + ex.Message; } }
    }

    private static bool Quiet(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return true;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return Math.Sqrt(sum / samples.Length) < 0.004;
    }

    /// <summary>Linear-interpolation resampler; Whisper needs 16 kHz.</summary>
    public static float[] Resample(float[] input, int from, int to)
    {
        if (from == to) return input;
        var output = new float[(int)((long)input.Length * to / from)];
        for (int i = 0; i < output.Length; i++)
        {
            double position = (double)i * from / to;
            int index = (int)position;
            float next = index + 1 < input.Length ? input[index + 1] : input[^1];
            output[i] = (float)(input[index] + (next - input[index]) * (position - index));
        }
        return output;
    }

    public void Dispose() { Disable(); engine.Dispose(); }
}
