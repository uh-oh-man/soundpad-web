using System.Xml.Linq;
using NAudio.Wave;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// 2.44 fix: Soundpad renumbers every later category when one is removed, so the app's Temp category's recorded
/// index could point at another category. Imports then failed ("already exists without a verified creation record")
/// and the Temp category was left behind at session end. The app category now follows its tagged name.
/// </summary>
public sealed class CategoryDriftTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-drift-test-" + Guid.NewGuid().ToString("N"));
    public CategoryDriftTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private string Wave(string name)
    {
        var path = Path.Combine(folder, name);
        using (var wave = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) wave.Write(new byte[4800], 0, 4800);
        return path;
    }
    private static int CountNamed(MockSoundpad mock, string name) => XDocument.Parse(mock.CategoryXml).Descendants("Category").Count(e => (string?)e.Attribute("name") == name);

    [Fact]
    public async Task RemovingACategoryAboveTempKeepsImportsWorking()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var temp = new OwnedCategory { Name = "Temp - Soundpad Web [abcdef-123456]" }; int checkpoints = 0;
        int first = await ManagedSoundpad.EnsureCategory(pipe, temp, () => checkpoints++);
        Assert.Equal("created", temp.State);
        // The person removes a category above it in Soundpad: Temp's index moves down by one.
        Assert.StartsWith("R-200", await pipe.Request("DoRemoveCategory(7)"));
        int moved = await ManagedSoundpad.EnsureCategory(pipe, temp, () => checkpoints++);
        Assert.Equal(first - 1, moved); Assert.Equal(moved, temp.NativeId); Assert.Equal(1, CountNamed(mock, temp.Name));
        Assert.Equal(3, checkpoints); // creating, created, rebound
        var file = Wave("clip.wav");
        var imported = await ManagedSoundpad.Import(pipe, file, moved, temp.Name);
        Assert.Equal(moved, imported.CategoryId);
        Assert.Single(mock.Commands, c => c.StartsWith("DoAddCategory("));
    }

    [Fact]
    public async Task RemoveTempCategoryRebindsAfterIndexShift()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var store = new ManagedAudioStore(folder, pipe, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p))).ToLowerInvariant(), async work => await work());
        store.BeginSession();
        var asset = await store.Upload(File.ReadAllBytes(Wave("upload.wav")), "Upload.wav", "Alice", "peer-a", false);
        await store.Prepare(asset.Id, "normal", false);
        var name = store.Public.TempCategoryName!;
        Assert.Equal(1, CountNamed(mock, name));
        // A category above Temp goes in Soundpad, so Temp's index shifts.
        Assert.StartsWith("R-200", await pipe.Request("DoRemoveCategory(7)"));
        Assert.Equal(1, await store.ClearTemporary());
        Assert.Equal(0, CountNamed(mock, name));
        Assert.Equal(0, mock.RiskyRemovals);
    }

    [Fact]
    public async Task TwoMatchesRefuseRebind()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var temp = new OwnedCategory { Name = "Temp - Soundpad Web [abcdef-654321]" };
        await ManagedSoundpad.EnsureCategory(pipe, temp, () => { });
        Assert.StartsWith("R-200", await pipe.Request("DoRemoveCategory(7)"));
        // Someone made a second category with exactly that name: neither is claimed.
        Assert.StartsWith("R-200", await pipe.Request($"DoAddCategory(\"{temp.Name}\", -1)"));
        int? before = temp.NativeId;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ManagedSoundpad.EnsureCategory(pipe, temp, () => { }));
        Assert.Contains("ambiguous", error.Message);
        Assert.Equal(before, temp.NativeId);
        Assert.Equal(2, CountNamed(mock, temp.Name));
    }
}
