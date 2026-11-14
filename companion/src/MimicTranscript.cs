using Whisper.net;
using Whisper.net.Ggml;
namespace SoundpadWeb;

// Live transcript for Mimic, using the local Whisper base.en model (docs/MIMIC.md). Speech is recognized on this
// PC; only the resulting text reaches the session. Since 2.39 it recognises the recording history with word timing
// (WordTiming.cs) and keeps the words with the history (TranscriptWords.cs).

public sealed record TranscriptLine(long StartMs, long EndMs, string Text);
/// <summary>One recognised phrase, relative to the audio handed in. <paramref name="Tokens"/> carries word timing (null from engines without it).</summary>
public sealed record TranscribedSpan(TimeSpan Start, TimeSpan End, string Text, float Probability = 1, TranscribedToken[]? Tokens = null);

/// <summary>How hard speech recognition may work while the host plays (2.39). Gaming is the default.</summary>
public enum SubtitleMode { Gaming, Balanced, Fast }
public static class SubtitleModes
{
    public static string Name(SubtitleMode mode) => mode switch { SubtitleMode.Balanced => "balanced", SubtitleMode.Fast => "fast", _ => "gaming" };
    public static SubtitleMode? Parse(string? name) => name switch { "gaming" => SubtitleMode.Gaming, "balanced" => SubtitleMode.Balanced, "fast" => SubtitleMode.Fast, _ => null };
}

/// <summary>Speech recognizer for 16 kHz mono samples. Tests use a fixture engine; production uses Whisper.</summary>
public interface ITranscriptEngine : IDisposable
{
    bool Available { get; }
    string Status { get; }
    Task Prepare(CancellationToken cancel);
    Task<IReadOnlyList<TranscribedSpan>> Transcribe(float[] samples16k, CancellationToken cancel);
    /// <summary>Gaming, Balanced or Fast; engines that have no such choice ignore it.</summary>
    void UseMode(SubtitleMode mode) { }
}

