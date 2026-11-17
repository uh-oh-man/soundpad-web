using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>Spotify (2.46): a guest's press reaches Spotify even while someone's status read is under way.</summary>
public sealed class SpotifyTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-spotify-" + Guid.NewGuid().ToString("N"));
    public SpotifyTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Spotify(string action) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "spotify") { Action = action };
    private static SpotifyState State(Reply reply) { Assert.True(reply.Ok, reply.Error); return Assert.IsType<SpotifyState>(reply.Data); }

    private async Task<(MockSoundpad, SoundpadPipe, Bridge, SpotifyFixture)> Start()
    {
        var mock = new MockSoundpad(); var pipe = new SoundpadPipe(mock.Name, true);
        var bridge = new Bridge(pipe, new StateStore(folder)); var fixture = new SpotifyFixture(); bridge.UseSpotify(fixture);
        await bridge.Poll(true);
        Assert.True((await bridge.Execute(Spotify("enable"), "host")).Ok);
        return (mock, pipe, bridge, fixture);
    }

    [Fact]
    public async Task AGuestPressDuringAStatusReadWaitsItsTurnAndPresses()
    {
        var (mock, pipe, bridge, fixture) = await Start();
        await using var _m = mock; using var _p = pipe; using var _b = bridge;
        fixture.StatusDelayMs = 400;
        var poll = bridge.Execute(Spotify("status"), "host");
        await Task.Delay(50); // the host's poll is under way
        var press = State(await bridge.Execute(Spotify("toggle"), "guest"));
        Assert.True(press.Available, press.Error);
        Assert.Equal("Playing", press.Status);
        Assert.Equal(["toggle"], fixture.Presses);
        Assert.True(State(await poll).Available);
    }

    [Fact]
    public async Task AStatusReadDuringAnotherAnswersWithTheLastStateNotBusy()
    {
        var (mock, pipe, bridge, fixture) = await Start();
        await using var _m = mock; using var _p = pipe; using var _b = bridge;
        Assert.Equal("Fixture song", State(await bridge.Execute(Spotify("status"), "host")).Title);
        fixture.StatusDelayMs = 1000;
        var slow = bridge.Execute(Spotify("status"), "host");
        await Task.Delay(50);
        var other = State(await bridge.Execute(Spotify("status"), "guest"));
        Assert.True(other.Available, other.Error);
        Assert.Equal("Fixture song", other.Title);
        await slow;
    }

    [Fact]
    public async Task AGuestPressIsRefusedWithoutPressingWhenTheHostTurnedGuestSpotifyOff()
    {
        var (mock, pipe, bridge, fixture) = await Start();
        await using var _m = mock; using var _p = pipe; using var _b = bridge;
        Assert.True((await bridge.Execute(new Command(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "permissions") { Permissions = new(GuestSpotify: false) }, "host")).Ok);
        var refused = await bridge.Execute(Spotify("next"), "guest");
        Assert.False(refused.Ok);
        Assert.Equal("Guest Spotify controls are disabled.", refused.Error);
        Assert.True(State(await bridge.Execute(Spotify("status"), "guest")).Available); // a guest still sees what plays
        Assert.Empty(fixture.Presses);
        Assert.True(State(await bridge.Execute(Spotify("next"), "host")).Available);
        Assert.Equal(["next"], fixture.Presses);
    }

    private static readonly Participant Alice = new("11111111-1111-1111-1111-111111111111", "Alice");
    private static readonly Participant Bob = new("22222222-2222-2222-2222-222222222222", "Bob");
    private static Command Permissions(bool guestSpotify) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "permissions") { Permissions = new(GuestSpotify: guestSpotify) };

    [Fact]
    public async Task AWaitingGuestPressIsAskedAgainWhenItsTurnComes()
    {
        var (mock, pipe, bridge, fixture) = await Start();
        await using var _m = mock; using var _p = pipe; using var _b = bridge;
        fixture.StatusDelayMs = 600;
        var poll = bridge.Execute(Spotify("status"), "host");
        await Task.Delay(50);
        var press = bridge.Execute(Spotify("toggle"), "guest", Alice, "peer-a");
        await Task.Delay(100);
        // The host turns guest Spotify off while Alice's press waits for the status read.
        Assert.True((await bridge.Execute(Permissions(false), "host")).Ok);
        var refused = await press;
        Assert.False(refused.Ok);
        Assert.Equal("Guest Spotify controls are disabled.", refused.Error);
        Assert.Empty(fixture.Presses);
        await poll;
    }

    [Fact]
    public async Task RepeatedPressesOfOneButtonWhileTheyWaitArePressedOnce()
    {
        var (mock, pipe, bridge, fixture) = await Start();
        await using var _m = mock; using var _p = pipe; using var _b = bridge;
        fixture.StatusDelayMs = 600;
        var poll = bridge.Execute(Spotify("status"), "host");
        await Task.Delay(50);
        // Alice presses Play/Pause three times while the read is under way; Bob once.
        var presses = new[]
        {
            bridge.Execute(Spotify("toggle"), "guest", Alice, "peer-a"), bridge.Execute(Spotify("toggle"), "guest", Alice, "peer-a"),
            bridge.Execute(Spotify("toggle"), "guest", Alice, "peer-a"), bridge.Execute(Spotify("next"), "guest", Alice, "peer-a"),
            bridge.Execute(Spotify("toggle"), "guest", Bob, "peer-b"),
        };
        foreach (var reply in await Task.WhenAll(presses)) Assert.True(State(reply).Available, reply.Error);
        // Until 2.47 each waited and then pressed: three toggles played, paused and played again.
        Assert.Equal(2, fixture.Presses.Count(p => p == "toggle")); // one for Alice, one for Bob
        Assert.Equal(1, fixture.Presses.Count(p => p == "next"));
        await poll;
        // Once a press went, pressing again is a new press.
        Assert.True(State(await bridge.Execute(Spotify("toggle"), "guest", Alice, "peer-a")).Available);
        Assert.Equal(3, fixture.Presses.Count(p => p == "toggle"));
    }
}
