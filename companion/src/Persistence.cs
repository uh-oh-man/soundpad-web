using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
namespace SoundpadWeb;

public sealed class StateStore(string directory)
{
    private string PathName => Path.Combine(directory, "state.json");
    public DiskState Load()
    {
        Directory.CreateDirectory(directory);
        if (!File.Exists(PathName)) { var initial = new DiskState(); Save(initial); return initial; }
        try { return JsonSerializer.Deserialize<DiskState>(File.ReadAllText(PathName), Json.Options) ?? throw new InvalidDataException(); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { throw new InvalidDataException("Local state is unreadable. Preserve state.json and repair it before starting; it has not been reset.", ex); }
    }
    public void Save(DiskState state)
    {
        Directory.CreateDirectory(directory);
        var temp = PathName + ".tmp";
        using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(f, state, Json.Options); f.Flush(true);
        }
        File.Move(temp, PathName, true);
    }
}
public static class IconData
{
    public static string Normalize(string data)
    {
        string prefix = data.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? "data:image/png;base64," : data.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) ? "data:image/jpeg;base64," : throw new InvalidDataException("Use PNG or JPEG.");
        if (data.Length > 180000) throw new InvalidDataException("Icon must be under 128 KiB.");
        var bytes = Convert.FromBase64String(data[prefix.Length..]);
        if (bytes.Length > 131072) throw new InvalidDataException("Icon too large.");
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream, true, true);
        if (image.Width > 512 || image.Height > 512 || image.Width < 1 || image.Height < 1) throw new InvalidDataException("Icon dimensions must be 1–512 pixels.");
        if (image.RawFormat.Guid != ImageFormat.Png.Guid && image.RawFormat.Guid != ImageFormat.Jpeg.Guid) throw new InvalidDataException("Use PNG or JPEG.");
        // Re-encode to strip metadata and any appended content; keep transmissions compact.
        using var thumb = new Bitmap(image, new Size(96, 96));
        using var output = new MemoryStream(); thumb.Save(output, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
    }
}