public sealed class WhisperEngine(string modelPath, string? sharedModelPath = null, string? nativeDirectory = null, int? threads = null) : ITranscriptEngine
{
    /// <summary>
    /// Settings measured by the word-timing evaluation (2.39): Gaming 1 thread on a below-normal thread (about 90 % of
    /// the CPU time is then low priority), Balanced 2 threads below normal (about half), Fast 2 threads at normal
    /// priority. All use a Whisper window sized to the audio, 3.5 to 4.8 times cheaper for short phrases.
    /// </summary>
    public int Threads => threads ?? (mode == SubtitleMode.Gaming ? 1 : 2);
    private volatile SubtitleMode mode = SubtitleMode.Gaming;
    public void UseMode(SubtitleMode next) => mode = next;
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
    /// <summary>DTW word anchors lose everything after the first 30 s of a call (word-timing evaluation, 3.8): longer audio is recognised in pieces.</summary>
    public const int MaxCallSeconds = 28, PieceSeconds = 25;
    private WhisperFactory? factory;
    private readonly List<(int Threads, int Context, WhisperProcessor Processor, List<TranscribedSpan> Collected)> processors = [];
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
    private readonly SemaphoreSlim busy = new(1, 1);
    public async Task<IReadOnlyList<TranscribedSpan>> Transcribe(float[] samples16k, CancellationToken cancel)
    {
        await busy.WaitAsync(cancel);
        try
        {
            if (samples16k.Length <= MaxCallSeconds * MimicTranscriber.SpeechRate) return await OnOwnThread(samples16k, cancel);
            var all = new List<TranscribedSpan>();
            for (int at = 0; at < samples16k.Length; at += PieceSeconds * MimicTranscriber.SpeechRate)
            {
                var piece = samples16k.AsSpan(at, Math.Min(PieceSeconds * MimicTranscriber.SpeechRate, samples16k.Length - at)).ToArray();
                foreach (var span in await OnOwnThread(piece, cancel)) all.Add(Shift(span, at));
            }
            return all;
        }
        finally { busy.Release(); }
    }
    private static TranscribedSpan Shift(TranscribedSpan span, int samples)
    {
        long cs = samples / (MimicTranscriber.SpeechRate / 100);
        var offset = TimeSpan.FromSeconds((double)samples / MimicTranscriber.SpeechRate);
        return span with { Start = span.Start + offset, End = span.End + offset, Tokens = span.Tokens?.Select(t => t with { StartCs = t.StartCs + cs, EndCs = t.EndCs + cs, DtwCs = t.DtwCs < 0 ? -1 : t.DtwCs + cs }).ToArray() };
    }
    /// <summary>The synchronous recognition runs on its own thread, below normal priority except in Fast mode, so a game keeps the CPU it needs.</summary>
    private Task<IReadOnlyList<TranscribedSpan>> OnOwnThread(float[] samples16k, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<IReadOnlyList<TranscribedSpan>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = mode;
        var thread = new Thread(() =>
        {
            try { result.TrySetResult(Recognise(samples16k)); }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true, Name = "Mimic speech recognition", Priority = current == SubtitleMode.Fast ? ThreadPriority.Normal : ThreadPriority.BelowNormal };
        thread.Start();
        return result.Task;
    }
    /// <summary>Whisper always encodes a 30 s window; this smaller one still covers the audio plus two seconds, in steps of 250 frames.</summary>
    public static int ShortContext(int samples16k) => Math.Min(1500, (samples16k / 320 + 100 + 249) / 250 * 250);
    private IReadOnlyList<TranscribedSpan> Recognise(float[] samples16k)
    {
        if (factory == null) UseEmbeddedNativeLibraries();
        // One pass gives phrases, token timestamps and the DTW anchors of every token. The heads preset must match the model (base.en).
        factory ??= WhisperFactory.FromPath(Model ?? throw new InvalidDataException("Download the speech model first."),
            new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = WhisperAlignmentHeadsPreset.BaseEn });
        int count = Threads, context = ShortContext(samples16k.Length);
        int index = processors.FindIndex(p => p.Threads == count && p.Context == context);
        (int Threads, int Context, WhisperProcessor Processor, List<TranscribedSpan> Collected) entry;
        if (index >= 0) { entry = processors[index]; processors.RemoveAt(index); }
        else
        {
            var collected = new List<TranscribedSpan>();
            var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(count).WithProbabilities().WithNoSpeechThreshold(.5f)
                .WithTokenTimestamps().WithAudioContextSize(context)
                .WithSegmentEventHandler(segment =>
                {
                    if (segment.Probability >= .45f && segment.Text.Trim() is { Length: > 0 } text && !text.StartsWith('[') && !text.StartsWith('('))
                        collected.Add(new(segment.Start, segment.End, text, segment.Probability,
                            (segment.Tokens ?? []).Select(t => new TranscribedToken(t.Text ?? "", t.Start, t.End, t.DtwTimestamp, t.Probability)).ToArray()));
                })
                .Build();
            entry = (count, context, processor, collected);
        }
        processors.Add(entry); // most recently used last; a few window sizes stay ready
        while (processors.Count > 4) { processors[0].Processor.Dispose(); processors.RemoveAt(0); }
        entry.Collected.Clear();
        entry.Processor.Process(samples16k);
        var result = entry.Collected.ToArray();
        entry.Collected.Clear();
        return result;
    }
    public void Dispose()
    {
        // Never free the native processor under a running recognition; if one is stuck, leaking beats crashing.
        if (!busy.Wait(TimeSpan.FromSeconds(15))) return;
        foreach (var p in processors) p.Processor.Dispose();
        processors.Clear(); factory?.Dispose(); factory = null;
    }
}

