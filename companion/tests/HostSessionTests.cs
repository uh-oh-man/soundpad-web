using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoundpadWeb.Testing;
using Xunit;
namespace SoundpadWeb.Tests;

/// <summary>
/// 2.36: several host pages on the loopback socket. One protocol-3 page owns the guest session (the window's key
/// wins), the rest are views; the owner's identity and session summary are relayed, and protocol-2 pages behave as in 2.35.
/// All over real loopback WebSockets, like LoopbackRequiresOriginTokenAndAcceptsAuthenticatedCommands.
/// </summary>
public sealed class HostSessionTests : IDisposable
{
    private const string Site = "https://example.test";
    private static readonly string Key = new('a', 64);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "soundpad-host-session-test-" + Guid.NewGuid().ToString("N"));
    public HostSessionTests() => Directory.CreateDirectory(folder);
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    private static Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op, Delta: 1);

    private sealed class Companion : IAsyncDisposable
    {
        private readonly MockSoundpad mock = new();
        private readonly SoundpadPipe pipe;
        private readonly Bridge bridge;
        public LocalServer Server { get; }
        public List<string> Log { get; } = [];
        public Companion(string folder, string? ownerKey = null, int graceMs = 3000)
        {
            pipe = new SoundpadPipe(mock.Name); bridge = new Bridge(pipe, new StateStore(folder));
            Server = new LocalServer(bridge, Site, ownerKey) { OwnerGrace = TimeSpan.FromMilliseconds(graceMs), Log = line => { lock (Log) Log.Add(line); } };
        }
        public async Task<Companion> Start() { await bridge.Poll(true); await Server.Start(); return this; }
        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); bridge.Dispose(); pipe.Dispose(); await mock.DisposeAsync(); }
    }

    /// <summary>A host page: reads every message in the background so tests can wait for one type without losing others.</summary>
    private sealed class Page : IAsyncDisposable
    {
        public ClientWebSocket Socket { get; } = new();
        private readonly List<JsonElement> inbox = [];
        private readonly SemaphoreSlim arrived = new(0);
        private Task? reader;
        public static async Task<Page> Open(LocalServer server, object hello)
        {
            var page = new Page(); page.Socket.Options.SetRequestHeader("Origin", Site);
            await page.Socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/bridge"), CancellationToken.None);
            await page.Send(hello);
            page.reader = Task.Run(page.Read);
            return page;
        }
        public static Task<Page> Host(LocalServer server, string clientId, string? ownerKey = null) => Open(server, new { token = server.Token, protocol = 3, clientId, ownerKey });
        private async Task Read()
        {
            try
            {
                while (Socket.State == WebSocketState.Open)
                {
                    using var data = new MemoryStream(); var buffer = new byte[16384]; WebSocketReceiveResult result;
                    do { result = await Socket.ReceiveAsync(buffer, CancellationToken.None); data.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    lock (inbox) inbox.Add(JsonSerializer.Deserialize<JsonElement>(data.ToArray()));
                    arrived.Release();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException) { }
            arrived.Release();
        }
        /// <summary>The first message of any type, in arrival order (removes it).</summary>
        public async Task<JsonElement> First(int ms = 5000) => await Take(_ => true, ms) ?? throw new TimeoutException("No message.");
        /// <summary>The next message of <paramref name="type"/>, or null when none arrives within <paramref name="ms"/>.</summary>
        public Task<JsonElement?> Maybe(string type, int ms) => Take(m => m.GetProperty("type").GetString() == type, ms);
        public async Task<JsonElement> Next(string type, int ms = 5000) => await Maybe(type, ms) ?? throw new TimeoutException("No " + type + " message.");
        private async Task<JsonElement?> Take(Func<JsonElement, bool> wanted, int ms)
        {
            var deadline = Environment.TickCount64 + ms;
            while (true)
            {
                lock (inbox) { int at = inbox.FindIndex(m => wanted(m)); if (at >= 0) { var found = inbox[at]; inbox.RemoveAt(at); return found; } }
                var left = deadline - Environment.TickCount64;
                if (left <= 0 || !await arrived.WaitAsync(TimeSpan.FromMilliseconds(left))) return null;
            }
        }
        public Task Send(object value) => Socket.SendAsync(Encoding.UTF8.GetBytes(Json.Write(value)), WebSocketMessageType.Text, true, CancellationToken.None);
        public async Task Close() { if (Socket.State == WebSocketState.Open) await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); if (reader != null) await reader; }
        public async ValueTask DisposeAsync() { try { await Close(); } catch (WebSocketException) { } Socket.Dispose(); }
    }

    private static string Role(JsonElement message) { Assert.Equal("role", message.GetProperty("type").GetString()); return message.GetProperty("role").GetString()!; }
    private static JsonElement? Identity(JsonElement role) => role.TryGetProperty("identity", out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    [Fact]
    public async Task WithoutAWindowTheFirstProtocol3PageOwnsAndLaterPagesView()
    {
        await using var companion = await new Companion(folder).Start();
        await using var first = await Page.Host(companion.Server, "first");
        // The role comes before anything else, then the usual full state.
        var role = await first.First();
        Assert.Equal("owner", Role(role)); Assert.Null(Identity(role));
        Assert.Equal("state", (await first.First()).GetProperty("type").GetString());
        await using var second = await Page.Host(companion.Server, "second");
        Assert.Equal("view", Role(await second.First()));
        Assert.Equal("state", (await second.First()).GetProperty("type").GetString());
        // Both are the host: either may send commands.
        var counter = Cmd("counter");
        await second.Send(new { actor = "host", command = counter });
        var ack = await second.Next("ack"); Assert.Equal(counter.Id, ack.GetProperty("id").GetString()); Assert.True(ack.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TheWindowsOwnerKeyTakesOverAndDemotesTheEarlierOwner()
    {
        await using var companion = await new Companion(folder, Key).Start();
        // The window's page has not connected yet (it is still loading), so a tab gets the session for now.
        await using var tab = await Page.Host(companion.Server, "tab");
        Assert.Equal("owner", Role(await tab.Next("role")));
        await tab.Send(new { type = "identity", identity = new { room = "room-1", secret = "s1" } }); await Task.Delay(100);
        await using var stranger = await Page.Host(companion.Server, "stranger", new string('b', 64));
        Assert.Equal("view", Role(await stranger.Next("role")));
        await using var window = await Page.Host(companion.Server, "window", Key);
        var taken = await window.Next("role");
        Assert.Equal("owner", Role(taken));
        // The window keeps the tab's identity, so guests' invitations keep working.
        Assert.Equal("room-1", Identity(taken)!.Value.GetProperty("room").GetString());
        Assert.Equal("view", Role(await tab.Next("role")));
        Assert.Null(await stranger.Maybe("role", 300));
        lock (companion.Log) Assert.Contains(companion.Log, line => line.Contains("took over"));
    }

    [Fact]
    public async Task AReloadedOwnerKeepsItsPlaceAndGetsTheSameIdentityBack()
    {
        await using var companion = await new Companion(folder).Start();
        Assert.Equal(TimeSpan.FromSeconds(3), new LocalServer(null!, Site).OwnerGrace);
        var owner = await Page.Host(companion.Server, "owner-page");
        Assert.Equal("owner", Role(await owner.Next("role")));
        await owner.Send(new { type = "identity", identity = new { room = "room-1", secret = "s1", key = new { x = "1" } } });
        await using var view = await Page.Host(companion.Server, "view-page");
        Assert.Equal("view", Role(await view.Next("role")));
        // A view cannot replace the identity.
        await view.Send(new { type = "identity", identity = new { room = "evil" } });
        await Task.Delay(200);
        await owner.DisposeAsync();
        await Task.Delay(400);
        await using var reloaded = await Page.Host(companion.Server, "owner-page");
        var role = await reloaded.Next("role");
        Assert.Equal("owner", Role(role));
        Assert.Equal("room-1", Identity(role)!.Value.GetProperty("room").GetString());
        Assert.Equal("1", Identity(role)!.Value.GetProperty("key").GetProperty("x").GetString());
        // The grace ends with an owner in place: the view is never promoted.
        Assert.Null(await view.Maybe("role", 3500));
    }

    [Fact]
    public async Task WhenTheOwnerDoesNotComeBackTheOldestViewIsPromoted()
    {
        await using var companion = await new Companion(folder, graceMs: 600).Start();
        var owner = await Page.Host(companion.Server, "owner-page");
        Assert.Equal("owner", Role(await owner.Next("role")));
        await owner.Send(new { type = "identity", identity = new { room = "room-1" } });
        await using var older = await Page.Host(companion.Server, "older");
        await using var newer = await Page.Host(companion.Server, "newer");
        Assert.Equal("view", Role(await older.Next("role"))); Assert.Equal("view", Role(await newer.Next("role")));
        await Task.Delay(100);
        var gone = Environment.TickCount64;
        await owner.DisposeAsync();
        // A different page arriving during the grace waits as a view; the place is held for the owner.
        await using var during = await Page.Host(companion.Server, "during");
        Assert.Equal("view", Role(await during.Next("role")));
        var promoted = await older.Next("role", 5000);
        Assert.Equal("owner", Role(promoted));
        Assert.InRange(Environment.TickCount64 - gone, 450, 4000);
        Assert.Equal("room-1", Identity(promoted)!.Value.GetProperty("room").GetString());
        Assert.Null(await newer.Maybe("role", 300)); Assert.Null(await during.Maybe("role", 10));
    }

    [Fact]
    public async Task EndHostingForgetsTheInvitationAndPromotesNoViewIntoIt()
    {
        // 2.36 integration: End hosting revokes every guest, so its invitation must not come back through a reload,
        // a promoted view or Resume hosting (the e2e "end and resume" test caught the same invitation coming back).
        await using var companion = await new Companion(folder, graceMs: 600).Start();
        var owner = await Page.Host(companion.Server, "owner-page");
        Assert.Equal("owner", Role(await owner.Next("role")));
        await owner.Send(new { type = "identity", identity = new { room = "room-1" } });
        await owner.Send(new { type = "session", invite = "https://example.test/#join=old", guests = new[] { "peer-a" }, people = new { }, layouts = Array.Empty<object>() });
        await using var view = await Page.Host(companion.Server, "view-page");
        Assert.Equal("view", Role(await view.Next("role")));
        Assert.Equal("https://example.test/#join=old", (await view.Next("session")).GetProperty("invite").GetString());
        var end = Cmd("endSession");
        await owner.Send(new { actor = "host", command = end });
        Assert.Equal(end.Id, (await owner.Next("ack", 10000)).GetProperty("id").GetString());
        // The views are told the session is empty, and a late summary or identity from the ended owner is ignored.
        var emptied = await view.Next("session");
        Assert.Equal("", emptied.GetProperty("invite").GetString()); Assert.Equal(0, emptied.GetProperty("guests").GetArrayLength());
        await owner.Send(new { type = "session", invite = "https://example.test/#join=old", guests = Array.Empty<string>(), people = new { }, layouts = Array.Empty<object>() });
        await owner.Send(new { type = "identity", identity = new { room = "room-1" } });
        Assert.Null(await view.Maybe("session", 300));
        await owner.DisposeAsync();
        // No view is promoted into the ended session, even after the grace.
        Assert.Null(await view.Maybe("role", 1200));
        // The next page (Resume hosting is a new connection) owns at once and gets no identity: a fresh invitation.
        await using var resumed = await Page.Host(companion.Server, "resumed-page");
        var role = await resumed.Next("role", 1000);
        Assert.Equal("owner", Role(role)); Assert.Null(Identity(role));
    }

    private static JsonElement? Settings(JsonElement role) => role.TryGetProperty("settings", out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    [Fact]
    public async Task SessionSettingsFollowTheOwnerLikeTheIdentityAndEndHostingForgetsThem()
    {
        // 2.37: admission, guest places and per-guest permissions survive an owner reload and a hand-over to a view.
        await using var companion = await new Companion(folder, graceMs: 600).Start();
        var owner = await Page.Host(companion.Server, "owner-page");
        Assert.Null(Settings(await owner.Next("role")));
        await owner.Send(new { type = "sessionSettings", settings = new { admission = "ask", capacity = 3 } });
        await using var view = await Page.Host(companion.Server, "view-page");
        Assert.Null(Settings(await view.Next("role"))); // a view is never given them
        // A view cannot replace them, a non-object is ignored, and so is anything above 64 KiB.
        await view.Send(new { type = "sessionSettings", settings = new { admission = "open" } });
        await owner.Send(new { type = "sessionSettings", settings = "not an object" });
        await owner.Send(new { type = "sessionSettings", settings = new { admission = "huge", padding = new string('x', LocalServer.MaxSessionSettingsBytes) } });
        await Task.Delay(200);
        await owner.DisposeAsync();
        await Task.Delay(100);
        // The reloaded owner gets them back next to the identity.
        var reloaded = await Page.Host(companion.Server, "owner-page");
        var role = await reloaded.Next("role");
        Assert.Equal("owner", Role(role));
        Assert.Equal("ask", Settings(role)!.Value.GetProperty("admission").GetString());
        Assert.Equal(3, Settings(role)!.Value.GetProperty("capacity").GetInt32());
        await reloaded.Send(new { type = "sessionSettings", settings = new { admission = "ask", capacity = 4 } });
        await Task.Delay(100);
        // The owner does not come back: the view is promoted and gets the latest settings.
        await reloaded.DisposeAsync();
        var promoted = await view.Next("role", 5000);
        Assert.Equal("owner", Role(promoted));
        Assert.Equal(4, Settings(promoted)!.Value.GetProperty("capacity").GetInt32());
        // End hosting forgets them: the next owner starts with the defaults.
        var end = Cmd("endSession");
        await view.Send(new { actor = "host", command = end });
        Assert.Equal(end.Id, (await view.Next("ack", 10000)).GetProperty("id").GetString());
        await view.Send(new { type = "sessionSettings", settings = new { admission = "late" } });
        await view.Close();
        await using var resumed = await Page.Host(companion.Server, "resumed-page");
        var fresh = await resumed.Next("role", 2000);
        Assert.Equal("owner", Role(fresh)); Assert.Null(Settings(fresh)); Assert.Null(Identity(fresh));
    }

    [Fact]
    public async Task SessionSummariesReachViewsAndLateJoinersButOnlyFromTheOwner()
    {
        await using var companion = await new Companion(folder).Start();
        await using var owner = await Page.Host(companion.Server, "owner-page");
        Assert.Equal("owner", Role(await owner.Next("role")));
        await using var view = await Page.Host(companion.Server, "view-page");
        Assert.Equal("view", Role(await view.Next("role")));
        await owner.Send(new { type = "session", invite = "https://example.test/#join=one", guests = new[] { "peer-1" }, people = new Dictionary<string, object> { ["peer-1"] = new { id = "p1", name = "Friend" } }, layouts = Array.Empty<object>() });
        var relayed = await view.Next("session");
        Assert.Equal("https://example.test/#join=one", relayed.GetProperty("invite").GetString());
        Assert.Equal("Friend", relayed.GetProperty("people").GetProperty("peer-1").GetProperty("name").GetString());
        Assert.Null(await owner.Maybe("session", 300)); // never echoed to the owner
        // A view's session message is not the session: nothing is relayed.
        await view.Send(new { type = "session", invite = "https://evil.test/", guests = Array.Empty<string>(), people = new { }, layouts = Array.Empty<object>() });
        await using var late = await Page.Host(companion.Server, "late-page");
        Assert.Equal("view", Role(await late.First()));
        // A late joiner gets the latest summary right after its role.
        var first = await late.First();
        Assert.Equal("session", first.GetProperty("type").GetString());
        Assert.Equal("https://example.test/#join=one", first.GetProperty("invite").GetString());
        await owner.Send(new { type = "session", invite = "https://example.test/#join=two", guests = Array.Empty<string>(), people = new { }, layouts = Array.Empty<object>() });
        Assert.Equal("https://example.test/#join=two", (await late.Next("session")).GetProperty("invite").GetString());
        Assert.Equal("https://example.test/#join=two", (await view.Next("session")).GetProperty("invite").GetString());
    }

    [Fact]
    public async Task OwnerActionsFromViewsAreForwardedToTheOwnerOnly()
    {
        await using var companion = await new Companion(folder).Start();
        await using var owner = await Page.Host(companion.Server, "owner-page");
        Assert.Equal("owner", Role(await owner.Next("role")));
        await using var view = await Page.Host(companion.Server, "view-page");
        await using var other = await Page.Host(companion.Server, "other-page");
        Assert.Equal("view", Role(await view.Next("role"))); Assert.Equal("view", Role(await other.Next("role")));
        await view.Send(new { type = "ownerAction", action = "explode" });            // not an action: dropped
        await view.Send(new { type = "ownerAction", action = "disconnect", peer = "peer-1" });
        await owner.Send(new { type = "ownerAction", action = "rotate" });             // the owner acts itself: dropped
        await other.Send(new { type = "ownerAction", action = "rotate" });
        // Two views are two sockets, so their actions may arrive in either order.
        var actions = new[] { await owner.Next("ownerAction"), await owner.Next("ownerAction") };
        var disconnect = Assert.Single(actions, a => a.GetProperty("action").GetString() == "disconnect");
        Assert.Equal("peer-1", disconnect.GetProperty("peer").GetString());
        Assert.Single(actions, a => a.GetProperty("action").GetString() == "rotate");
        Assert.Null(await owner.Maybe("ownerAction", 300));
        Assert.Null(await view.Maybe("ownerAction", 10)); Assert.Null(await other.Maybe("ownerAction", 10));
    }

    [Fact]
    public async Task Protocol2PagesWorkAsBeforeAndNoLongerShutEachOtherOut()
    {
        await using var companion = await new Companion(folder, Key).Start();
        // 2.35 accepted one page and answered a second with 409; since 2.36 a second page is accepted too.
        await using var first = await Page.Open(companion.Server, new { token = companion.Server.Token, protocol = 2 });
        await using var second = await Page.Open(companion.Server, new { token = companion.Server.Token, protocol = 2 });
        foreach (var page in new[] { first, second })
        {
            Assert.Equal("state", (await page.First()).GetProperty("type").GetString()); // never a role message
            var counter = Cmd("counter");
            await page.Send(new { actor = "host", command = counter });
            Assert.True((await page.Next("ack")).GetProperty("ok").GetBoolean());
            Assert.Null(await page.Maybe("role", 10));
        }
        lock (companion.Log) Assert.Contains(companion.Log, line => line.Contains("older website"));
        // A protocol-3 page next to them is arbitrated among protocol-3 pages only.
        await using var modern = await Page.Host(companion.Server, "modern");
        Assert.Equal("owner", Role(await modern.First()));
    }

    [Fact]
    public async Task ANinthPageIsRefusedAndTheListenerStaysLoopbackOnly()
    {
        await using var companion = await new Companion(folder).Start();
        var endpoints = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == companion.Server.Port).ToArray();
        Assert.NotEmpty(endpoints); Assert.All(endpoints, e => Assert.True(System.Net.IPAddress.IsLoopback(e.Address)));
        var pages = new List<Page>();
        try
        {
            for (int i = 0; i < LocalServer.MaxClients; i++) { var page = await Page.Host(companion.Server, "page-" + i); pages.Add(page); Assert.Equal(i == 0 ? "owner" : "view", Role(await page.Next("role"))); }
            using (var ninth = new ClientWebSocket())
            {
                ninth.Options.SetRequestHeader("Origin", Site);
                var refused = await Assert.ThrowsAsync<WebSocketException>(() => ninth.ConnectAsync(new Uri($"ws://127.0.0.1:{companion.Server.Port}/bridge"), CancellationToken.None));
                Assert.Contains("409", refused.Message);
            }
            // A place frees up when a page leaves.
            await pages[^1].DisposeAsync(); pages.RemoveAt(pages.Count - 1);
            Page? again = null;
            for (int attempt = 0; again == null && attempt < 20; attempt++)
            {
                try { again = await Page.Host(companion.Server, "again"); }
                catch (WebSocketException) { await Task.Delay(100); }
            }
            Assert.NotNull(again); pages.Add(again!);
            Assert.Equal("view", Role(await again!.Next("role")));
        }
        finally { foreach (var page in pages) await page.DisposeAsync(); }
    }

    [Fact]
    public async Task AWrongTokenStillClosesTheSocketForProtocol3()
    {
        await using var companion = await new Companion(folder, Key).Start();
        using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", Site);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{companion.Server.Port}/bridge"), CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes(Json.Write(new { token = "invalid", protocol = 3, clientId = "x", ownerKey = Key })), WebSocketMessageType.Text, true, CancellationToken.None);
        var result = await socket.ReceiveAsync(new byte[1000], CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType); Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
    }
}
