using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;
using System.Text.RegularExpressions;
namespace SoundpadWeb.Testing;
// Schema-derived test fixture, never included in the production executable.
public sealed class MockSoundpad : IAsyncDisposable
{
    public string Name { get; } = "soundpad-web-test-" + Guid.NewGuid().ToString("N");
    public const string Sounds = "<Soundlist><Sound index=\"1\" url=\"C:\\Private\\airhorn.wav\" title=\"Airhorn\"/><Sound index=\"2\" url=\"C:\\Private\\laugh.wav\" title=\"Laugh\"/><Sound index=\"3\" url=\"C:\\Private\\other-laugh.wav\" title=\"Laugh\"/><Sound index=\"4\" url=\"C:\\Private\\long.wav\" title=\"An extremely long sound title that should wrap gracefully on every single phone screen without breaking the grid\"/></Soundlist>";
    public const string Categories = "<Categories><Category index=\"2\" name=\"Reactions\"><Sound index=\"2\" url=\"C:\\Private\\laugh.wav\"/><Category index=\"5\" name=\"Deep cuts\"><Sound index=\"1\" url=\"C:\\Private\\airhorn.wav\"/></Category></Category><Category index=\"7\" name=\"Empty category\"/><Category index=\"8\" name=\"Favorites\"><Sound index=\"3\" url=\"C:\\Private\\other-laugh.wav\"/><Sound index=\"1\" url=\"C:\\Private\\airhorn.wav\"/></Category></Categories>";
    public string AllXml { get; set; } = Sounds;
    public string CategoryXml { get; set; } = Categories;
    public List<string> Commands { get; } = [];
    public string Status { get; private set; } = "STOPPED";
    private readonly CancellationTokenSource cancel = new();
    private readonly Task loop;
    public bool ChangeLibraryBeforeOwnedRemoval { get; set; }
    public bool StartPlaybackBeforeOwnedRemoval { get; set; }
    /// <summary>Refuses the next DoAddSound, as Soundpad does when it can't add a file.</summary>
    public bool FailNextAddSound { get; set; }
    public bool FailOwnedRemovalOnce { get; set; }
    /// <summary>Simulated cost of one pipe round trip, in ms (2.35). 0 (the default) answers at once, as before.</summary>
    public int LatencyMs { get; set; }
    /// <summary>While set, commands starting with this text are answered "R-500", as Soundpad refuses an action (2.35).</summary>
    public string? RejectPrefix { get; set; }
    /// <summary>Commands starting with <see cref="SlowPrefix"/> take this many ms more (2.35), e.g. "DoAddSound(" for an import that takes a while.</summary>
    public int SlowMs { get; set; }
    public string? SlowPrefix { get; set; }
    /// <summary>Every command with the Stopwatch timestamp of its arrival, so a run can be analysed afterwards (2.35).</summary>
    public List<(long Timestamp, string Command)> Timeline { get; } = [];
    /// <summary>The index of the sound that is "playing": the last DoPlaySound, until it is stopped or ends (2.35).</summary>
    public int? PlayingIndex { get; private set; }
    /// <summary>One play of an exact-playback slot file (2.37): what the file held at the moment Soundpad was told to play it.</summary>
    public sealed record SlotPlay(long Timestamp, int Index, string Path, long Frames, int Rate, int Channels, string Hash, byte[] Bytes);
    /// <summary>Every play of a slot file (named slot-N.wav), with a copy of its contents as they were read for that play (2.37).</summary>
    public List<SlotPlay> SlotPlays { get; } = [];
    /// <summary>
    /// When set, a play of any existing WAV lasts as long as that file (2.37). Slot files always do, as Soundpad
    /// reads them again on every play; other sounds keep <see cref="DurationMs"/> unless this is set.
    /// </summary>
    public bool FollowFileLength { get; set; }
    /// <summary>
    /// Category removals that came while one of its sounds was playing or less than a second after it stopped (2.37).
    /// The real Soundpad 4.0.35 exited once on exactly that; the companion must never do it.
    /// </summary>
    public int RiskyRemovals { get; private set; }
    // The playing slot file is held open the way Soundpad holds it (measured 2.36: it cannot be rewritten while it
    // plays, and is free at once after another play or Stop). Other files are never held, as before.
    private readonly object held = new();
    private FileStream? heldFile;
    private long? playingFileMs;
    private string? playingPath, lastAudioPath;
    private long lastAudioEnded = long.MinValue / 2, playGeneration;
    private int selectedCategory = 1, selectedIndex;
    private string CategoriesMetadata(bool withSounds = false)
    {
        var doc = XDocument.Parse(CategoryXml);
        if (!doc.Descendants("Category").Any(e => (string?)e.Attribute("type") == "1")) doc.Root!.AddFirst(new XElement("Category", new XAttribute("index", 1), new XAttribute("type", 1), new XAttribute("name", "All sounds")));
        if (withSounds) { var builtin = doc.Descendants("Category").Single(e => (string?)e.Attribute("type") == "1"); builtin.Elements("Sound").Remove(); builtin.Add(XDocument.Parse(AllXml).Root!.Elements("Sound").Select(e => new XElement(e))); }
        return doc.ToString();
    }
    private string Manage(string command)
    {
        var cats = XDocument.Parse(CategoryXml); var all = XDocument.Parse(AllXml);
        if (command.StartsWith("FixtureRemoveOwnedEntry("))
        {
            if (ChangeLibraryBeforeOwnedRemoval) { ChangeLibraryBeforeOwnedRemoval = false; all.Root!.Elements("Sound").First().SetAttributeValue("title", "Changed by another application"); AllXml = all.ToString(); }
            if (StartPlaybackBeforeOwnedRemoval) { StartPlaybackBeforeOwnedRemoval = false; BeginFixturePlayback(); }
            if (FailOwnedRemovalOnce) { FailOwnedRemovalOnce = false; return "R-500"; }
            using var proof = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(command[24..^1]));
            var expected = proof.RootElement.GetProperty("fingerprint").GetString(); var path = proof.RootElement.GetProperty("path").GetString();
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AllXml))).ToLowerInvariant();
            if (Status != "STOPPED" || expected != fingerprint) return "R-409";
            var matches = all.Root!.Elements("Sound").Where(e => (string?)e.Attribute("url") == path).ToArray(); if (matches.Length != 1) return "R-409";
            selectedCategory = 1; selectedIndex = (int)matches[0].Attribute("index")!; command = "DoRemoveSelectedEntries(false)";
        }
        if (command.StartsWith("DoAddCategory(")) { int id = cats.Descendants("Category").Select(e => (int)e.Attribute("index")!).DefaultIfEmpty(1).Max() + 1; cats.Root!.Add(new XElement("Category", new XAttribute("index", id), new XAttribute("name", Regex.Match(command, "\"([^\"]+)\"").Groups[1].Value))); }
        else if (command.StartsWith("DoAddSound("))
        {
            if (FailNextAddSound) { FailNextAddSound = false; return "R-500"; }
            var m = Regex.Match(command, "^DoAddSound\\(\"([^\"]+)\", (\\d+), (?:-1|1)\\)$");
            if (!m.Success) return "R-400";
            int id = all.Root!.Elements("Sound").Select(e => (int)e.Attribute("index")!).DefaultIfEmpty(0).Max() + 1;
            var sound = new XElement("Sound", new XAttribute("index", id), new XAttribute("url", m.Groups[1].Value), new XAttribute("title", "Uploaded audio")); try { using var wave = new NAudio.Wave.WaveFileReader(m.Groups[1].Value); sound.SetAttributeValue("duration", wave.TotalTime.ToString(@"mm\:ss\.fff")); } catch (Exception) { /* like Soundpad, length is optional */ } all.Root.Add(sound); cats.Descendants("Category").Single(e => (int)e.Attribute("index")! == int.Parse(m.Groups[2].Value)).Add(new XElement(sound));
        }
        else if (command.StartsWith("DoRemoveCategory("))
        {
            // Like Soundpad: later categories move up one index.
            int removed = int.Parse(command[17..^1]);
            var target = cats.Descendants("Category").SingleOrDefault(e => (int)e.Attribute("index")! == removed);
            if (target == null) return "R-404";
            // Measured on Soundpad 4.0.35 (2.16): a removed category's sounds leave the main list too (files stay on disk).
            var urls = target.Descendants("Sound").Select(e => (string?)e.Attribute("url")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (held)
            {
                bool playing = Status is "PLAYING" or "PAUSED" && Position < CurrentDuration && playingPath != null && urls.Contains(playingPath);
                bool justPlayed = lastAudioPath != null && urls.Contains(lastAudioPath) && Environment.TickCount64 - lastAudioEnded < 1000;
                if (playing || justPlayed) { RiskyRemovals++; Console.WriteLine("TEST_RISK " + command + (playing ? " while one of its sounds plays" : " right after one of its sounds played")); }
            }
            target.Remove();
            foreach (var e in cats.Descendants("Category").Where(e => (int)e.Attribute("index")! > removed)) e.SetAttributeValue("index", (int)e.Attribute("index")! - 1);
            if (urls.Count > 0)
            {
                all.Root!.Elements("Sound").Where(e => urls.Contains((string?)e.Attribute("url"))).Remove();
                cats.Descendants("Sound").Where(e => urls.Contains((string?)e.Attribute("url"))).Remove();
                var indexes = all.Root.Elements("Sound").Select((e, i) => (Old: (int)e.Attribute("index")!, New: i + 1)).ToDictionary(x => x.Old, x => x.New);
                foreach (var e in cats.Descendants("Sound")) e.SetAttributeValue("index", indexes[(int)e.Attribute("index")!]);
                foreach (var e in all.Root.Elements("Sound")) e.SetAttributeValue("index", indexes[(int)e.Attribute("index")!]);
            }
        }
        else if (command.StartsWith("DoSelectCategory(")) selectedCategory = int.Parse(command[17..^1]);
        else if (command.StartsWith("DoSelectIndex(")) selectedIndex = int.Parse(command[14..^1]);
        else if (command == "DoRemoveSelectedEntries(false)")
        {
            if (selectedCategory != 1) return "R-400";
            all.Root!.Elements("Sound").Where(e => (int)e.Attribute("index")! == selectedIndex).Remove(); cats.Descendants("Sound").Where(e => (int)e.Attribute("index")! == selectedIndex).Remove();
            var indexes = all.Root.Elements("Sound").Select((e, i) => (Old: (int)e.Attribute("index")!, New: i + 1)).ToDictionary(x => x.Old, x => x.New);
            foreach (var e in cats.Descendants("Sound")) e.SetAttributeValue("index", indexes[(int)e.Attribute("index")!]);
            foreach (var e in all.Root.Elements("Sound")) e.SetAttributeValue("index", indexes[(int)e.Attribute("index")!]);
        }
        AllXml = all.ToString(); CategoryXml = cats.ToString(); return "R-200";
    }
    private long startedTick, position;
    public long DurationMs { get; set; } = 2500;
    public int SeekLagSamples { get; set; }
    public bool TransientSeekStop { get; set; }
    private int remainingLag, remainingStops;
    private long beforeSeek;
    private long ReportPosition() => remainingLag-- > 0 ? beforeSeek : Position;
    private string ReportStatus() => remainingStops-- > 0 ? "STOPPED" : Status;
    private long Position => position + (Status == "PLAYING" ? Environment.TickCount64 - startedTick : 0);
    public void BeginFixturePlayback() { position = 0; startedTick = Environment.TickCount64; Status = "PLAYING"; }
    /// <summary>The person presses Soundpad's own stop (a hotkey or its window): no pipe command is involved (2.37).</summary>
    public void StopFromSoundpad() { Status = "STOPPED"; position = 0; PlayingIndex = null; EndAudio(); }
    /// <summary>The person starts the entry at <paramref name="index"/> from Soundpad itself (a hotkey) (2.37).</summary>
    public void PlayFromSoundpad(int index) { BeginFixturePlayback(); PlayingIndex = index; StartAudio(index); }
    private long CurrentDuration => playingFileMs ?? DurationMs;
    /// <summary>The sound stopped, ended, or another took its place: its file is free and the "just played" clock starts.</summary>
    private void EndAudio()
    {
        lock (held)
        {
            if (playingPath != null) { lastAudioPath = playingPath; lastAudioEnded = Environment.TickCount64; }
            heldFile?.Dispose(); heldFile = null; playingPath = null; playingFileMs = null; playGeneration++;
        }
    }
    private string? UrlOf(int index) => XDocument.Parse(AllXml).Root!.Elements("Sound").FirstOrDefault(e => (int?)e.Attribute("index") == index)?.Attribute("url")?.Value;
    /// <summary>A play of the entry at <paramref name="index"/>: slot files are read, recorded and held open while they play.</summary>
    private void StartAudio(int? index)
    {
        EndAudio();
        // A library of 50,000 fixtures (the e2e size test) is not parsed on every play when nothing needs the file.
        if (!FollowFileLength && !AllXml.Contains("slot-", StringComparison.OrdinalIgnoreCase)) return;
        var path = index is int i ? UrlOf(i) : null;
        if (path == null || !File.Exists(path)) return;
        bool slot = SlotPool.IsSlotFile(path);
        long? ms = null;
        if (slot)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); } catch (IOException) { return; }
            if (SliceWav.TryRead(bytes, out var info))
            {
                ms = (long)Math.Round(info.DurationMs);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                lock (SlotPlays) SlotPlays.Add(new(System.Diagnostics.Stopwatch.GetTimestamp(), index!.Value, path, info.Frames, info.SampleRate, info.Channels, hash, bytes));
                Console.WriteLine($"TEST_SLOT DoPlaySound({index}) frames={info.Frames} rate={info.SampleRate} channels={info.Channels} sha256={hash}");
            }
        }
        else if (FollowFileLength) { try { using var wave = new NAudio.Wave.WaveFileReader(path); ms = (long)wave.TotalTime.TotalMilliseconds; } catch (Exception) { /* not a WAV: keep DurationMs */ } }
        long generation;
        lock (held)
        {
            playingPath = path; playingFileMs = ms; generation = playGeneration;
            if (slot) { try { heldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); } catch (IOException) { } }
        }
        if (ms is long length) _ = ReleaseWhenDone(generation, length);
    }
    /// <summary>Lets go of a played file once it has played to its end (checked again while paused).</summary>
    private async Task ReleaseWhenDone(long generation, long length)
    {
        try
        {
            while (true)
            {
                long left = length - Position;
                if (Status != "PAUSED" && left <= 0) break;
                await Task.Delay((int)Math.Clamp(left, 5, 250), cancel.Token);
                lock (held) if (playGeneration != generation) return;
            }
            lock (held) if (playGeneration == generation) { lastAudioPath = playingPath; lastAudioEnded = Environment.TickCount64; heldFile?.Dispose(); heldFile = null; }
        }
        catch (OperationCanceledException) { }
    }
    private static readonly Encoding Ansi = CodePages();
    private static Encoding CodePages() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1252); }
    public MockSoundpad(string? name = null) { if (name != null) Name = name; loop = Run(); }
    private async Task Run()
    {
        while (!cancel.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(cancel.Token);
                while (server.IsConnected && !cancel.IsCancellationRequested)
                {
                    byte[] buffer = new byte[4096]; int read = await server.ReadAsync(buffer, cancel.Token); if (read == 0) break;
                    // Real Soundpad decodes commands in the Windows ANSI code page (1252 on the owner's PC and CI), not
                    // UTF-8; decoding UTF-8 here hid the 2.15.8 bug where "·" arrived as "Â·".
                    var command = Ansi.GetString(buffer, 0, read); lock (Commands) Commands.Add(command);
                    lock (Timeline) Timeline.Add((System.Diagnostics.Stopwatch.GetTimestamp(), command));
                    // Windows timers are too coarse (about 15 ms) for a few milliseconds, so the short part is spun.
                    if (LatencyMs > 0) { long until = System.Diagnostics.Stopwatch.GetTimestamp() + LatencyMs * System.Diagnostics.Stopwatch.Frequency / 1000; if (LatencyMs > 20) await Task.Delay(LatencyMs - 16, cancel.Token); while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(64); }
                    if (SlowMs > 0 && SlowPrefix is { } slow && command.StartsWith(slow)) await Task.Delay(SlowMs, cancel.Token);
                    if (Status == "PLAYING" && Position >= CurrentDuration) { Status = "STOPPED"; PlayingIndex = null; EndAudio(); }
                    if (RejectPrefix is { } rejected && command.StartsWith(rejected)) { await server.WriteAsync(Encoding.UTF8.GetBytes("R-500"), cancel.Token); continue; }
                    string result = command switch { "GetSoundlist()" => AllXml, "GetCategories(true, true)" => CategoryXml, "GetCategories(false, false)" => CategoriesMetadata(), "GetCategories(true, false)" => CategoriesMetadata(true), "GetPlayStatus()" => ReportStatus(), "GetPlaybackPositionInMs()" => ReportPosition().ToString(), "GetPlaybackDurationInMs()" => CurrentDuration.ToString(), "IsAlive()" => "R-200", _ => command.StartsWith("Do") ? "R-200" : "R-404" };
                    if (command.StartsWith("FixtureRemoveOwnedEntry(") || command.StartsWith("DoAdd") || command.StartsWith("DoRemoveCategory(") || command.StartsWith("DoSelectCategory(") || command.StartsWith("DoSelectIndex(") || command.StartsWith("DoRemoveSelectedEntries(")) result = Manage(command);
                    if (command.StartsWith("DoPlaySound(")) { BeginFixturePlayback(); PlayingIndex = int.TryParse(command[12..^1], out int playing) ? playing : null; Console.WriteLine("TEST_PLAY " + command); StartAudio(PlayingIndex); }
                    if (command.StartsWith("DoSeekMs(")) { beforeSeek = Position; remainingLag = SeekLagSamples; remainingStops = TransientSeekStop ? 1 : 0; position = long.Parse(command[9..^1]); startedTick = Environment.TickCount64; Console.WriteLine("TEST_PLAY " + command); }
                    if (command == "DoTogglePause()") { Console.WriteLine("TEST_PLAY " + command); position = Position; Status = Status == "PLAYING" ? "PAUSED" : "PLAYING"; startedTick = Environment.TickCount64; }
                    // Its own prefix (2.35): e2e tests count TEST_PLAY lines, and a stop must not change those counts.
                    if (command == "DoStopSound()") { Status = "STOPPED"; position = 0; PlayingIndex = null; EndAudio(); Console.WriteLine("TEST_STOP " + command); }
                    await server.WriteAsync(Encoding.UTF8.GetBytes(result), cancel.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { cancel.Cancel(); await loop; EndAudio(); cancel.Dispose(); }
}