/// <summary>
/// Turns the recording history into word-timed transcript, chunk by chunk, behind live audio (2.39). Recent audio is
/// recognised as it arrives; older stretches when someone views them (<see cref="Request"/>). Words are kept with
/// the history in <see cref="TranscriptWords"/>, so turning transcripts off or a failure keeps what was recognised.
/// </summary>
public sealed class MimicTranscriber(ITranscriptEngine engine) : IDisposable
{
    public const int SpeechRate = 16000;
    private const double MinChunkSeconds = 2, MaxChunkSeconds = 15, PauseSeconds = 0.4, CutSearchSeconds = 5, CutWindowMs = 300, EdgeMs = 50;
    private const int MaxRequests = 8;
    private readonly object gate = new();
    private HistoryStore? store;
    private TranscriptWords? words;
    /// <summary>First frame not yet recognised by the live loop; -1 until the loop first looks (it starts at "now").</summary>
    private long committed = -1;
    private bool lastCutForced;
    private readonly List<HistoryRange> requests = [];
    private long prunedAt;
    private CancellationTokenSource? loop;
    private string status = "Off";
    public bool Enabled { get; private set; }
    public bool ModelReady => engine.Available;
    public string Status { get { lock (gate) return engine.Status is { Length: > 0 } s ? s : status; } }
    public SubtitleMode Mode { get; private set; } = SubtitleMode.Gaming;
    public void UseMode(SubtitleMode mode) { Mode = mode; engine.UseMode(mode); }

    /// <summary>Points the transcriber at a history session and its stored words (null: none).</summary>
    public void Attach(HistoryStore? history, TranscriptWords? transcript)
    {
        lock (gate) { store = history; words = transcript; committed = -1; lastCutForced = false; requests.Clear(); }
    }

    private Task? download;
    public bool Downloading => download is { IsCompleted: false };
    /// <summary>Downloads the model in the background (it can take minutes), then turns transcripts on.</summary>
    public void BeginDownload(Func<HistoryStore?> current, Action changed)
    {
        lock (gate)
        {
            if (Downloading) return;
            download = Task.Run(async () =>
            {
                try { await Enable(current, true); }
                catch (InvalidDataException) { /* engine.Status explains the failure */ }
                finally { changed(); }
            });
        }
    }
    /// <summary>Starts transcribing the history from now on. Downloads the model only when asked to.</summary>
    public async Task Enable(Func<HistoryStore?> current, bool download, CancellationToken cancel = default)
    {
        if (!engine.Available)
        {
            if (!download) throw new InvalidDataException("Download the speech model (about 148 MB, once) to turn on transcripts.");
            await engine.Prepare(cancel);
        }
        var stop = new CancellationTokenSource();
        int generation;
        lock (gate)
        {
            loop?.Cancel(); // a racing Enable (e.g. a finished download) must not leave a second loop running
            generation = ++loopGeneration;
            loop = stop; Enabled = true; committed = -1; lastCutForced = false; requests.Clear(); status = "Waiting for speech";
        }
        _ = Task.Run(() => Run(current, generation, stop.Token));
    }
    public void Disable()
    {
        lock (gate) { loop?.Cancel(); loop = null; loopGeneration++; Enabled = false; committed = -1; requests.Clear(); status = "Off"; }
    }
    private int loopGeneration;

    /// <summary>Asks for an older stretch to be recognised (someone is looking at it). Newest requests go first.</summary>
    public void Request(HistoryRange range)
    {
        lock (gate)
        {
            if (!Enabled || range.EndFrame <= range.StartFrame) return;
            requests.RemoveAll(r => r.StartFrame >= range.StartFrame && r.EndFrame <= range.EndFrame);
            if (requests.Any(r => r.StartFrame <= range.StartFrame && r.EndFrame >= range.EndFrame)) return;
            requests.Insert(0, range);
            if (requests.Count > MaxRequests) requests.RemoveRange(MaxRequests, requests.Count - MaxRequests);
        }
    }

