using NAudio.Wave;
namespace SoundpadWeb;

public sealed partial class Bridge
{
    public const int TrimMinimumMs = 100;

    /// <summary>
    /// Saves part of a Temp source as a new Temp sound, as Voice Replay Studio's trim saved a new version:
    /// the original stays untouched. Guests need the same permission as uploading.
    /// </summary>
    private async Task<Reply> TrimAudio(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            if (c.AssetId == null || c.PositionMs is not long from || c.EndMs is not long to || from < 0 || to - from < TrimMinimumMs)
                throw new InvalidDataException($"Choose a start and end at least {TrimMinimumMs / 1000.0:0.0} s apart.");
            string? path; string original;
            await audioGate.WaitAsync();
            try
            {
                var asset = Audio.AssetById(c.AssetId);
                original = asset.Name;
                path = asset.Variants.FirstOrDefault(v => v.Orientation == "normal" && v.Current && !v.Retired && File.Exists(v.Path))?.Path ?? asset.SourcePath;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { throw new InvalidDataException("Unknown source."); }
            finally { audioGate.Release(); }
            if (path == null || !File.Exists(path)) throw new InvalidDataException("The original audio for that source is no longer available.");
            byte[] wav;
            using (var audio = new DecodedAudio(path))
            {
                if (from >= audio.Duration.TotalMilliseconds) throw new InvalidDataException("The start is past the end of the sound.");
                long skip = from * audio.Rate / 1000 * audio.Channels, keep = (Math.Min(to, (long)audio.Duration.TotalMilliseconds) - from) * audio.Rate / 1000 * audio.Channels;
                using var output = new MemoryStream();
                using (var writer = new WaveFileWriter(output, new WaveFormat(audio.Rate, 16, audio.Channels)))
                {
                    var buffer = new float[audio.Channels * 4096];
                    int read;
                    while (keep > 0 && (read = audio.Read(buffer)) > 0)
                    {
                        int offset = (int)Math.Min(skip, read);
                        skip -= offset;
                        int count = (int)Math.Min(read - offset, keep);
                        for (int i = offset; i < offset + count; i++) writer.WriteSample(Math.Clamp(buffer[i], -1f, 1f));
                        keep -= count;
                    }
                }
                wav = output.ToArray();
            }
            var name = string.IsNullOrWhiteSpace(c.Name) ? original + " (trimmed)" : c.Name.Trim();
            if (name.Length > 100) name = name[..99].TrimEnd() + "…";
            return (await Upload(wav, name + ".wav", Person(actor, participant).Name, actor == "host", requester ?? participant?.Id ?? actor, audience)) with { Id = c.Id };
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
    }
}
