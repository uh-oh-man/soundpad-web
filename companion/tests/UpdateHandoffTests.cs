using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>The update hand-over file (2.44, WP2): DPAPI, its checks, and that nothing of it outlives an update.</summary>
public sealed class UpdateHandoffTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-update-handoff-" + Guid.NewGuid().ToString("N"));
    private long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public UpdateHandoffTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private string FilePath => Path.Combine(folder, UpdateHandoffFile.Name);

    private UpdateHandoff Make(string? id = null, int pid = 4242, string? dir = null, long? created = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"), CreatedUtcMs = created ?? now, DataDir = dir ?? folder, OldPid = pid, FromVersion = "2.44.0", ToVersion = "2.44.1",
        Server = new(new string('b', 64), 51234, JsonDocument.Parse("""{"v":1,"room":"r"}""").RootElement.Clone(), null, "{\"type\":\"session\"}", false, "client-1"),
        Bridge = new() { HistorySessionId = new string('c', 32), CounterSession = 3, Drafts = [new("d1", "host", "Host", new string('c', 32), 10, 20, "x", true, 10, 20, 1, 2, null, 0, 0)] },
        Window = new(false, true),
    };
    private UpdateHandoff? Consume(string? id, int? waitPid, Func<int, bool>? running = null) => UpdateHandoffFile.TryConsume(folder, id, waitPid, () => now, running ?? (_ => false), out _);

    [Fact]
    public void TheHandOverRoundTripsThroughDpapiAndIsNotPlainText()
    {
        var handoff = Make();
        UpdateHandoffFile.Write(folder, handoff);
        var raw = File.ReadAllBytes(FilePath);
        Assert.DoesNotContain(new string('b', 64), System.Text.Encoding.UTF8.GetString(raw)); // the token is not readable on disk
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser)); // our entropy is needed
        var read = Consume(handoff.Id, 4242)!;
        Assert.Equal((handoff.Id, handoff.Server.Token, handoff.Server.Port, "client-1", 3, true), (read.Id, read.Server.Token, read.Server.Port, read.Server.OwnerClientId, read.Bridge.CounterSession, read.Window.Minimized));
        Assert.Equal("r", read.Server.Identity!.Value.GetProperty("room").GetString());
        Assert.Equal("d1", Assert.Single(read.Bridge.Drafts).Id);
        // Consumed: renamed, so a second start cannot use it; Finish removes it.
        Assert.False(File.Exists(FilePath)); Assert.True(File.Exists(FilePath + ".consumed"));
        Assert.Null(Consume(handoff.Id, 4242));
        UpdateHandoffFile.Finish(folder);
        Assert.False(File.Exists(FilePath + ".consumed"));
    }

    [Fact]
    public void AStaleForeignOrSuspectHandOverIsRefusedAndDeleted()
    {
        void Refused(UpdateHandoff h, string? id, int? waitPid, Func<int, bool>? running = null)
        {
            UpdateHandoffFile.Write(folder, h);
            Assert.Null(Consume(id, waitPid, running));
            Assert.False(File.Exists(FilePath)); Assert.False(File.Exists(FilePath + ".consumed"));
        }
        Refused(Make(created: now - 5 * 60000 - 1), null, null);                 // older than 5 minutes
        Refused(Make(created: now + 120000), null, null);                        // from the future
        Refused(Make(dir: Path.Combine(folder, "other")), null, null);           // another data folder
        Refused(Make(id: new string('1', 32)), new string('2', 32), 4242);        // another update
        Refused(Make(pid: 777), null, 4242, pid => pid == 777);                  // its writer still runs, and is not the one waiting
        Refused(Make() with { Format = 2 }, null, null);                          // unknown format
        Refused(Make() with { Server = new("short", 1, null, null, null, false, null) }, null, null);
        // Oversize, or not DPAPI at all.
        File.WriteAllBytes(FilePath, new byte[UpdateHandoffFile.MaxFileBytes + 1]);
        Assert.Null(Consume(null, null)); Assert.False(File.Exists(FilePath));
        File.WriteAllText(FilePath, "{\"format\":1}");
        Assert.Null(Consume(null, null)); Assert.False(File.Exists(FilePath));
        // Too much session to hand over is refused when writing.
        Assert.Throws<InvalidDataException>(() => UpdateHandoffFile.Protect(Make() with { Server = new(new string('b', 64), 1, null, null, new string('x', UpdateHandoffFile.MaxJsonBytes), false, null) }));
        // The watchdog itself may still be running: that is the normal case.
        var ok = Make(pid: 777);
        UpdateHandoffFile.Write(folder, ok);
        Assert.NotNull(Consume(ok.Id, 777, pid => pid == 777));
    }

    [Fact]
    public void ANormalStartDeletesWhatAnUpdateLeft()
    {
        var exeFolder = Path.Combine(folder, "app"); Directory.CreateDirectory(exeFolder);
        var exe = Path.Combine(exeFolder, "SoundpadWeb.exe");
        string[] gone = [FilePath, FilePath + ".consumed", Path.Combine(folder, "update-ready." + new string('a', 32)), Path.Combine(folder, "update-child.log"),
            Path.Combine(exeFolder, ".SoundpadWeb.2.44.1.download"), Path.Combine(exeFolder, "SoundpadWeb.failed-2.44.1.exe")];
        string[] kept = [exe, Path.Combine(exeFolder, "SoundpadWeb.previous.exe"), Path.Combine(folder, "update-settings.v1.json"), Path.Combine(folder, "state.json"),
            Path.Combine(folder, "update-ready.notes.txt"), Path.Combine(exeFolder, "SoundpadWeb.failed-notes.exe"), Path.Combine(exeFolder, "other.download")];
        foreach (var f in gone.Concat(kept)) File.WriteAllText(f, "x");
        UpdateFiles.CleanLeftovers(folder, exe);
        foreach (var f in gone) Assert.False(File.Exists(f), f);
        foreach (var f in kept) Assert.True(File.Exists(f), f);
    }
}
