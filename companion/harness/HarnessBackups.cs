using System.Text.Json;
namespace SoundpadWeb.Testing;

/// <summary>
/// Backups in the harness (2.44, WP1). Every harness gets a made-up Soundpad folder (never %APPDATA%\Leppsoft, which is
/// only ever compared with) and a "suggested" backup folder beside its own temporary data folder, removed when the
/// harness ends. <c>--backup-hold-ms N</c> makes every backup run wait N ms first (a slow backup, for the busy button).
/// <c>--backup-roundtrip &lt;dir&gt;</c> is a whole round trip: back up, delete and damage, restore, compare bytes.
/// </summary>
public static class HarnessBackups
{
    private static string? dataDirectory;

    public static void BeforeBridge(string[] args, string dataDir) => dataDirectory = dataDir;

    public static void AfterBridge(string[] args, Bridge bridge, LocalServer server)
    {
        if (dataDirectory == null) return;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dataDirectory))!;
        bridge.UseLeppsoftFolder(Path.Combine(parent, Path.GetFileName(dataDirectory) + "-leppsoft"));
        var suggested = dataDirectory.TrimEnd('\\') + "-suggested-backups";
        bridge.UseSuggestedBackupFolder(suggested);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { if (Directory.Exists(suggested)) Directory.Delete(suggested, true); } catch (Exception) { } };
        int at = Array.IndexOf(args, "--backup-hold-ms");
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var hold) && hold is > 0 and <= 30000)
        {
            bridge.BackupHold = cancel => Task.Delay(hold, cancel);
            Console.WriteLine($"Backups: every run waits {hold} ms first (--backup-hold-ms).");
        }
    }

    private static Command C(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value, Json.Options);
    private static async Task<Reply> Must(Task<Reply> reply, string what) { var r = await reply; if (!r.Ok) throw new InvalidOperationException(what + ": " + r.Error); return r; }

    /// <summary>A workspace as a page would send it (schema 2), for the host id of <paramref name="bridge"/>.</summary>
    public static JsonElement Workspace(Bridge bridge, string folder = "Backed up") => Body(new
    {
        schemaVersion = 2, hostId = bridge.Snapshot.HostId, layout = "personal", view = "icons", sort = "original", descending = false,
        folders = new[] { new { id = Guid.NewGuid().ToString(), name = folder, parent = (string?)null, soundIds = Array.Empty<string>() } }, favorites = Array.Empty<string>(), hidden = Array.Empty<string>(), order = Array.Empty<string>(),
        cues = new { }, regions = new { }, zoom = new { }, lastCue = (object?)null,
    });

    /// <summary>
    /// At the end of --make-rollback-fixture (2.44): backup settings pointing beside the fixture, a backed-up layout, a
    /// kept clip in the voice library, one backup, and that recording removed and restored from it.
    /// </summary>
    public static async Task RollbackFixture(string dir, Bridge b)
    {
        var backups = dir.TrimEnd('\\') + "-backups";
        b.UseLeppsoftFolder(dir.TrimEnd('\\') + "-leppsoft");
        await Must(b.Execute(C("backupSettings") with { Body = Body(new { action = "set", folder = backups }) }, "host"), "backup settings");
        var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(dir, "managed-audio", "journal.json")), Json.Options)!;
        var clip = journal.Assets.SelectMany(a => a.Variants.Where(v => v.Orientation == "normal" && v.Current && v.OwnsFile && !v.Permanent && v.State == "ready").Select(v => (a.Id, VariantId: v.Id))).First();
        await Must(b.Execute(C("keepAudio") with { AssetId = clip.Id, VariantId = clip.VariantId, Confirmed = true }, "host"), "keep");
        await Must(b.Execute(C("backupAttach") with { Body = Body(new { source = "browser", workspace = Workspace(b) }) }, "host"), "layout");
        var run = await b.RunBackup("manual");
        if (!run.Ok) throw new InvalidOperationException("backup: " + run.Message);
        var recording = b.VoiceLibraryStore.List(new(), 0, 10).Items.First();
        await Must(b.Execute(C("libraryRemove") with { RecordingId = recording.Id }, "host"), "remove recording");
        var restored = await Must(b.Execute(C("backupRestore") with { Body = Body(new { snapshot = run.Id, kind = "recording", ids = new[] { recording.Id } }) }, "host"), "restore recording");
        Console.WriteLine($"backups: {run.Id} in {backups}; recording restored: {JsonSerializer.Serialize(restored.Data, Json.Options)}");
    }

    /// <summary>A small PNG icon as a data URL.</summary>
    public static string Icon(System.Drawing.Color color)
    {
        using var bitmap = new System.Drawing.Bitmap(8, 8);
        using (var g = System.Drawing.Graphics.FromImage(bitmap)) g.Clear(color);
        using var output = new MemoryStream(); bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// --backup-roundtrip &lt;dir&gt; (a folder in the temp directory): a fixture with a kept clip (and its voice-library
    /// recording), a kept sentence version, an icon and a layout is backed up; then the kept file, the sentence audio and
    /// the icon are deleted and the voice library's file is damaged; a new companion restores the whole library; every
    /// file must come back byte for byte. Exit code 0 when it did.
    /// </summary>
    public static async Task<int> RoundTrip(string folder)
    {
        var root = Path.GetFullPath(folder);
        if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("ROUNDTRIP_FAILED use a folder in the temp directory"); return 2; }
        var data = Path.Combine(root, "data"); var backups = Path.Combine(root, "backups"); var sounds = Path.Combine(root, "sounds");
        Directory.CreateDirectory(data); Directory.CreateDirectory(sounds);
        try
        {
            await using var soundpad = new MockSoundpad();
            var all = System.Xml.Linq.XDocument.Parse(MockSoundpad.Sounds);
            var categories = System.Xml.Linq.XDocument.Parse(MockSoundpad.Categories);
            foreach (var element in all.Descendants("Sound").Concat(categories.Descendants("Sound"))) { var original = (string?)element.Attribute("url"); if (original == null) continue; var path = Path.Combine(sounds, Path.GetFileName(original)); if (!File.Exists(path)) AudioFixtures.Wave(path); element.SetAttributeValue("url", path); }
            soundpad.AllXml = all.ToString(); soundpad.CategoryXml = categories.ToString();
            string keptPath, sentencePath, iconPath, snapshot, recordingId, sentenceId;
            byte[] keptBytes, sentenceBytes, iconBytes;
            using (var pipe = new SoundpadPipe(soundpad.Name, true))
            {
                var b = new Bridge(pipe, new StateStore(data));
                try
                {
                    var capture = new MimicFixture(); b.UseMimicCapture(capture); b.UseTranscriptEngine(new TranscriptFixture()); b.UseHotkey(new HotkeyFixture());
                    b.UseLeppsoftFolder(Path.Combine(root, "leppsoft"));
                    await b.Poll(true);
                    await Must(b.Execute(C("beginSession") with { AudienceId = Guid.NewGuid().ToString() }, "host"), "begin");
                    await Must(b.Execute(C("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host"), "start");
                    capture.Advance(TimeSpan.FromSeconds(8));
                    var peekReply = await Must(b.Execute(C("mimicPeek"), "host"), "peek");
                    var peek = JsonSerializer.Deserialize<MimicPeek>(JsonSerializer.Serialize(peekReply.Data, Json.Options), Json.Options)!;
                    await Must(b.Execute(C("mimicClip") with { MimicEnd = peek.End, PositionMs = 22000, EndMs = 26000, Name = "Roundtrip clip", SourceId = peek.RingId }, "host"), "clip");
                    var journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(data, "managed-audio", "journal.json")), Json.Options)!;
                    var asset = journal.Assets.Single();
                    var variant = asset.Variants.FirstOrDefault(v => v.Orientation == "normal" && v.Current && v.OwnsFile) ?? throw new InvalidOperationException("no clip variant: " + JsonSerializer.Serialize(asset.Variants, Json.Options));
                    await Must(b.Execute(C("keepAudio") with { AssetId = asset.Id, VariantId = variant.Id, Confirmed = true }, "host"), "keep");
                    journal = JsonSerializer.Deserialize<AudioJournal>(File.ReadAllText(Path.Combine(data, "managed-audio", "journal.json")), Json.Options)!;
                    keptPath = journal.Assets.Single().Variants.Single(v => v.Permanent).Path;
                    recordingId = b.VoiceLibraryStore.List(new(), 0, 10).Items.Single().Id;
                    // An icon on the first sound.
                    var sound = b.Snapshot.Library.Sounds.FirstOrDefault(s => s.Name == "Airhorn") ?? throw new InvalidOperationException("no Airhorn: " + b.Snapshot.Error + string.Join(",", b.Snapshot.Library.Sounds.Select(s => s.Name)));
                    await Must(b.Execute(C("icon") with { SoundId = sound.Id, Image = Icon(System.Drawing.Color.CornflowerBlue) }, "host"), "icon");
                    var state = JsonSerializer.Deserialize<DiskState>(File.ReadAllText(Path.Combine(data, "state.json")), Json.Options)!;
                    iconPath = Path.Combine(data, "icons", state.IconFiles.Values.Single());
                    // A kept sentence version (written through the store, as Keep does).
                    var sentences = b.SentenceStoreForTests;
                    sentenceId = SentenceStore.NewId();
                    var wav = File.ReadAllBytes(keptPath);
                    var (file, hash, _) = sentences.WriteAudio(sentenceId, 1, wav);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    sentences.Change(f => { f.Sentences.Add(new SentenceProject { Id = sentenceId, Title = "Roundtrip sentence", Text = "fixture tone", Created = now, Updated = now, Versions = [new SentenceVersion { N = 1, At = now, Text = "fixture tone", File = file, Hash = hash, Bytes = wav.Length, DurationMs = 4000 }] }); return 0; });
                    sentencePath = sentences.AudioPath(sentenceId, file);
                    await Must(b.Execute(C("backupAttach") with { Body = Body(new { source = "window", workspace = Workspace(b, "Round trip") }) }, "host"), "layout");
                    await Must(b.Execute(C("backupSettings") with { Body = Body(new { action = "set", folder = backups }) }, "host"), "backup settings");
                    var run = await b.RunBackup("manual");
                    if (!run.Ok) throw new InvalidOperationException("backup: " + run.Message);
                    snapshot = run.Id!;
                    await Must(b.Execute(C("endSession"), "host"), "end");
                }
                finally { b.Dispose(); }
            }
            keptBytes = File.ReadAllBytes(keptPath); sentenceBytes = File.ReadAllBytes(sentencePath); iconBytes = File.ReadAllBytes(iconPath);
            // Delete and damage.
            File.Delete(keptPath); File.Delete(sentencePath); File.Delete(iconPath);
            var libraryFile = Path.Combine(data, "library", "voice-library.v1.json");
            File.WriteAllText(libraryFile, "{ damaged");
            File.Delete(Path.Combine(data, "library", "voice-library.v1.bak.json"));
            using (var pipe = new SoundpadPipe(soundpad.Name, true))
            {
                var b = new Bridge(pipe, new StateStore(data));
                try
                {
                    b.UseMimicCapture(new MimicFixture()); b.UseTranscriptEngine(new TranscriptFixture()); b.UseHotkey(new HotkeyFixture());
                    b.UseLeppsoftFolder(Path.Combine(root, "leppsoft"));
                    await b.Poll(true);
                    if (b.VoiceLibraryStore.Blocked != true) throw new InvalidOperationException("the damaged library was not detected");
                    var reply = await Must(b.Execute(C("backupRestore") with { Body = Body(new { snapshot, kind = "library" }) }, "host"), "restore");
                    Console.WriteLine("TEST_LOG restore " + JsonSerializer.Serialize(reply.Data, Json.Options));
                    if (b.VoiceLibraryStore.Get(recordingId) == null) throw new InvalidOperationException("the recording is not back");
                    if (b.SentenceStoreForTests.Read(f => f.Sentences.Any(s => s.Id == sentenceId)) != true) throw new InvalidOperationException("the sentence is not back");
                }
                finally { b.Dispose(); }
            }
            foreach (var (path, bytes) in new[] { (keptPath, keptBytes), (sentencePath, sentenceBytes), (iconPath, iconBytes) })
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException(Path.GetFileName(path) + " did not come back byte for byte");
            Console.WriteLine("ROUNDTRIP_OK " + snapshot);
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine("ROUNDTRIP_FAILED " + ex.Message + " " + ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("HarnessBackups"))?.Trim());
            return 1;
        }
        finally
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
            }
        }
    }
}
