using System.Text;
using System.Xml.Linq;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb;

/// <summary>
/// 2.15.9: Soundpad decodes commands in the Windows ANSI code page. Companions up to 2.15.8 sent UTF-8, so
/// the Temp category "Temp · Soundpad Web [...]" arrived as "Temp Â· Soundpad Web [...]", was never recognised,
/// and was created again on every attempt (1,507 copies on the owner's PC) while reverse never finished.
/// </summary>
public sealed class SoundpadEncodingTests
{
    private const string Name = "Temp · Soundpad Web [abc123-def456]";
    private static string Garbled => Encoding.GetEncoding(1252).GetString(Encoding.UTF8.GetBytes(Name)); // "Temp Â· Soundpad Web [...]"
    static SoundpadEncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public async Task NonAsciiCategoryNamesReachSoundpadIntact()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var reference = new OwnedCategory { Name = Name };
        int index = await ManagedSoundpad.EnsureCategory(pipe, reference, () => { });
        Assert.Equal("created", reference.State); Assert.Equal(index, reference.NativeId);
        Assert.Contains($"name=\"{Name}\"", mock.CategoryXml);
        // Asking again finds it instead of creating another.
        Assert.Equal(index, await ManagedSoundpad.EnsureCategory(pipe, reference, () => { }));
        Assert.Single(mock.Commands, c => c.StartsWith("DoAddCategory("));
    }

    [Fact]
    public async Task GarbledCopiesStopTheCreateLoopAndOnlyEmptyOnesAreRemoved()
    {
        var categories = XDocument.Parse(MockSoundpad.Categories);
        int next = categories.Descendants("Category").Max(c => (int)c.Attribute("index")!) + 1;
        foreach (var i in Enumerable.Range(0, 3)) categories.Root!.Add(new XElement("Category", new XAttribute("index", next++), new XAttribute("name", Garbled)));
        categories.Root!.Add(new XElement("Category", new XAttribute("index", next++), new XAttribute("name", Garbled), new XElement("Sound", new XAttribute("index", 1), new XAttribute("url", @"C:\Private\kept.wav"))));
        categories.Root!.Add(new XElement("Category", new XAttribute("index", next++), new XAttribute("name", "Temp - Soundpad Web [zzz999-zzz999]")));
        await using var mock = new MockSoundpad { CategoryXml = categories.ToString() }; using var pipe = new SoundpadPipe(mock.Name, true);
        var reference = new OwnedCategory { Name = Name, State = "creating" };

        var stopped = await Assert.ThrowsAsync<InvalidDataException>(() => ManagedSoundpad.EnsureCategory(pipe, reference, () => { }));
        Assert.Equal(4, stopped.Data[ManagedSoundpad.GarbledKey]);
        Assert.DoesNotContain(mock.Commands, c => c.StartsWith("DoAddCategory(")); // no 1,508th copy

        Assert.Equal(3, await ManagedSoundpad.RemoveGarbledCopies(pipe, [reference]));
        var left = XDocument.Parse(mock.CategoryXml).Descendants("Category").Select(c => (string)c.Attribute("name")!).ToArray();
        Assert.Single(left, n => n == Garbled); // the one holding a sound is kept
        Assert.Contains("Temp - Soundpad Web [zzz999-zzz999]", left); // another app tag is not ours
        Assert.Contains(@"C:\Private\kept.wav", mock.CategoryXml);
        // Nothing left to remove; a second run changes nothing.
        Assert.Equal(0, await ManagedSoundpad.RemoveGarbledCopies(pipe, [reference]));
    }

    [Fact]
    public void ManagedFileNamesDropCharactersSoundpadCannotReceive()
    {
        Assert.Equal("Party _ time", ManagedFiles.Name("Party 🎉 time"));
        Assert.Equal("Café · olé", ManagedFiles.Name("Café · olé")); // representable in the ANSI code page
        Assert.True(SoundpadText.CanSend("Temp · Soundpad Web"));
        Assert.False(SoundpadText.CanSend("🎉"));
    }
}
