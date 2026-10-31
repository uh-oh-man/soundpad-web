using System.Threading.Channels;

namespace VoiceReplay.Core;

public sealed class ClipPipeline : IAsyncDisposable
{
    private readonly Store store;
    private readonly ITranscriber transcriber;
    private readonly SoundpadService soundpad;
    private readonly Channel<(Clip Clip, bool Speech, bool ForceImport)> queue = Channel.CreateUnbounded<(Clip, bool, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim pendingSaves = new(8, 8);
    private readonly CancellationTokenSource cancel = new();
    private readonly Task worker;
    private volatile bool closing;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> scheduled = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> clipLocks = new();
    public event Action? Changed;
    public ClipPipeline(Store store, ITranscriber transcriber, SoundpadService soundpad)
    {
        this.store = store;
        this.transcriber = transcriber;
        this.soundpad = soundpad;
        worker = Task.Run(Work);
    }
    public void Notify() => Changed?.Invoke();
    public async Task<Clip> SaveCapture(float[] samples, int rate, string source)
    {
        if (closing)
            throw new InvalidOperationException("Application is closing.");
        if (!pendingSaves.Wait(0))
            throw new InvalidOperationException("Eight recordings are still saving. Please wait briefly.");
        try
        {
            if (AudioFiles.Silent(samples))
                throw new InvalidOperationException("Silent capture was not saved.");
            return await Task.Run(() =>
            {
                var clip = new Clip { Source = source, Duration = samples.Length / (double)rate, TrimEnd = samples.Length / (double)rate };
                var directory = Path.Combine(store.Settings.StorageDirectory, clip.Id.ToString("N"));
                Directory.CreateDirectory(directory);
                clip.AudioPath = Path.Combine(directory, Names.FileName(clip));
                clip.OriginalPath = Path.Combine(directory, "original.wav");
                AudioFiles.Write(clip.AudioPath, samples, rate);
                File.Copy(clip.AudioPath, clip.OriginalPath);
                store.Add(clip);
                Notify();
                Enqueue(clip, true);
                return clip;
            });
        }
        finally { pendingSaves.Release(); }
    }
    public void Enqueue(Clip clip, bool speech, bool forceImport = false)
    {
        if (scheduled.TryAdd(clip.Id, 0) && !queue.Writer.TryWrite((clip, speech, forceImport)))
        {
            scheduled.TryRemove(clip.Id, out _);
            throw new InvalidOperationException("Application is closing.");
        }
    }
    public bool Busy(Clip c) => scheduled.ContainsKey(c.Id);
    public async Task Edit(Clip clip, Action action)
    {
        var mutex = clipLocks.GetOrAdd(clip.Id, _ => new(1, 1));
        await mutex.WaitAsync();
        try
        {
            await Task.Run(action);
            store.Save();
            Notify();
        }
        finally { mutex.Release(); }
    }
    private async Task Work()
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(cancel.Token))
            {
                var c = item.Clip;
                var mutex = clipLocks.GetOrAdd(c.Id, _ => new(1, 1));
                await mutex.WaitAsync(cancel.Token);
                if (!store.Snapshot().Contains(c))
                {
                    mutex.Release();
                    scheduled.TryRemove(c.Id, out _);
                    continue;
                }
                try
                {
                    if (item.Speech)
                    {
                        c.Error = "";
                        if (store.Settings.InstantImport && store.Settings.AutoImport)
                            await Import(c);
                        c.TranscriptionState = "Working";
                        store.Save();
                        Notify();
                        try
                        {
                            var text = await transcriber.Transcribe(c.AudioPath, cancel.Token);
                            c.Transcript = text;
                            c.TranscriptionState = text.Length == 0 ? "No confident speech" : "Done";
                            if (text.Length > 0)
                            {
                                c.Title = text;
                                RenameFile(c);
                            }
                        }
                        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { c.TranscriptionState = "Pending"; throw; }
                        catch (Exception ex) { c.TranscriptionState = "Retry available"; c.Error = ex.Message; store.Log("Transcription failed for " + c.Id, ex); }
                    }
                    if ((store.Settings.AutoImport || item.ForceImport) && c.ImportedPath != c.AudioPath)
                        await Import(c);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                catch (Exception ex) { c.Error = ex.Message; store.Log("Clip processing failed " + c.Id, ex); }
                finally { store.Save(); scheduled.TryRemove(c.Id, out _); mutex.Release(); Notify(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void RenameFile(Clip c)
    {
        string target = Path.Combine(Path.GetDirectoryName(c.AudioPath)!, Names.FileName(c));
        if (target == c.AudioPath)
            return;
        while (File.Exists(target))
        {
            c.Version++;
            target = Path.Combine(Path.GetDirectoryName(c.AudioPath)!, Names.FileName(c));
        }
        // Keep the previous path valid until the replacement metadata is committed, including after a crash.
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.Copy(c.AudioPath, temporary, false);
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (!c.RetainedPaths.Contains(c.AudioPath))
            c.RetainedPaths.Add(c.AudioPath);
        c.AudioPath = target;
        c.ImportState = "Pending";
    }
    private async Task Import(Clip c)
    {
        try
        {
            // A lost response may still mean Soundpad accepted the path. Preserve every attempted backing file.
            if (!c.RetainedPaths.Contains(c.AudioPath))
                c.RetainedPaths.Add(c.AudioPath);
            c.ImportState = "Working";
            store.Save();
            Notify();
            string index = await soundpad.Import(c.AudioPath, cancel.Token);
            c.SoundpadIndex = index;
            c.ImportedPath = c.AudioPath;
            c.ImportState = "Imported (verified)";
            if (c.TranscriptionState != "Retry available")
                c.Error = "";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { c.ImportState = "Pending"; throw; }
        catch (Exception ex) { c.ImportState = "Pending retry"; c.Error = ex.Message; store.Log("Soundpad import pending " + c.Id, ex); }
    }
    public async ValueTask DisposeAsync()
    {
        closing = true;
        for (int i = 0; i < 8; i++)
            await pendingSaves.WaitAsync();
        queue.Writer.TryComplete();
        cancel.Cancel();
        await worker;
        store.Save();
        cancel.Dispose();
    }
}
