using NAudio.Wave;
namespace SoundpadWeb;

internal sealed class DecodedAudio : IDisposable
{
    private readonly IDisposable decoder = null!;
    private readonly Func<float[], int> read = null!;
    public int Channels { get; }
    public int Rate { get; }
    public TimeSpan Duration { get; }
    public DecodedAudio(string path)
    {
        bool opened = false;
        if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            try { var vorbis = new NVorbis.VorbisReader(path); decoder = vorbis; Channels = vorbis.Channels; Rate = vorbis.SampleRate; Duration = vorbis.TotalTime; read = b => vorbis.ReadSamples(b, 0, b.Length); opened = true; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        if (!opened)
        {
            WaveStream? stream = null;
            try
            {
                if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) { stream = new WaveFileReader(path); _ = stream.ToSampleProvider(); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { stream?.Dispose(); stream = null; }
            stream ??= new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { SingleReaderObject = true });
            try { var samples = stream.ToSampleProvider(); decoder = stream; Channels = samples.WaveFormat.Channels; Rate = samples.WaveFormat.SampleRate; Duration = stream.TotalTime; read = b => samples.Read(b); } catch { stream.Dispose(); throw; }
        }
        if (Channels is < 1 or > 16 || Rate is < 8000 or > 384000) { decoder.Dispose(); throw new InvalidDataException("Unsupported audio format."); }
    }
    public int Read(float[] data) => read(data);
    public void Dispose() => decoder.Dispose();
}
