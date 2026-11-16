using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// 2.45 (capacity benchmark, D13): the host page relays every guest's queries over its one socket. Each relayed guest
/// now has its own query budget, so twelve busy guests no longer use up the host page's.
/// </summary>
public sealed class CapacityTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-capacity-test-" + Guid.NewGuid().ToString("N"));
    public CapacityTests() => Directory.CreateDirectory(folder);
    public void Dispose() => TestFolders.Delete(folder);
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
    private const string Limited = "Too many requests. Try again shortly.";

    private static async Task<ClientWebSocket> Connect(LocalServer server)
    {
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", "https://example.test");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/bridge"), CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes(Json.Write(new { token = server.Token, protocol = 2 })), WebSocketMessageType.Text, true, CancellationToken.None);
        return socket;
    }
    private static async Task<JsonElement> Next(ClientWebSocket socket, CancellationToken cancel)
    {
        using var data = new MemoryStream(); var buffer = new byte[16384]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer, cancel); data.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(data.ToArray());
    }
    private static Task Send(ClientWebSocket socket, string actor, Command command, string? requester = null) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(requester == null ? Json.Write(new { actor, command }) : Json.Write(new { actor, command, requester })), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Sends every (actor, requester) query at once and counts, per sender, how many were refused as too many.</summary>
    private static async Task<Dictionary<string, int>> LimitedPerSender(ClientWebSocket socket, (string Actor, string? Requester, int Count)[] senders)
    {
        var owner = new Dictionary<string, string>();
        foreach (var (actor, requester, count) in senders)
            for (int i = 0; i < count; i++)
            {
                var c = Cmd("statistics"); owner[c.Id] = requester ?? actor;
                await Send(socket, actor, c, requester);
            }
        var limited = senders.ToDictionary(s => s.Requester ?? s.Actor, _ => 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int seen = 0;
        while (seen < owner.Count)
        {
            var message = await Next(socket, timeout.Token);
            if (message.GetProperty("type").GetString() != "ack" || !owner.TryGetValue(message.GetProperty("id").GetString()!, out var who)) continue;
            seen++;
            if (!message.GetProperty("ok").GetBoolean() && message.GetProperty("error").GetString() == Limited) limited[who]++;
        }
        return limited;
    }

    [Fact]
    public async Task TwelveRelayedGuestsEachHaveTheirOwnQueryBudgetBesideTheHostPages()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        await using var server = new LocalServer(bridge, "https://example.test"); await server.Start();
        using var socket = await Connect(server);
        // Twelve guests at their host page's own limit (12 a second each), and the host page's own 30: none refused.
        var senders = Enumerable.Range(1, 12).Select(i => ("guest", (string?)$"peer-{i}", 12)).Append(("host", null, 30)).ToArray();
        var limited = await LimitedPerSender(socket, senders);
        Assert.All(limited, pair => Assert.Equal(0, pair.Value));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task ARunawayGuestIsLimitedAloneAndUnnamedGuestQueriesKeepTheSharedLimit()
    {
        await using var mock = new MockSoundpad(); using var pipe = new SoundpadPipe(mock.Name); using var bridge = new Bridge(pipe, new StateStore(folder)); await bridge.Poll(true);
        await using var server = new LocalServer(bridge, "https://example.test"); await server.Start();
        using var socket = await Connect(server);
        var limited = await LimitedPerSender(socket, [("guest", "runaway", 60), ("guest", "calm", 10)]);
        Assert.True(limited["runaway"] >= 60 - LocalServer.GuestQueriesPerSecond - 4, $"only {limited["runaway"]} of 60 were limited");
        Assert.Equal(0, limited["calm"]);
        // A guest command without a requester (no host page sends one) shares the host page's budget, as before.
        var shared = await LimitedPerSender(socket, [("guest", null, 60)]);
        Assert.True(shared["guest"] >= 60 - LocalServer.QueriesPerSecond - 4, $"only {shared["guest"]} of 60 were limited");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