    /// <summary>Words overlapping [start, end) that <paramref name="access"/> may see, with corrections. Empty for another session.</summary>
    public IReadOnlyList<(TranscriptWord Word, WordCorrection? Correction)> WordsBetween(HistoryStore? source, HistoryAccess access, long start, long end)
    {
        TranscriptWords? transcript;
        lock (gate) { if (source == null || !ReferenceEquals(source, store)) return []; transcript = words; }
        if (transcript == null) return [];
        // A transcript never outlives its audio, also between prunes: only words whose audio is still held.
        HistoryRange[] held;
        try { held = source.Timeline(HistoryAccess.Host).Available; }
        catch (HistoryException) { return []; }
        // The text itself is what would leak: a word is shown only when all of its audio is visible to the asker.
        return transcript.Between(start, end)
            .Where(w => access.Allows(w.Word.Start, w.Word.End) && held.Any(r => w.Word.Start < r.EndFrame && w.Word.End > r.StartFrame))
            .ToList();
    }

    /// <summary>
    /// Drops words whose audio has expired, also while transcripts are off (the live loop is not running then).
    /// Throttled like the live loop's pruning unless <paramref name="force"/> (the window just shrank).
    /// </summary>
    public void PruneExpired(HistoryStore? source, bool force = false)
    {
        TranscriptWords? transcript;
        lock (gate) { if (source == null || !ReferenceEquals(source, store)) return; transcript = words; }
        if (transcript == null) return;
        if (force) Interlocked.Exchange(ref prunedAt, long.MinValue / 2);
        PruneIfDue(source, transcript);
    }

    /// <summary>2.41: the words of <paramref name="source"/> before <paramref name="start"/> go (their audio was discarded).</summary>
    public void DropBefore(HistoryStore source, long start)
    {
        TranscriptWords? transcript;
        lock (gate) { if (!ReferenceEquals(source, store)) return; transcript = words; }
        transcript?.DropBefore(start);
    }

    /// <summary>
    /// Stores corrected text for a word; its boundaries are never touched. Saved is false (2.41) when it could not be
    /// written to disk: it is kept in memory and written again by <see cref="RetrySaves"/>.
    /// </summary>
    public (WordCorrection Fix, bool Saved) Correct(HistoryStore source, long wordId, string text, string by)
    {
        TranscriptWords? transcript;
        lock (gate) { if (!ReferenceEquals(source, store) || words == null) throw new InvalidDataException("That word is no longer in the transcript."); transcript = words; }
        var fix = transcript.Correct(wordId, text, by);
        return (fix, !transcript.CorrectionsUnsaved);
    }

    /// <summary>Why words or corrections of the current transcript exist only in memory (2.41); null when all is saved.</summary>
    public string? Unsaved { get { TranscriptWords? transcript; lock (gate) transcript = words; return transcript?.Unsaved; } }

    /// <summary>Writes again what could not be saved (2.41). True when nothing is left unsaved.</summary>
    public bool RetrySaves() { TranscriptWords? transcript; lock (gate) transcript = words; return transcript?.RetrySaves() ?? true; }

    /// <summary>Parts of [start, end) the asker may see that are not recognised yet ("pending").</summary>
    public List<HistoryRange> PendingBetween(HistoryStore? source, IReadOnlyList<HistoryRange> visible, long start, long end)
    {
        TranscriptWords? transcript;
        lock (gate) { if (source == null || !ReferenceEquals(source, store)) return []; transcript = words; }
        var result = new List<HistoryRange>();
        if (transcript == null) return result;
        foreach (var v in visible)
        {
            long s = Math.Max(start, v.StartFrame), e = Math.Min(end, v.EndFrame);
            if (s < e) result.AddRange(transcript.Missing(s, e));
        }
        return result;
    }

    /// <summary>Phrases overlapping [start, end) as lines in milliseconds relative to <paramref name="start"/> (the pre-2.39 peek shape).</summary>
    public TranscriptLine[] Lines(HistoryStore? source, HistoryAccess access, long start, long end)
    {
        if (source == null) return [];
        var lines = new List<TranscriptLine>();
        foreach (var phrase in WordsBetween(source, access, start, end).GroupBy(w => w.Word.PhraseId))
        {
            var list = phrase.OrderBy(w => w.Word.Start).ToList();
            var text = string.Join(" ", list.Select(w => (w.Correction?.Text ?? w.Word.Text).Trim())).Trim();
            if (text.Length > 0) lines.Add(new((list[0].Word.Start - start) * 1000 / source.Rate, (list[^1].Word.End - start) * 1000 / source.Rate, text));
        }
        return [.. lines.OrderBy(l => l.StartMs)];
    }

