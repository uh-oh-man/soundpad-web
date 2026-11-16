using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>2.44 (gate missingFiles): sounds whose file is missing are marked, never sent to Soundpad to play, and guests see no paths.</summary>
public sealed class MissingFilesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-missing-test-" + Guid.NewGuid().ToString("N"));
    public MissingFilesTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op, string? sound = null, string? version = null) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op, sound, version);
    private string Wave(string name)
    {
        var path = Path.Combine(folder, name);
        using (var wave = new WaveFileWriter(path, new WaveFormat(48000, 16, 2))) wave.Write(new byte[4800], 0, 4800);
        return path;
    }
    private MockSoundpad Mock(out string a, out string b)
    {
        a = Wave("present.wav"); b = Wave("gone.wav");
        return new MockSoundpad
        {
            AllXml = $"<Soundlist><Sound index=\"1\" url=\"{a}\" title=\"Present\"/><Sound index=\"2\" url=\"{b}\" title=\"Gone\"/></Soundlist>",
            CategoryXml = $"<Categories><Category index=\"2\" name=\"Mixed\"><Sound index=\"1\" url=\"{a}\"/><Sound index=\"2\" url=\"{b}\"/></Category></Categories>",
        };
    }

    [Fact]
    public async Task ADeletedFileIsMarkedMissingAndItsPlayIsRefused()
    {
        await using var mock = Mock(out _, out var gone); using var pipe = new SoundpadPipe(mock.Name, true);
        using var bridge = new Bridge(pipe, new StateStore(folder)); bridge.UseFileExists(File.Exists);
        await bridge.Poll(true); await bridge.MissingScanIdle();
        Assert.DoesNotContain(bridge.Snapshot.Library.Sounds, s => s.Missing);
        var version = bridge.Snapshot.Library.Version;
        File.Delete(gone);
        bridge.RescanMissingFiles(); await bridge.MissingScanIdle();
        var sound = Assert.Single(bridge.Snapshot.Library.Sounds, s => s.Missing);
        Assert.Equal("Gone", sound.Name);
        Assert.NotEqual(version, bridge.Snapshot.Library.Version); // pages that cache the library by version see the mark
        foreach (var op in new[] { "play", "startAt", "region" })
        {
            var reply = await bridge.Execute(Cmd(op, sound.Id, bridge.Snapshot.Library.Version) with { PositionMs = 0, EndMs = 100 }, "guest");
            Assert.False(reply.Ok); Assert.Contains("missing on the host's PC", reply.Error);
        }
        Assert.DoesNotContain(mock.Commands, c => c.StartsWith("DoPlaySound("));
        // The snapshot carries the mark, never a path; only true marks are written.
        var json = Json.Write(bridge.Snapshot);
        Assert.DoesNotContain(folder.Replace("\\", "\\\\"), json);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"missing\":true"));
        Assert.DoesNotContain("\"missing\":false", json);
        // It comes back: the mark goes.
        Wave("gone.wav"); bridge.RescanMissingFiles(); await bridge.MissingScanIdle();
        Assert.DoesNotContain(bridge.Snapshot.Library.Sounds, s => s.Missing);
        Assert.True((await bridge.Execute(Cmd("play", sound.Id, bridge.Snapshot.Library.Version), "host")).Ok);
    }

    [Fact]
    public async Task ASlowFileCheckIsUnknownNotMissingAndNeverStallsPlayback()
    {
        await using var mock = Mock(out _, out _); using var pipe = new SoundpadPipe(mock.Name, true);
        using var bridge = new Bridge(pipe, new StateStore(folder));
        var release = new ManualResetEventSlim();
        // The scan's own clock starts when it first asks about a file, which happens inside Poll (2.45 CI: timed from
        // after Poll, a slow runner had already spent part of the 2 s limit and the scan looked too short).
        Stopwatch? scanClock = null;
        bridge.UseFileExists(path => { Interlocked.CompareExchange(ref scanClock, Stopwatch.StartNew(), null); release.Wait(TimeSpan.FromSeconds(8)); return false; });
        await bridge.Poll(true);
        var scan = bridge.MissingScanIdle();
        // A play during the scan is not held up by it.
        var clock = Stopwatch.StartNew();
        Assert.True((await bridge.Execute(Cmd("play", bridge.Snapshot.Library.Sounds[0].Id, bridge.Snapshot.Library.Version), "host")).Ok);
        Assert.True(clock.ElapsedMilliseconds < 1500, $"play took {clock.ElapsedMilliseconds} ms during the scan");
        await scan.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.NotNull(scanClock);
        Assert.True(scanClock!.ElapsedMilliseconds >= MissingFiles.PathTimeoutMs - 200, $"the scan gave up after {scanClock.ElapsedMilliseconds} ms");
        // Past the 2 s limit: unknown, so nothing is marked missing.
        Assert.DoesNotContain(bridge.Snapshot.Library.Sounds, s => s.Missing);
        release.Set();
    }

    [Fact]
    public async Task GuestsSeeTheMarkButNoPathAndRealPipesAlwaysScan()
    {
        await using var mock = Mock(out _, out var gone); using var pipe = new SoundpadPipe(mock.Name, true);
        File.Delete(gone);
        using var bridge = new Bridge(pipe, new StateStore(folder));
        // Test Soundpads list made-up paths: without a file check, nothing is scanned.
        await bridge.Poll(true); await bridge.MissingScanIdle();
        Assert.DoesNotContain(bridge.Snapshot.Library.Sounds, s => s.Missing);
        bridge.UseFileExists(File.Exists); bridge.RescanMissingFiles(); await bridge.MissingScanIdle();
        Assert.Single(bridge.Snapshot.Library.Sounds, s => s.Missing);
        var guest = new Participant(Guid.NewGuid().ToString(), "Friend");
        var view = await bridge.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "organizeGet"), "guest", guest);
        Assert.False(view.Ok); // proposals are off: guests get nothing of the plan
        Assert.DoesNotContain("gone.wav", Json.Write(bridge.Snapshot), StringComparison.OrdinalIgnoreCase);
    }
}
