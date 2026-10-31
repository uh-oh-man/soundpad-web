using Whisper.net;
using Whisper.net.Ggml;

namespace VoiceReplay.Core;

public interface ITranscriber
{
    Task<string> Transcribe(string path, CancellationToken token = default);
}
public sealed class LocalTranscriber(Store store) : ITranscriber
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Status { get; private set; } = File.Exists(store.ModelPath) ? "Base English model ready (CPU)" : "Model not installed";
    public async Task Download(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        var temporary = store.ModelPath + ".download";
        try
        {
            Status = "Downloading base.en model (~148 MB)…";
            Directory.CreateDirectory(Path.GetDirectoryName(store.ModelPath)!);
            using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn, cancellationToken: token);
            using (var file = File.Create(temporary))
                await source.CopyToAsync(file, token);
            if (new FileInfo(temporary).Length < 100_000_000)
                throw new InvalidDataException("Incomplete Whisper model download.");
            // Native loader verifies the model before it replaces a configured model.
            using (var factory = WhisperFactory.FromPath(temporary))
            {
            }
            File.Move(temporary, store.ModelPath, true);
            Status = "Base English model ready (CPU)";
        }
        catch (Exception ex) { Status = "Model setup failed: " + ex.Message; throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
    public async Task<string> Transcribe(string path, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!File.Exists(store.ModelPath))
                throw new FileNotFoundException("Install the offline model from Settings. Audio is saved and can be retried.");
            var audio = AudioFiles.Read(path);
            if (AudioFiles.Silent(audio.Samples))
                return "";
            var samples = AudioFiles.Resample(audio.Samples, audio.Rate, 16000);
            using var factory = WhisperFactory.FromPath(store.ModelPath);
            using var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(4).WithProbabilities().WithNoSpeechThreshold(.5f).Build();
            var segments = new List<string>();
            await foreach (var segment in processor.ProcessAsync(samples, token))
                if (segment.Probability >= .45f && segment.Text.Trim() is { Length: > 0 } text && !text.StartsWith('[') && !text.StartsWith('('))
                    segments.Add(text.Trim());
            Status = "Base English model ready (CPU)";
            return string.Join(" ", segments);
        }
        catch (Exception ex) { Status = ex.Message; throw; }
        finally { gate.Release(); }
    }
}