    /// <summary>
    /// The words in a finished piece of audio, for naming an edited copy after what it says (2.24), like
    /// Voice Replay Studio's "Save + retranscribe". Only while the host has transcripts on; null when off,
    /// silent or nothing was recognised. Shares the engine (and its one-at-a-time lock) with the live loop.
    /// </summary>
    public async Task<string?> Words(float[] interleaved, int rate, int channels, CancellationToken cancel)
    {
        if (!Enabled || !engine.Available || interleaved.Length == 0) return null;
        var mono = new float[interleaved.Length / Math.Max(1, channels)];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        if (Quiet(mono)) return null;
        var spans = await engine.Transcribe(Resample(mono, rate, SpeechRate), cancel);
        var text = string.Join(" ", spans.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// One step of the live loop; public so tests can drive it deterministically. Recognises the next chunk of the
    /// history behind live once it ends at a pause (relative to its own background, so game sound does not hide
    /// pauses), or at 15 s, or when <paramref name="force"/> (capture stopped) says no more audio is coming.
    /// </summary>
    public async Task<bool> Step(HistoryStore? current, bool force, CancellationToken cancel, int? generation = null)
    {
        HistoryStore history; TranscriptWords transcript; long start, live; bool startForced;
        lock (gate)
        {
            if (generation != null && generation != loopGeneration) return false;
            if (current == null || !ReferenceEquals(current, store) || words == null) return false;
            history = current; transcript = words;
            live = history.LiveFrame;
            if (committed < 0) { committed = live; return false; } // transcripts start from "now"
            start = committed; startForced = lastCutForced;
        }
        PruneIfDue(history, transcript);
        long rate = history.Rate, length = live - start;
        if (length < MinChunkSeconds * rate && !(force && length >= rate * 3 / 10)) return false;
        long end = start + Math.Min(length, (long)(MaxChunkSeconds * rate));
        float[] audio;
        try { audio = history.Read(HistoryAccess.Host, start, (int)(end - start)); }
        catch (HistoryUnavailableException ex)
        {
            // Audio that scrolled out before it was recognised is skipped; the loop resumes at the oldest audio still held.
            lock (gate) if (ReferenceEquals(current, store) && committed == start) committed = Math.Max(start + 1, ex.Available.Where(r => r.EndFrame > start).Select(r => Math.Max(r.StartFrame, start)).DefaultIfEmpty(live).Min());
            return true;
        }
        var speech = Resample(audio, (int)rate, SpeechRate);
        var energy = SpeechEnergy.Analyze(speech);
        bool full = end - start >= MaxChunkSeconds * rate;
        bool pause = Quiet(audio.AsSpan(Math.Max(0, audio.Length - (int)(PauseSeconds * rate)))) || energy.QuietAtEnd(PauseSeconds * 1000);
        // Cut at a pause so no sentence is split (a split makes Whisper guess the missing words).
        if (!force && !full && !pause) return false;
        if (Quiet(audio))
        {
            // Silent chunks never reach the recognizer.
            lock (gate) if (ReferenceEquals(current, store) && committed == start) { transcript.Add([], new(start, end)); committed = end; lastCutForced = false; }
            return true;
        }
        long cut = end; bool forced = false;
        if (!pause && full)
        {
            // No pause in 15 s: cut at the quietest moment of the last few seconds rather than at the hard end.
            double at = energy.QuietestBetween(energy.DurationMs - CutSearchSeconds * 1000, energy.DurationMs, CutWindowMs);
            if (at > 1000 && at < energy.DurationMs) cut = start + (long)(at * rate / 1000);
            forced = !energy.QuietBetween(at - CutWindowMs / 2, at + CutWindowMs / 2);
        }
        else if (!pause) forced = true; // capture stopped mid-word
        var result = await Recognise(history, start, cut, audio, speech, startForced, forced, transcript, cancel, generation);
        if (result == null) return false;
        var (recognised, next) = result.Value;
        // After a forced cut, a sentence still being spoken at the edge is held back and retried with what follows.
        lock (gate)
        {
            // A loop replaced while Whisper ran (turned off and on again, another session) must not commit or rewind.
            if (!ReferenceEquals(current, store) || generation != null && generation != loopGeneration || committed != start) return false;
            transcript.Add(recognised, new(start, next));
            committed = next; lastCutForced = forced && next == cut;
            status = "Listening for speech";
        }
        return true;
    }

    /// <summary>One piece of an older stretch someone asked for. False when there is nothing to do.</summary>
    public async Task<bool> StepRequest(HistoryStore? current, CancellationToken cancel, int? generation = null)
    {
        HistoryStore history; TranscriptWords transcript; HistoryRange piece = default; bool found = false;
        lock (gate)
        {
            if (generation != null && generation != loopGeneration) return false;
            if (current == null || !ReferenceEquals(current, store) || words == null) return false;
            history = current; transcript = words;
            long limit = committed >= 0 ? committed : history.LiveFrame;
            while (requests.Count > 0 && !found)
            {
                var r = requests[0];
                var missing = transcript.Missing(r.StartFrame, Math.Min(r.EndFrame, limit));
                if (missing.Count == 0) { requests.RemoveAt(0); continue; }
                piece = missing[0]; found = true;
            }
        }
        if (!found) return false;
        long rate = history.Rate;
        long end = Math.Min(piece.EndFrame, piece.StartFrame + (long)(MaxChunkSeconds * rate));
        float[] audio;
        try { audio = history.Read(HistoryAccess.Host, piece.StartFrame, (int)(end - piece.StartFrame)); }
        catch (HistoryUnavailableException)
        {
            // Expired (or never held) meanwhile: nothing to recognise there any more.
            lock (gate) requests.RemoveAll(r => r.StartFrame < end && r.EndFrame > piece.StartFrame);
            return true;
        }
        var speech = Resample(audio, (int)rate, SpeechRate);
        var energy = SpeechEnergy.Analyze(speech);
        if (Quiet(audio)) { transcript.Add([], new(piece.StartFrame, end)); return true; }
        long cut = end; bool forcedEnd = false;
        if (end < piece.EndFrame && !energy.QuietAtEnd(PauseSeconds * 1000))
        {
            double at = energy.QuietestBetween(energy.DurationMs - CutSearchSeconds * 1000, energy.DurationMs, CutWindowMs);
            if (at > 1000 && at < energy.DurationMs) cut = piece.StartFrame + (long)(at * rate / 1000);
            forcedEnd = !energy.QuietBetween(at - CutWindowMs / 2, at + CutWindowMs / 2);
        }
        else if (end == piece.EndFrame) forcedEnd = !energy.QuietAtEnd(CutWindowMs / 2);
        bool forcedStart = !energy.QuietBetween(0, CutWindowMs / 2);
        var result = await Recognise(history, piece.StartFrame, cut, audio, speech, forcedStart, forcedEnd, transcript, cancel, generation, holdBack: false);
        if (result == null) return false;
        lock (gate)
        {
            if (!ReferenceEquals(current, store) || generation != null && generation != loopGeneration) return false;
            // The live loop may have reached this stretch meanwhile; never recognise anything twice.
            if (transcript.Missing(piece.StartFrame, cut) is not [var only] || only != new HistoryRange(piece.StartFrame, cut)) return true;
            transcript.Add(result.Value.Words, new(piece.StartFrame, result.Value.Next));
        }
        return true;
    }

    /// <summary>Recognises [start, cut) and aligns its words. Returns the words (no ids yet) and where the next chunk begins.</summary>
    private async Task<(List<TranscriptWord> Words, long Next)?> Recognise(HistoryStore history, long start, long cut, float[] audio, float[] speech, bool forcedStart, bool forcedEnd, TranscriptWords transcript, CancellationToken cancel, int? generation, bool holdBack = true)
    {
        long rate = history.Rate;
        int cutSamples = (int)Math.Min(speech.Length, (cut - start) * SpeechRate / rate);
        var piece = cutSamples == speech.Length ? speech : speech[..cutSamples];
        var energy = SpeechEnergy.Analyze(piece);
        lock (gate) { if (generation != null && generation != loopGeneration) return null; status = "Transcribing"; }
        var spans = await engine.Transcribe(piece, cancel);
        var aligned = WordAligner.Align(spans, energy);
        double cutMs = cutSamples * 1000.0 / SpeechRate;
        long next = cut;
        if (holdBack && forcedEnd && spans.Count > 1 && cutMs - spans[^1].End.TotalMilliseconds < 400)
        {
            int last = spans.Count - 1;
            double from = aligned.Where(w => w.Phrase == last).Select(w => w.StartMs).DefaultIfEmpty(spans[^1].Start.TotalMilliseconds).Min();
            from = Math.Min(from, spans[^1].Start.TotalMilliseconds);
            long held = start + (long)(Math.Max(0, from) * rate / 1000);
            if (held > start) { next = held; aligned.RemoveAll(w => w.Phrase == last); }
        }
        double snr = energy.PeakDb - energy.FloorDb;
        var phrases = new Dictionary<int, long>();
        var result = new List<TranscriptWord>();
        foreach (var w in aligned)
        {
            long ws = start + (long)Math.Round(w.StartMs * rate / 1000), we = Math.Max(ws + 1, start + (long)Math.Round(w.EndMs * rate / 1000));
            if (ws >= next) continue;
            we = Math.Min(we, Math.Max(ws + 1, next));
            bool touches = forcedEnd && next == cut && cutMs - w.EndMs < EdgeMs || forcedStart && w.StartMs < EdgeMs;
            if (!phrases.TryGetValue(w.Phrase, out var phraseId)) phrases[w.Phrase] = phraseId = transcript.NewPhraseId();
            var text = w.Text.Length > 200 ? w.Text[..200] : w.Text;
            result.Add(new(0, ws, we, text, w.Probability, w.StretchMs, w.PauseBefore, w.PauseAfter, WordAligner.Status(w.Probability, w.StretchMs, touches, snr), phraseId));
        }
        return (result, next);
    }

    private void PruneIfDue(HistoryStore history, TranscriptWords transcript)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref prunedAt) < 30000) return;
        Interlocked.Exchange(ref prunedAt, now);
        try { transcript.Prune(history.Timeline(HistoryAccess.Host).Available); }
        catch (HistoryException) { }
    }

    private async Task Run(Func<HistoryStore?> current, int generation, CancellationToken cancel)
    {
        bool liveFirst = true;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    var history = current();
                    bool stopped = history != null && !history.Timeline(HistoryAccess.Host).Recording;
                    // Live and requested stretches take turns, so neither starves the other.
                    bool did = liveFirst
                        ? await Step(history, stopped, cancel, generation) || await StepRequest(history, cancel, generation)
                        : await StepRequest(history, cancel, generation) || await Step(history, stopped, cancel, generation);
                    liveFirst = !liveFirst;
                    if (!did) await Task.Delay(500, cancel);
                }
                catch (HistoryException) { await Task.Delay(500, cancel); /* the history changed under the step; the next one resyncs */ }
                catch (InvalidDataException) { await Task.Delay(500, cancel); }
            }
        }
        catch (OperationCanceledException) { }
        // The words already recognised stay with the history (they describe its audio); only recognising stops.
        catch (Exception ex) when (ex is not OutOfMemoryException) { lock (gate) if (generation == loopGeneration) { Enabled = false; committed = -1; requests.Clear(); status = "Transcripts stopped: " + ex.Message; } }
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
