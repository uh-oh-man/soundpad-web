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
    private int selectedCategory = 1, selectedIndex;
    private string CategoriesMetadata()
    {
        var doc = XDocument.Parse(CategoryXml);
        if (!doc.Descendants("Category").Any(e => (string?)e.Attribute("type") == "1")) doc.Root!.AddFirst(new XElement("Category", new XAttribute("index", 1), new XAttribute("type", 1), new XAttribute("name", "All sounds")));
        return doc.ToString();
    }
    private string Manage(string command)
    {
        var cats = XDocument.Parse(CategoryXml); var all = XDocument.Parse(AllXml);
        if (command.StartsWith("DoAddCategory(")) { int id = cats.Descendants("Category").Select(e => (int)e.Attribute("index")!).DefaultIfEmpty(1).Max() + 1; cats.Root!.Add(new XElement("Category", new XAttribute("index", id), new XAttribute("name", "temp"))); }
        else if (command.StartsWith("DoAddSound("))
        {
            var m = Regex.Match(command, "^DoAddSound\\(\"([^\"]+)\", (\\d+), -1\\)$");
            if (!m.Success) return "R-400";
            int id = all.Root!.Elements("Sound").Select(e => (int)e.Attribute("index")!).DefaultIfEmpty(0).Max() + 1;
            var sound = new XElement("Sound", new XAttribute("index", id), new XAttribute("url", m.Groups[1].Value), new XAttribute("title", "Uploaded audio")); all.Root.Add(sound); cats.Descendants("Category").Single(e => (int)e.Attribute("index")! == int.Parse(m.Groups[2].Value)).Add(new XElement(sound));
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
                    var command = Encoding.UTF8.GetString(buffer, 0, read); lock (Commands) Commands.Add(command);
                    if (Status == "PLAYING" && Position >= DurationMs) Status = "STOPPED";
                    string result = command switch { "GetSoundlist()" => AllXml, "GetCategories(true, true)" => CategoryXml, "GetCategories(false, false)" => CategoriesMetadata(), "GetPlayStatus()" => ReportStatus(), "GetPlaybackPositionInMs()" => ReportPosition().ToString(), "GetPlaybackDurationInMs()" => DurationMs.ToString(), "IsAlive()" => "R-200", _ => command.StartsWith("Do") ? "R-200" : "R-404" };
                    if (command.StartsWith("DoAdd") || command.StartsWith("DoSelectCategory(") || command.StartsWith("DoSelectIndex(") || command.StartsWith("DoRemoveSelectedEntries(")) result = Manage(command);
                    if (command.StartsWith("DoPlaySound(")) { BeginFixturePlayback(); Console.WriteLine("TEST_PLAY " + command); }
                    if (command.StartsWith("DoSeekMs(")) { beforeSeek = Position; remainingLag = SeekLagSamples; remainingStops = TransientSeekStop ? 1 : 0; position = long.Parse(command[9..^1]); startedTick = Environment.TickCount64; Console.WriteLine("TEST_PLAY " + command); }
                    if (command == "DoTogglePause()") { position = Position; Status = Status == "PLAYING" ? "PAUSED" : "PLAYING"; startedTick = Environment.TickCount64; }
                    if (command == "DoStopSound()") { Status = "STOPPED"; position = 0; }
                    await server.WriteAsync(Encoding.UTF8.GetBytes(result), cancel.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { cancel.Cancel(); await loop; cancel.Dispose(); }
}
