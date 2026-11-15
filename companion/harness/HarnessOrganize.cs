using System.Text.Json;
using System.Xml.Linq;
namespace SoundpadWeb.Testing;

/// <summary>
/// Organize mode in the harness (2.44): the mock Soundpad's library can be changed "by hand" (library-mutate), a
/// fixture file can be deleted (remove-file), and --missing-fixture adds a sound whose file does not exist. The mock
/// lists a sound added to a category only once in All sounds unless --organize-duplicates is given.
/// </summary>
public static class HarnessOrganize
{
    private static MockSoundpad? mock;
    private static string dataDir = "";
    public const string MissingTitle = "Fixture missing file";
    public static void MockReady(string[] args, MockSoundpad soundpad, string directory)
    {
        mock = soundpad; dataDir = directory;
        soundpad.DuplicateOnAdd = args.Contains("--organize-duplicates");
        if (args.Contains("--missing-fixture"))
        {
            // A sound in Reactions whose file is not there (never created), for the Needs attention group.
            var all = XDocument.Parse(soundpad.AllXml); var cats = XDocument.Parse(soundpad.CategoryXml);
            int index = all.Root!.Elements("Sound").Select(e => (int)e.Attribute("index")!).DefaultIfEmpty(0).Max() + 1;
            var path = Path.Combine(directory, "missing-fixture.wav");
            var sound = new XElement("Sound", new XAttribute("index", index), new XAttribute("url", path), new XAttribute("title", MissingTitle), new XAttribute("duration", "00:02.500"));
            all.Root.Add(sound);
            cats.Descendants("Category").First(e => (string?)e.Attribute("name") == "Reactions").AddFirst(new XElement("Sound", new XAttribute("index", index), new XAttribute("url", path)));
            soundpad.AllXml = all.ToString(); soundpad.CategoryXml = cats.ToString();
        }
    }
    public static void AfterBridge(string[] args, Bridge bridge)
    {
        // The real companion starts Stage H services after its startup cleanup; the harness has none, so here.
        if (args.Contains("--missing-fixture")) { bridge.UseFileExists(File.Exists); bridge.RescanMissingFiles(); }
        bridge.StageHStarted();
        HarnessVerbs.Register("library-mutate", (b, json) => { Mutate(json); b.Poll(true).GetAwaiter().GetResult(); Console.WriteLine("TEST_MUTATED"); });
        // The mock refuses ("R-500") every command starting with this text until "reject-prefix" with nothing clears it.
        HarnessVerbs.Register("reject-prefix", (_, prefix) => { mock!.RejectPrefix = prefix.Length == 0 ? null : prefix; Console.WriteLine("TEST_REJECTING " + prefix); });
        HarnessVerbs.Register("remove-file", (b, name) =>
        {
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Give a file name.");
            var path = Path.Combine(dataDir, name);
            if (File.Exists(path)) File.Delete(path);
            b.RescanMissingFiles();
            Console.WriteLine("TEST_REMOVED " + name);
        });
    }
    /// <summary>
    /// Changes the mock library as a person would in Soundpad: {"op":"addCategory","name"}, {"op":"removeCategory","name"},
    /// {"op":"renameCategory","name","to"}, {"op":"removeSound","category","title"} (from one category) or
    /// {"op":"addSound","category","title"} (a library sound into a category).
    /// </summary>
    private static void Mutate(string json)
    {
        var soundpad = mock ?? throw new InvalidOperationException("No mock.");
        using var document = JsonDocument.Parse(json);
        var r = document.RootElement;
        string Text(string name) => r.GetProperty(name).GetString() ?? throw new InvalidDataException(name);
        var all = XDocument.Parse(soundpad.AllXml); var cats = XDocument.Parse(soundpad.CategoryXml);
        XElement Category(string name) => cats.Descendants("Category").First(e => (string?)e.Attribute("name") == name);
        string Url(string title) => (string)all.Root!.Elements("Sound").First(e => (string?)e.Attribute("title") == title).Attribute("url")!;
        switch (Text("op"))
        {
            case "addCategory":
                int next = cats.Descendants("Category").Select(e => (int)e.Attribute("index")!).DefaultIfEmpty(1).Max() + 1;
                cats.Root!.Add(new XElement("Category", new XAttribute("index", next), new XAttribute("name", Text("name"))));
                break;
            case "removeCategory":
                var target = Category(Text("name")); int removed = (int)target.Attribute("index")!; int count = target.DescendantsAndSelf("Category").Count();
                target.Remove();
                foreach (var e in cats.Descendants("Category").Where(e => (int)e.Attribute("index")! > removed)) e.SetAttributeValue("index", (int)e.Attribute("index")! - count);
                break;
            case "renameCategory": Category(Text("name")).SetAttributeValue("name", Text("to")); break;
            case "removeSound":
                var url = Url(Text("title"));
                Category(Text("category")).Elements("Sound").Where(e => string.Equals((string?)e.Attribute("url"), url, StringComparison.OrdinalIgnoreCase)).Remove();
                break;
            case "addSound":
                var sound = all.Root!.Elements("Sound").First(e => (string?)e.Attribute("title") == Text("title"));
                Category(Text("category")).Add(new XElement("Sound", new XAttribute("index", (int)sound.Attribute("index")!), new XAttribute("url", (string)sound.Attribute("url")!)));
                break;
            default: throw new InvalidDataException("Unknown library-mutate op.");
        }
        soundpad.AllXml = all.ToString(); soundpad.CategoryXml = cats.ToString();
    }
    /// <summary>The rollback fixture's Organize files: a paused plan and its settings, which older companions must ignore.</summary>
    public static async Task RollbackFixture(string dir, Bridge b)
    {
        Command C(string op, object body) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op) { Body = JsonSerializer.SerializeToElement(body, Json.Options) };
        async Task Must(Task<Reply> reply, string what) { var r = await reply; if (!r.Ok) throw new InvalidOperationException(what + ": " + r.Error); }
        await Must(b.Execute(C("organizeSettings", new { guestOrganize = true }), "host"), "organize settings");
        var sound = b.Snapshot.Library.Sounds.First(s => !s.Temporary);
        await Must(b.Execute(C("organizeEdit", new { planRevision = 0, add = new object[] { new { kind = "createCategory", name = "Rollback organize", key = "new" }, new { kind = "addSound", soundId = sound.Id, targetKey = "new" } } }), "host"), "organize plan");
        b.PauseOrganizePlanForFixture();
        Console.WriteLine("organize: " + (File.Exists(Path.Combine(dir, "organize", "plan.v1.json")) ? "plan.v1.json and settings.v1.json written" : "no plan"));
    }
}
