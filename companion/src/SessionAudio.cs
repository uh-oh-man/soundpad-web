using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using NAudio.Wave;
namespace SoundpadWeb;

public record ManagedAudio(string File, string Name, string By, bool Permanent = false, string? Source = null);
// Only GUID files recorded in this journal belong to us. Native originals are never modified.
public sealed class SessionAudio(string directory)
{
    private string Folder => Path.Combine(directory, "session-audio");
    private string Journal => Path.Combine(Folder, "journal.json");
    private List<ManagedAudio>? entries;
    private List<ManagedAudio> Entries => entries ??= File.Exists(Journal)
        ? JsonSerializer.Deserialize<List<ManagedAudio>>(File.ReadAllText(Journal), Json.Options) ?? [] : [];
    public string PathFor(ManagedAudio entry)
    {
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(entry.File), "N", out _) || Path.GetExtension(entry.File) != ".wav" || Path.GetFileName(entry.File) != entry.File) throw new InvalidDataException("Invalid managed audio journal.");
        return Path.Combine(Folder, entry.File);
    }
    private void Save() { Directory.CreateDirectory(Folder); File.WriteAllText(Journal + ".tmp", Json.Write(Entries)); File.Move(Journal + ".tmp", Journal, true); }
    public ManagedAudio? Find(string path) => Entries.FirstOrDefault(e => string.Equals(PathFor(e), path, StringComparison.OrdinalIgnoreCase));
    public void Keep(string path) { var e = Find(path) ?? throw new InvalidDataException("This is not a session upload."); var index = Entries.IndexOf(e); Entries[index] = e with { Permanent = true }; try { Save(); } catch { Entries[index] = e; throw; } }
    public async Task<ManagedAudio> Prepare(string source, string name, string by, bool reverse)
    {
        var key = reverse ? source + ":" + new FileInfo(source).Length + ":" + File.GetLastWriteTimeUtc(source).Ticks : null;
        if (key != null && Entries.FirstOrDefault(e => e.Source == key && File.Exists(PathFor(e))) is { } cached) return cached;
        if (Entries.Count(e => !e.Permanent) >= 32 || Entries.Where(e => !e.Permanent).Sum(e => File.Exists(PathFor(e)) ? new FileInfo(PathFor(e)).Length : 0) >= 256L * 1024 * 1024) throw new InvalidDataException("Session audio limit reached (32 files / 256 MiB). End the session to clear temporary audio.");
        Directory.CreateDirectory(Folder);
        var entry = new ManagedAudio(Guid.NewGuid().ToString("N") + ".wav", new string(name.Where(c => !char.IsControl(c)).Take(120).ToArray()), by, Source: key);
        var output = PathFor(entry);
        try
        {
            await Task.Run(() => ConvertAudio(source, output, reverse));
            if (Entries.Where(e => !e.Permanent).Sum(e => File.Exists(PathFor(e)) ? new FileInfo(PathFor(e)).Length : 0) + new FileInfo(output).Length > 256L * 1024 * 1024) throw new InvalidDataException("Session audio exceeds the 256 MiB budget.");
            Entries.Add(entry); Save(); return entry;
        }
        catch { Entries.Remove(entry); if (File.Exists(output)) File.Delete(output); throw; }
    }
    public static void ConvertAudio(string input, string output, bool reverse)
    {
        // Decode on an MTA worker without creating audio devices. Reverse interleaved frames,
        // preserving left/right channels, rather than reversing individual samples.
        using var reader = new DecodedAudio(input);
        if (reader.Duration > TimeSpan.FromMinutes(10) || reader.Channels > 8) throw new InvalidDataException("Session audio supports up to ten minutes and eight channels.");
        using var pcm = new MemoryStream(); var buffer = new float[8192]; int count;
        using (var writer = new BinaryWriter(pcm, System.Text.Encoding.UTF8, true))
            while ((count = reader.Read(buffer)) > 0)
            {
                if (pcm.Length + count * 2L > 64L * 1024 * 1024) throw new InvalidDataException("Decoded session audio exceeds 64 MiB.");
                for (int i = 0; i < count; i++) writer.Write((short)(Math.Clamp(float.IsFinite(buffer[i]) ? buffer[i] : 0, -1, 1) * 32767));
            }
        var bytes = pcm.ToArray(); int frame = reader.Channels * 2;
        if (bytes.Length == 0 || bytes.Length % frame != 0) throw new InvalidDataException("Empty or incomplete audio.");
        if (reverse) for (int a = 0, b = bytes.Length - frame; a < b; a += frame, b -= frame) for (int c = 0; c < frame; c++) (bytes[a + c], bytes[b + c]) = (bytes[b + c], bytes[a + c]);
        using var wave = new WaveFileWriter(output, new WaveFormat(reader.Rate, 16, reader.Channels)); wave.Write(bytes, 0, bytes.Length);
    }
    private static XDocument Xml(string value)
    {
        using var reader = XmlReader.Create(new StringReader(value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
    private static async Task Ok(ISoundpad pipe, string command) { if (!(await pipe.Request(command)).StartsWith("R-200")) throw new InvalidDataException("Soundpad rejected temporary audio management. The recovery journal has been retained."); }
    public async Task Import(ISoundpad pipe, ManagedAudio entry)
    {
        var path = PathFor(entry);
        if (Xml(await pipe.Request("GetSoundlist()")).Descendants("Sound").Any(e => string.Equals((string?)e.Attribute("url"), path, StringComparison.OrdinalIgnoreCase))) return;
        var cats = Xml(await pipe.Request("GetCategories(false, false)"));
        var category = cats.Descendants("Category").FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), "temp", StringComparison.OrdinalIgnoreCase));
        if (category == null)
        {
            await Ok(pipe, "DoAddCategory(\"temp\", -1)");
            category = Xml(await pipe.Request("GetCategories(false, false)")).Descendants("Category").FirstOrDefault(e => (string?)e.Attribute("name") == "temp");
        }
        if (!int.TryParse((string?)category?.Attribute("index"), out var index)) throw new InvalidDataException("Soundpad did not provide the temp category.");
        if (path.Contains('"')) throw new InvalidDataException("Unsupported local data path.");
        await Ok(pipe, $"DoAddSound(\"{path}\", {index}, -1)");
        if (!Xml(await pipe.Request("GetSoundlist()")).Descendants("Sound").Any(e => string.Equals((string?)e.Attribute("url"), path, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Soundpad has not finished importing the temporary sound. Try again shortly.");
    }
    public async Task Cleanup(ISoundpad pipe)
    {
        foreach (var entry in Entries.Where(e => !e.Permanent).ToArray())
        {
            var path = PathFor(entry);
            var all = Xml(await pipe.Request("GetSoundlist()"));
            var matches = all.Root!.Elements("Sound").Where(e => string.Equals((string?)e.Attribute("url"), path, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var match in matches.OrderByDescending(e => (int)e.Attribute("index")!))
            {
                // Select the vendor's built-in All category, discovered by type, never a guessed ID.
                var cats = Xml(await pipe.Request("GetCategories(false, false)"));
                var allCategory = cats.Descendants("Category").SingleOrDefault(e => (string?)e.Attribute("type") == "1");
                if (!int.TryParse((string?)allCategory?.Attribute("index"), out int allId)) throw new InvalidDataException("Cannot safely locate Soundpad's All sounds category; temporary cleanup will retry later.");
                await Ok(pipe, $"DoSelectCategory({allId})");
                var current = Xml(await pipe.Request("GetSoundlist()"));
                int index = (int)match.Attribute("index")!;
                if (!current.Root!.Elements("Sound").Any(e => (int?)e.Attribute("index") == index && string.Equals((string?)e.Attribute("url"), path, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Soundpad changed during cleanup; retry later.");
                await Ok(pipe, $"DoSelectIndex({index})");
                await Ok(pipe, "DoRemoveSelectedEntries(false)");
                if (Xml(await pipe.Request("GetSoundlist()")).Root!.Elements("Sound").Any(e => string.Equals((string?)e.Attribute("url"), path, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Soundpad retained the temporary entry; its file has been preserved.");
            }
            if (File.Exists(path)) File.Delete(path);
            Entries.Remove(entry); Save();
        }
    }
}

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
