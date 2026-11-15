using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>2.44: why Soundpad can't be reached, as the setup guide and the connection banner say it.</summary>
public sealed class SoundpadDiagnosisTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-diagnosis-test-" + Guid.NewGuid().ToString("N"));
    public SoundpadDiagnosisTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Soundpad as a test wants it: failing with a given exception, or answering through the mock.</summary>
    private sealed class FakeSoundpad(ISoundpad inner) : ISoundpad
    {
        public Func<string, Exception?>? Fail { get; set; }
        public string? CategoriesAnswer { get; set; }
        public Task<string> Request(string command, CancellationToken cancel = default)
        {
            if (Fail?.Invoke(command) is { } error) return Task.FromException<string>(error);
            if (CategoriesAnswer != null && command.StartsWith("GetCategories(")) return Task.FromResult(CategoriesAnswer);
            return inner.Request(command, cancel);
        }
    }

    private async Task<(Bridge Bridge, FakeSoundpad Fake, Action<bool> Running, IAsyncDisposable Mock, IDisposable Pipe)> Start()
    {
        var mock = new MockSoundpad(); var pipe = new SoundpadPipe(mock.Name, true);
        var fake = new FakeSoundpad(pipe);
        bool running = true;
        var bridge = new Bridge(fake, new StateStore(folder));
        bridge.UseSoundpadRunning(() => running);
        await bridge.Poll(true);
        Assert.True(bridge.Snapshot.Available); Assert.Null(bridge.Snapshot.SoundpadProblem);
        return (bridge, fake, value => running = value, mock, pipe);
    }

    [Theory]
    [InlineData("access", true, "accessDenied")]
    [InlineData("access", false, "accessDenied")]
    [InlineData("timeout", false, "notRunning")]
    [InlineData("timeout", true, "noAnswer")]
    [InlineData("closed", false, "notRunning")]
    [InlineData("closed", true, "noAnswer")]
    [InlineData("win32timeout", true, "noAnswer")]
    public async Task FailuresAreClassifiedAndPollNeverThrows(string failure, bool soundpadRuns, string problem)
    {
        var (bridge, fake, running, mock, pipe) = await Start();
        try
        {
            running(soundpadRuns);
            fake.Fail = _ => failure switch
            {
                "access" => new UnauthorizedAccessException("Access to the path is denied."),
                "timeout" => new OperationCanceledException(),
                "win32timeout" => new TimeoutException(),
                _ => new IOException("Soundpad closed the pipe."),
            };
            await bridge.Poll(true); // must not throw (2.43 let UnauthorizedAccessException escape Poll)
            Assert.False(bridge.Snapshot.Available);
            Assert.Equal(problem, bridge.Snapshot.SoundpadProblem);
            // The error text is what older versions showed.
            Assert.Equal("Soundpad is unavailable. Open it normally; reconnection is automatic.", bridge.Snapshot.Error);
            // Recovery clears the problem with no extra step.
            fake.Fail = null;
            await bridge.Poll(true);
            Assert.True(bridge.Snapshot.Available); Assert.Null(bridge.Snapshot.SoundpadProblem);
            Assert.DoesNotContain("soundpadProblem\":\"", Json.Write(bridge.Snapshot));
        }
        finally { bridge.Dispose(); pipe.Dispose(); await mock.DisposeAsync(); }
    }

    [Fact]
    public async Task AnOldSoundpadWithoutCategoriesIsOldVersion()
    {
        var (bridge, fake, _, mock, pipe) = await Start();
        try
        {
            fake.CategoriesAnswer = "R-404";
            await bridge.Poll(true);
            Assert.False(bridge.Snapshot.Available);
            Assert.Equal("oldVersion", bridge.Snapshot.SoundpadProblem);
            Assert.Equal(SoundpadDiagnosis.OldVersionMessage, bridge.Snapshot.Error);
            fake.CategoriesAnswer = null;
            await bridge.Poll(true);
            Assert.Null(bridge.Snapshot.SoundpadProblem);
        }
        finally { bridge.Dispose(); pipe.Dispose(); await mock.DisposeAsync(); }
    }

    [Fact]
    public async Task ADroppedConnectionWhileSamplingPlaybackIsClassifiedToo()
    {
        var (bridge, fake, running, mock, pipe) = await Start();
        try
        {
            running(true);
            fake.Fail = command => command.StartsWith("GetPlay") ? new UnauthorizedAccessException() : null;
            await bridge.Poll(false);
            Assert.False(bridge.Snapshot.Available);
            Assert.Equal("accessDenied", bridge.Snapshot.SoundpadProblem);
            Assert.Equal("Soundpad disconnected; reconnecting automatically.", bridge.Snapshot.Error);
        }
        finally { bridge.Dispose(); pipe.Dispose(); await mock.DisposeAsync(); }
    }

    [Fact]
    public void ClassifierAsksWhetherSoundpadRunsOnlyWhenItMatters()
    {
        int asked = 0;
        var diagnosis = new SoundpadDiagnosis(() => { asked++; return false; });
        Assert.Equal("accessDenied", diagnosis.Classify(new UnauthorizedAccessException()));
        Assert.Equal("oldVersion", diagnosis.Classify(new InvalidDataException(SoundpadDiagnosis.OldVersionMessage)));
        Assert.Equal(0, asked);
        Assert.Equal("notRunning", diagnosis.Classify(new IOException()));
        Assert.Equal("notRunning", diagnosis.Classify(new System.Xml.XmlException()));
        Assert.Equal(2, asked);
    }
}
