using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// A companion with a mock Soundpad for the backup tests (2.44): its own temporary data folder, backup folder, sound
/// files and a made-up Soundpad folder (never %APPDATA%\Leppsoft), a fake backup clock, fixture capture and transcripts.
/// </summary>
internal sealed class BackupRig : IAsyncDisposable
{
    public readonly string Root, Data, Backups, Leppsoft, Sounds;
    public readonly MockSoundpad Mock = new();
    public readonly SoundpadPipe Pipe;
    public Bridge Bridge { get; private set; }
    public MimicFixture Capture { get; private set; } = new();
    public DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly BackupFiles? files;
    private bool listening;
    private int clips;

    public BackupRig(string root, BackupFiles? files = null, string? data = null)
    {
        Root = root; Data = data ?? Path.Combine(root, "data"); Backups = Path.Combine(root, "backups"); Leppsoft = Path.Combine(root, "leppsoft"); Sounds = Path.Combine(root, "sounds");
        this.files = files;
        Directory.CreateDirectory(Data); Directory.CreateDirectory(Sounds);
        var all = System.Xml.Linq.XDocument.Parse(MockSoundpad.Sounds);
        var categories = System.Xml.Linq.XDocument.Parse(MockSoundpad.Categories);
        foreach (var e in all.Descendants("Sound").Concat(categories.Descendants("Sound")))
        {
            var url = (string?)e.Attribute("url"); if (url == null) continue;
            var path = Path.Combine(Sounds, Path.GetFileName(url)); if (!File.Exists(path)) File.WriteAllBytes(path, Wav(0.5, 220));
            e.SetAttributeValue("url", path);
        }
        Mock.AllXml = all.ToString(); Mock.CategoryXml = categories.ToString();
        Pipe = new SoundpadPipe(Mock.Name, true);
        Bridge = Make();
    }

    private Bridge Make()
    {
        var b = new Bridge(Pipe, new StateStore(Data));
        Capture = new MimicFixture();
        b.UseMimicCapture(Capture); b.UseTranscriptEngine(new TranscriptFixture()); b.UseHotkey(new HotkeyFixture());
        b.WorkerPauseMs = 0; b.BackgroundWorker = false;
        b.UseLeppsoftFolder(Leppsoft); b.UseSuggestedBackupFolder(Path.Combine(Root, "suggested")); b.UseBackupClock(() => Now);
        if (files != null) b.UseBackupFiles(files);
        return b;
    }

    /// <summary>A new companion on the same data folder (the old one is closed as an ordinary quit would).</summary>
    public async Task Restart(BackupFiles? use = null)
    {
        Bridge.Dispose();
        Bridge = Make();
        if (use != null) Bridge.UseBackupFiles(use);
        listening = false;
        await Bridge.Poll(true);
    }

    public static byte[] Wav(double seconds, double hz, int rate = 16000)
    {
        int frames = (int)(seconds * rate);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + frames * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(frames * 2);
        for (int i = 0; i < frames; i++) w.Write((short)(Math.Sin(i * hz * 2 * Math.PI / rate) * 8000));
        w.Flush();
        return stream.ToArray();
    }

