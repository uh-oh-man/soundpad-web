using NAudio.Wave;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// 2.44 real-hardware proof: the owner's Soundpad 4.0.35 lists the first file added to a new category a moment after
/// DoAddSound answers (about 100 ms), so an import that read the list once straight after the add failed, and the
/// exact-playback slots never registered in a new session. Imports now re-read for up to about 2 s.
/// </summary>
public sealed class ImportSettleTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-import-settle-test-" + Guid.NewGuid().ToString("N"));
    public ImportSettleTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private string Wave(string name)
    {
        var path = Path.Combine(folder, name);
        using (var wave = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) wave.Write(new byte[4800], 0, 4800);
        return path;
    }

    [Fact]
    public async Task AnImportWaitsForSoundpadToListTheNewFile()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var temp = new OwnedCategory { Name = "Temp - Soundpad Web [abcdef-654321]" };
        int category = await ManagedSoundpad.EnsureCategory(pipe, temp, () => { });
        // The next three library reads after the add still show the list as it was, as the real Soundpad did once.
        mock.ApplyLagReads = 3;
        var file = Wave("first.wav");
        var imported = await ManagedSoundpad.Import(pipe, file, category, temp.Name);
        Assert.Equal(file, imported.Path); Assert.Equal(category, imported.CategoryId);
        var catalog = await ManagedSoundpad.Read(pipe);
        Assert.Single(catalog.Entries, e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(file, catalog.Categories.Single(c => c.Index == category).Paths);
    }

    [Fact]
    public async Task AnImportSoundpadNeverConfirmsStillFailsAfterAboutTwoSeconds()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name, true);
        var temp = new OwnedCategory { Name = "Temp - Soundpad Web [abcdef-765432]" };
        int category = await ManagedSoundpad.EnsureCategory(pipe, temp, () => { });
        mock.ApplyLagReads = 1000; // never catches up
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ManagedSoundpad.Import(pipe, Wave("never.wav"), category, temp.Name));
        Assert.Contains("has not confirmed", error.Message);
        Assert.InRange(clock.ElapsedMilliseconds, 1500, 6000);
    }
}
