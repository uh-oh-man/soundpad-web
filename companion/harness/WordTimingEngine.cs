using System.Collections.Concurrent;
using System.Text.Json;
using Whisper.net;
namespace SoundpadWeb.Testing;

// Word-timing evaluation (harness command --word-timing-eval). This file talks to Whisper.net directly so
// every timestamp source the 1.9.1 binding offers can be compared without touching the companion's own
// WhisperEngine (companion/src/MimicTranscript.cs). It only ever READS the model and the native DLLs.

/// <summary>How one Whisper pass is configured.</summary>
public enum WordMode
{
    /// <summary>Exactly what the companion runs today: phrase segments only.</summary>
    Phrase,
    /// <summary>Phrase + WithTokenTimestamps(): whisper.cpp's heuristic per-token t0/t1.</summary>
    Tokens,
    /// <summary>Tokens, on a factory created with UseDtwTimeStamps + the base.en alignment heads: adds a DTW time per token.</summary>
    Dtw,
    /// <summary>WithTokenTimestamps + WithMaxSegmentLength(1) + SplitOnWord(): whisper.cpp "-ml 1 -sow", one word per segment.</summary>
    OneWord,
}

/// <summary>One token as Whisper.net reports it. Times are in centiseconds (10 ms), relative to the audio handed in.</summary>
public sealed record RawToken(string Text, int Id, long StartCs, long EndCs, long DtwCs, float P, float TsP, float TsPSum, float VoiceLen);
public sealed record RawSegment(double StartMs, double EndMs, string Text, float Prob, float MinProb, float NoSpeech, RawToken[] Tokens);
public sealed record RawResult(RawSegment[] Segments);

public sealed class WordTimingEngine : IDisposable
{
    private readonly string model;
    private readonly string? cacheDirectory;
    private readonly object gate = new();
    private WhisperFactory? plain, dtw;
    private readonly ConcurrentDictionary<(WordMode Mode, int Threads, int Context), ConcurrentBag<WhisperProcessor>> processors = new();
    public long Calls;

    /// <param name="model">ggml model file (read only).</param>
    /// <param name="cacheDirectory">Optional folder for raw results, so metric code can be rerun without Whisper.</param>
    public WordTimingEngine(string model, string? cacheDirectory)
    {
        this.model = model; this.cacheDirectory = cacheDirectory;
        if (cacheDirectory != null) Directory.CreateDirectory(cacheDirectory);
        // Same convention as WhisperEngine.UseEmbeddedNativeLibraries: the loader probes
        // <folder of LibraryPath>\runtimes\win-x64. A normal harness build already has that folder next to the
        // EXE; otherwise fall back to the copy the companion extracted earlier (read only, never written here).
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "whisper.dll")))
        {
            var extracted = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundpadWeb", "native", "whisper-1.9.1");
            if (File.Exists(Path.Combine(extracted, "runtimes", "win-x64", "whisper.dll")))
                Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(extracted, "whisper.dll");
        }
    }

    private WhisperProcessor Build(WordMode mode, int threads, int audioContext)
    {
        WhisperFactory factory;
        lock (gate)
        {
            factory = mode == WordMode.Dtw
                ? dtw ??= WhisperFactory.FromPath(model, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = WhisperAlignmentHeadsPreset.BaseEn })
                : plain ??= WhisperFactory.FromPath(model);
        }
        // The first four options are the companion's (MimicTranscript.cs); the rest is what each mode adds.
        var builder = factory.CreateBuilder().WithLanguage("en").WithThreads(threads).WithProbabilities().WithNoSpeechThreshold(.5f);
        if (mode != WordMode.Phrase) builder = builder.WithTokenTimestamps();
        if (mode == WordMode.OneWord) builder = builder.WithMaxSegmentLength(1).SplitOnWord();
        if (audioContext > 0) builder = builder.WithAudioContextSize(audioContext);
        return builder.Build();
    }

    public bool Cached(WordMode mode, string cacheKey) => cacheDirectory != null && File.Exists(Path.Combine(cacheDirectory, cacheKey + "." + mode + ".json"));

    /// <summary>
    /// Whisper always encodes a 30 s window (1500 frames of 20 ms), however short the audio. This is the smaller
    /// window ("audio context") that still covers the audio plus two seconds, in steps of 250 frames.
    /// </summary>
    public static int ShortContext(int samples16k) => Math.Min(1500, (samples16k / 320 + 100 + 249) / 250 * 250);

    /// <summary>
    /// One Whisper pass over 16 kHz mono samples. With a cache key the result is stored and reused.
    /// <paramref name="audioContext"/> 0 = the full 30 s window, as the companion runs today.
    /// </summary>
    public async Task<RawResult> Run(WordMode mode, float[] samples16k, int threads, string? cacheKey = null, int audioContext = 0)
    {
        string? cached = cacheKey != null && cacheDirectory != null ? Path.Combine(cacheDirectory, cacheKey + "." + mode + (audioContext > 0 ? ".ctx" : "") + ".json") : null;
        if (cached != null && File.Exists(cached)) return JsonSerializer.Deserialize<RawResult>(await File.ReadAllTextAsync(cached))!;
        var bag = processors.GetOrAdd((mode, threads, audioContext), _ => []);
        if (!bag.TryTake(out var processor)) processor = Build(mode, threads, audioContext);
        try
        {
            var segments = new List<RawSegment>();
            await foreach (var s in processor.ProcessAsync(samples16k))
                segments.Add(new(s.Start.TotalMilliseconds, s.End.TotalMilliseconds, s.Text, s.Probability, s.MinProbability, s.NoSpeechProbability,
                    (s.Tokens ?? []).Select(t => new RawToken(t.Text ?? "", t.Id, t.Start, t.End, t.DtwTimestamp, t.Probability, t.TimestampProbability, t.TimestampProbabilitySum, t.VoiceLen)).ToArray()));
            Interlocked.Increment(ref Calls);
            var result = new RawResult(segments.ToArray());
            if (cached != null)
            {
                var temporary = cached + "." + Environment.CurrentManagedThreadId + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result));
                File.Move(temporary, cached, true);
            }
            return result;
        }
        finally { bag.Add(processor); }
    }

    public void Dispose()
    {
        foreach (var bag in processors.Values) while (bag.TryTake(out var p)) p.Dispose();
        plain?.Dispose(); dtw?.Dispose();
    }
}