    public static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, SoundpadWeb.Json.Options);
    public static JsonElement Body(Reply r) => JsonSerializer.SerializeToElement(r.Data, SoundpadWeb.Json.Options);
    public async Task<Reply> Ok(Command c) { var r = await Bridge.Execute(c, "host"); Assert.True(r.Ok, c.Op + ": " + r.Error); return r; }
    public async Task<string> Refused(Command c, string actor = "host") { var r = await Bridge.Execute(c, actor); Assert.False(r.Ok, c.Op + " was expected to be refused"); return r.Error!; }
    public Task<Reply> Op(string op, object body, string actor = "host") => Bridge.Execute(Cmd(op) with { Body = Json(body) }, actor);
    public async Task<JsonElement> OkOp(string op, object body) { var r = await Op(op, body); Assert.True(r.Ok, op + ": " + r.Error); return Body(r); }

    public async Task Host()
    {
        await Bridge.Poll(true);
        await Ok(Cmd("beginSession") with { AudienceId = Guid.NewGuid().ToString() });
    }

    /// <summary>Keeps a new Mimic clip (it becomes a voice-library recording). Returns its asset id.</summary>
    public async Task<string> KeepClip(string name)
    {
        if (!listening) { await Ok(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }); listening = true; }
        Capture.Advance(TimeSpan.FromSeconds(6));
        var peek = JsonSerializer.Deserialize<MimicPeek>(JsonSerializer.Serialize((await Ok(Cmd("mimicPeek"))).Data, SoundpadWeb.Json.Options), SoundpadWeb.Json.Options)!;
        clips++;
        // Positions are in the peek's window, which ends at MimicEnd: the last 3 s but one.
        await Ok(Cmd("mimicClip") with { MimicEnd = peek.End, PositionMs = peek.WindowMs - 3000, EndMs = peek.WindowMs - 1000, Name = name, SourceId = peek.RingId });
        var journal = Journal();
        var asset = journal.Assets.Last(a => a.Name == name);
        var variant = asset.Variants.First(v => v.Orientation == "normal" && v.Current && v.OwnsFile && !v.Permanent);
        await Ok(Cmd("keepAudio") with { AssetId = asset.Id, VariantId = variant.Id, Confirmed = true });
        return asset.Id;
    }

    public AudioJournal Journal() => JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(Data, "managed-audio", "journal.json")), SoundpadWeb.Json.Options)!;
    public AudioVariant KeptVariant(string assetId) => Journal().Assets.Single(a => a.Id == assetId).Variants.Single(v => v.Permanent);
    public DiskState State() => JsonSerializer.Deserialize<DiskState>(File.ReadAllText(Path.Combine(Data, "state.json")), SoundpadWeb.Json.Options)!;

    public Task SetFolder(string? folder = null, int? keepCount = null, int? keepDays = null) =>
        OkOp("backupSettings", new { action = "set", folder = folder ?? Backups, keepCount, keepDays });

    public async Task<BackupRunResult> Backup(string kind = "manual")
    {
        var r = await Bridge.RunBackup(kind);
        Assert.True(r.Ok, "backup: " + r.Message);
        return r;
    }

    /// <summary>The install's own backup folder.</summary>
    public string Install => Directory.GetDirectories(Path.Combine(Backups, BackupFolders.RootName)).Single();
    public BackupRepository Repo => new(Install, new BackupFiles());
    public BackupManifest Manifest(string id) => Repo.Read(id, out var problem) ?? throw new InvalidOperationException(problem);

    /// <summary>A kept sentence version written through the store, as Keep does.</summary>
    public (string Id, string File, byte[] Wav) Sentence(string title, int versions = 1)
    {
        var store = Bridge.SentenceStoreForTests;
        var id = SentenceStore.NewId(); long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<SentenceVersion>(); byte[] last = [];
        string file = "";
        for (int n = 1; n <= versions; n++)
        {
            last = Wav(0.3, 300 + 40 * n);
            var (f, hash, _) = store.WriteAudio(id, n, last); file = f;
            list.Add(new SentenceVersion { N = n, At = now, Text = title, File = f, Hash = hash, Bytes = last.Length, DurationMs = 300 });
        }
        store.Change(s => { s.Sentences.Add(new SentenceProject { Id = id, Title = title, Text = title, Created = now, Updated = now, Versions = list }); return 0; });
        return (id, file, last);
    }

    public static string Icon(byte shade)
    {
        using var bitmap = new System.Drawing.Bitmap(8, 8);
        using (var g = System.Drawing.Graphics.FromImage(bitmap)) g.Clear(System.Drawing.Color.FromArgb(255, shade, 40, 200));
        using var output = new MemoryStream(); bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
    }

    public object Workspace(string folder = "Layout") => new
    {
        schemaVersion = 2, hostId = Bridge.Snapshot.HostId, layout = "personal", view = "icons", sort = "original", descending = false,
        folders = new[] { new { id = Guid.NewGuid().ToString(), name = folder, parent = (string?)null, soundIds = Array.Empty<string>() } }, favorites = Array.Empty<string>(), hidden = Array.Empty<string>(), order = Array.Empty<string>(),
        cues = new { }, regions = new { }, zoom = new { }, lastCue = (object?)null,
    };

    public async ValueTask DisposeAsync()
    {
        Bridge.Dispose(); Pipe.Dispose(); await Mock.DisposeAsync();
        TestFolders.Delete(Root);
    }
}

/// <summary>File operations that can fail on purpose, report little free space, or run code after a chunk.</summary>
internal sealed class FaultyFiles : BackupFiles
{
    public Func<string, long, bool>? CrashAfterChunk;
    public Func<string, bool>? CrashOnCreate;
    public long? Free;
    public Action<string, long>? OnChunk;
    public override long FreeBytes(string folder) => Free ?? base.FreeBytes(folder);
    public override void AfterChunk(string path, long written)
    {
        OnChunk?.Invoke(path, written);
        if (CrashAfterChunk?.Invoke(path, written) == true) throw new SimulatedCrash();
    }
    public override Stream Create(string path) => CrashOnCreate?.Invoke(path) == true ? throw new SimulatedCrash() : base.Create(path);
}

/// <summary>Stands for the process ending in the middle of something (not an error the run handles).</summary>
internal sealed class SimulatedCrash : Exception;
