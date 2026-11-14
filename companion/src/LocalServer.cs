using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
namespace SoundpadWeb;

/// <param name="ownerKey">The Soundpad Web window's per-launch key (2.36). A page that presents it is the session
/// owner. Null when there is no window (--no-window, the fallback, the harness): the first page that asks becomes owner.</param>
public sealed class LocalServer(Bridge bridge, string origin, string? ownerKey = null) : IAsyncDisposable
{
    private WebApplication? app;
    /// <summary>Playback-control messages accepted per sliding second on the socket (all people together, 2.35).</summary>
    public const int ControlPerSecond = 600;
    /// <summary>Host pages connected at once (2.36): the Soundpad Web window plus browser tabs that view it.</summary>
    public const int MaxClients = 8;
    /// <summary>How long a vanished owner's place is held for it (2.36): long enough for a page reload.</summary>
    public TimeSpan OwnerGrace { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Warnings worth a line in the companion log (never carries keys or tokens).</summary>
    public Action<string>? Log { get; init; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public int Port { get; private set; }
    private int sockets;

    // Session ownership (2.36). Exactly one protocol-3 page runs the guest session (the Trystero host); the rest are
    // views of it. Everything below is guarded by `roles`.
    private readonly object roles = new();
    private readonly List<HostClient> hosts = []; // protocol >= 3, oldest first
    private int legacyHosts;                      // protocol < 3 pages: always owner-capable, as in 2.35
    private HostClient? owner;
    private string? graceFor;                     // clientId whose place is held after its owner page vanished
    private long graceGeneration;
    // The owner's HostIdentity and latest session summary live in memory only, for this launch: a reloaded, restarted
    // or promoted owner gets the same identity back, so guests' invitations keep working. A new launch starts afresh.
    private JsonElement? identity;
    // The owner's session settings (2.37: admission, guest places, per-guest permissions, turned-away guests), kept
    // exactly like the identity: memory only, owner only, handed to the next owner, forgotten on End hosting. The
    // companion never reads inside it; the owner page enforces it.
    private JsonElement? settings;
    /// <summary>Largest session settings message kept (2.37). Larger ones are ignored, and the owner page keeps its own copy.</summary>
    public const int MaxSessionSettingsBytes = 65536;
    private string? session;
    private long sessionVersion;

    // The quit prompt (2.40): closing the window while Temp holds clips asks its page first. Guarded by `roles`.
    private Action<QuitAnswer>? quitAnswered;
    private string? quitPromptId;
    private long quitAskedAt;
    /// <summary>A prompt the page has not answered for this long no longer holds the window open: the next close quits.</summary>
    public TimeSpan QuitPromptPatience { get; init; } = TimeSpan.FromSeconds(30);

    private sealed class HostClient(string clientId, bool keyed, bool quitPrompt = false)
    {
        public string ClientId { get; } = clientId;
        public bool Keyed { get; } = keyed;
        /// <summary>The page said it can show the quit prompt (2.40); older pages would drop the connection over it.</summary>
        public bool QuitPrompt { get; } = quitPrompt;
        public bool Owner;
        public bool Ended;                        // its last session command was End hosting (2.36)
        public string? RoleSent;
        public long SessionSent = -1;
        // Role and session changes coalesce: the sender reads the current values when it wakes.
        public Channel<byte> Signal { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        public Channel<string> Forward { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropWrite });
    }

    public async Task Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, 0); k.Limits.MaxRequestBodySize = 16777216; });
        app = builder.Build();
        app.MapMethods("/session-audio", ["POST", "OPTIONS"], async context =>
        {
            if (context.Request.Headers.Origin != origin || context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = "Authorization, X-Audio-Name, X-Audio-By, X-Audio-Actor, X-Audio-Owner, X-Audio-Audience, Content-Type";
            if (context.Request.Method == "OPTIONS") { context.Response.StatusCode = 204; return; }
            var supplied = context.Request.Headers.Authorization.ToString();
            var expected = "Bearer " + Token;
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected))) { context.Response.StatusCode = 403; return; }
            if (context.Request.ContentLength is null or < 1 or > 16777216) { context.Response.StatusCode = 413; return; }
            using var input = new MemoryStream(); await context.Request.Body.CopyToAsync(input, context.RequestAborted);
            string name, by;
            try { name = Uri.UnescapeDataString(context.Request.Headers["X-Audio-Name"].ToString()); by = Uri.UnescapeDataString(context.Request.Headers["X-Audio-By"].ToString()); }
            catch (UriFormatException) { context.Response.StatusCode = 400; return; }
            if (name.Length > 256 || by.Length > 80) { context.Response.StatusCode = 400; return; }
            var result = await bridge.Upload(input.ToArray(), name, by, context.Request.Headers["X-Audio-Actor"] == "host", context.Request.Headers["X-Audio-Owner"].ToString()[..Math.Min(128, context.Request.Headers["X-Audio-Owner"].ToString().Length)], context.Request.Headers["X-Audio-Audience"].ToString());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(Json.Write(result));
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/bridge", async context =>
        {
            // Exact origin, exact numeric Host. No wildcard CORS and no token in access URLs/logs.
            if (context.Request.Headers.Origin != origin || context.Request.Host.Host != "127.0.0.1" || !context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 403; return; }
            // Several host pages at once (2.36; one until 2.35). A place is taken before the hello, so pages that
            // never authenticate can hold one for at most the 5 s hello deadline.
            if (Interlocked.Increment(ref sockets) > MaxClients) { Interlocked.Decrement(ref sockets); context.Response.StatusCode = 409; return; }
            HostClient? host = null; bool legacy = false;
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                using var sendGate = new SemaphoreSlim(1);
                async Task SendText(string text)
                {
                    await sendGate.WaitAsync(lifetime.Token);
                    try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(8)); }
                    // A page that stops reading for 8 s is dropped, not kept with dead relays: it would otherwise stay
                    // "connected" and could be promoted to owner without ever hearing it (review, 2.36). It reconnects
                    // by itself and gets a fresh role.
                    catch (TimeoutException) { lifetime.Cancel(); socket.Abort(); throw; }
                    finally { sendGate.Release(); }
                }
                Task Send(object value) => SendText(Json.Write(value));
                string auth;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) { deadline.CancelAfter(5000); auth = await Receive(socket, deadline.Token); }
                using var hello = JsonDocument.Parse(auth);
                var supplied = hello.RootElement.GetProperty("token").GetString() ?? "";
                int version = hello.RootElement.TryGetProperty("protocol", out var protocol) && protocol.TryGetInt32(out int number) ? number : 1;
                bool modern = version >= 2;
                if (!Matches(supplied, Token)) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None); return; }
                // Protocol 3 pages (2.36) are told their role before anything else; older pages are hosts exactly as in 2.35.
                if (version >= 3) { host = Join(hello.RootElement); await Flush(host, SendText); }
                else { legacy = true; JoinLegacy(); }
                var changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
                void Changed(Snapshot _) => changes.Writer.TryWrite(0);
                bridge.Changed += Changed; changes.Writer.TryWrite(0);
                var waves = Channel.CreateBounded<Waveform>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
                void WaveChanged(Waveform value) => waves.Writer.TryWrite(value);
                bridge.WaveformChanged += WaveChanged;
                var waveSender = Task.Run(async () => { await foreach (var value in waves.Reader.ReadAllAsync(lifetime.Token)) await Send(new { type = "waveform", data = value }); });
                string? sentLibrary = null;
                var sender = Task.Run(async () =>
                {
                    await foreach (var _ in changes.Reader.ReadAllAsync(lifetime.Token))
                    {
                        var state = bridge.Snapshot;
                        bool full = !modern || sentLibrary != state.Library.Version;
                        await Send(new { type = full ? "state" : "tick", state = full ? state : state with { Library = null! } }); sentLibrary = state.Library.Version;
                    }
                });
                var relay = host == null ? Task.CompletedTask : Task.Run(async () => { await foreach (var _ in host.Signal.Reader.ReadAllAsync(lifetime.Token)) await Flush(host, SendText); });
                var forward = host == null ? Task.CompletedTask : Task.Run(async () => { await foreach (var text in host.Forward.Reader.ReadAllAsync(lifetime.Token)) await SendText(text); });
                // Two budgets per sliding second (2.35). Playback control is cheap and coalesces in the Bridge (a newer
                // play releases the older one at once), so a burst of presses from several people must never be answered
                // with "Too many requests"; its limit only stops a runaway sender. Queries and everything else
                // (waveforms, statistics, Spotify, Mimic peeks, edits) keep the old, tighter limit.
                var controlBucket = new Queue<long>(); var hostControlBucket = new Queue<long>(); var bucket = new Queue<long>();
                using var queries = new SemaphoreSlim(16);
                var running = new List<Task>();
                // Answers one command when it is done. Nothing is awaited in the receive loop any more (2.35): a Stop
                // that waits for Soundpad no longer holds up reading the next message.
                async Task Answer(string id, Task<Reply> operation, SemaphoreSlim? slot)
                {
                    try
                    {
                        Reply reply;
                        try { reply = await operation.WaitAsync(lifetime.Token); }
                        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { reply = new("ack", id, false, "Companion operation failed. Check Soundpad and local storage."); }
                        await Send(reply);
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
                    finally { slot?.Release(); }
                }
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var input = await Receive(socket, lifetime.Token);
                        var now = Environment.TickCount64;
                        using var doc = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 24 });
                        var root = doc.RootElement;
                        // Session relay messages between host pages (2.36) carry a type; commands never do.
                        if (host != null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String) { Relay(host, kind.GetString()!, root); continue; }
                        var actor = root.GetProperty("actor").GetString() == "host" ? "host" : "guest";
                        var command = root.GetProperty("command").Deserialize<Command>(Json.Options) ?? throw new InvalidDataException();
                        running.RemoveAll(t => t.IsCompleted);
                        bool control = command.Op is "play" or "startAt" or "region" or "stop" or "seek" or "pause" or "panic" or "audioPlay" or "reverse";
                        // The host's own Stop and Panic never compete with relayed guest traffic for this budget (2.35).
                        var budget = control ? (actor == "host" ? hostControlBucket : controlBucket) : bucket;
                        while (budget.Count > 0 && budget.Peek() < now - 1000) budget.Dequeue();
                        if (budget.Count >= (control ? ControlPerSecond : 32)) { running.Add(Answer(command.Id, Task.FromResult(new Reply("ack", command.Id, false, "Too many requests. Try again shortly.")), null)); continue; }
                        budget.Enqueue(now);
                        Participant? person = root.TryGetProperty("participant", out var p) ? p.Deserialize<Participant>(Json.Options) : null;
                        string? requester = root.TryGetProperty("requester", out var requestPeer) ? requestPeer.GetString() : null;
                        string? audience = root.TryGetProperty("audience", out var requestAudience) ? requestAudience.GetString() : null;
                        if (requester?.Length > 128 || audience?.Length > 64) throw new InvalidDataException("Invalid request ownership.");
                        // When the owner page first admitted this guest (2.39), so the recording history shows them only what
                        // was recorded since. The Bridge keeps the first value per participant; a later one never lowers it.
                        if (actor == "guest")
                            bridge.NoteGuest(person, requester, root.TryGetProperty("joinedAt", out var joined) && joined.ValueKind == JsonValueKind.Number && joined.TryGetInt64(out long joinedAt) ? joinedAt : null);
                        // Expensive work is admitted sixteen at a time. Playback control never counts against that.
                        SemaphoreSlim? slot = null;
                        if (command.Op is "mimicSources" or "mimicStart" or "mimicClip" or "mimicLibrary" or "mimicLibraryImport" or "mimicTranscribe" or "trimAudio" or "trimPreview" or "waveform" or "spotify" or "audioAssets" or "keepAudio" or "removeAudio" or "retryAudio" or "retryCleanup" or "cleanupCategories" or "clearTemp" or "beginSession" or "endSession"
                            // 2.40: previews, clips made from drafts, and Temp recovery are expensive too.
                            or "mimicPreview" or "draftKeep" or "draftPlay" or "tempRecover")
                        {
                            if (!await queries.WaitAsync(0)) { running.Add(Answer(command.Id, Task.FromResult(new Reply("ack", command.Id, false, "Too many background requests.")), null)); continue; }
                            slot = queries;
                        }
                        // Execute is CALLED here, in arrival order, and runs up to its first real wait: that is where
                        // playback intents get their order. Its answer is sent whenever it is ready.
                        if (host != null && actor == "host" && command.Op is "endSession" or "beginSession") Hosting(host, command.Op == "endSession");
                        var operation = bridge.Execute(command, actor, person, requester, audience);
                        running.Add(Answer(command.Id, operation, slot));
                    }
                }
                finally { bridge.Changed -= Changed; bridge.WaveformChanged -= WaveChanged; lifetime.Cancel(); try { await sender; await waveSender; await relay; await forward; await Task.WhenAll(running); } catch (Exception) { } }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IOException or InvalidDataException or TimeoutException) { }
            finally
            {
                if (host != null) Leave(host);
                if (legacy) lock (roles) legacyHosts--;
                Interlocked.Decrement(ref sockets);
            }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Port = new Uri(address).Port;
    }

    private static bool Matches(string? supplied, string expected) => supplied != null && supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));

    /// <summary>Registers an authenticated protocol-3 page and decides its role (2.36).</summary>
    private HostClient Join(JsonElement hello)
    {
        string? key = hello.TryGetProperty("ownerKey", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        string? id = hello.TryGetProperty("clientId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        // A page without a usable id can never reclaim a held place; it still gets a role like any other page.
        if (string.IsNullOrEmpty(id) || id.Length > 64) id = "unnamed-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        bool quitPrompt = hello.TryGetProperty("quitPrompt", out var q) && q.ValueKind == JsonValueKind.True;
        var client = new HostClient(id, ownerKey != null && Matches(key, ownerKey), quitPrompt);
        string? warning = null;
        lock (roles)
        {
            if (legacyHosts > 0) warning = "A host page from an older website is still open next to this one; both may run hosting. Reload or close the older tab.";
            if (client.Keyed)
            {
                // The Soundpad Web window always runs the session. A browser tab that held it until now becomes a view
                // and leaves its room without revoking guests; they rejoin the window's room with the same invitation.
                if (owner != null && owner != client) { if (!owner.Keyed) warning ??= "The Soundpad Web window took over hosting from a browser tab."; Demote(owner); }
                Promote(client);
            }
            // The first page becomes owner, unless an owner that just vanished still has its place held (a reload).
            else if (owner == null && (graceFor == null || graceFor == client.ClientId)) Promote(client);
            hosts.Add(client);
        }
        if (warning != null) Log?.Invoke(warning);
        return client;
    }

    private void JoinLegacy()
    {
        bool others;
        lock (roles) { others = hosts.Count + legacyHosts > 0; legacyHosts++; }
        // Accepted for the compatibility window (a tab still on the 2.35 website), but two pages may now host.
        if (others) Log?.Invoke("A host page from an older website connected while another host page is open; both may run hosting. Reload or close the older tab.");
    }

    private void Leave(HostClient client)
    {
        lock (roles)
        {
            hosts.Remove(client);
            if (owner != client) return;
            owner = null; client.Owner = false;
            // An owner that pressed End hosting is not waited for, and no view is promoted into the session it ended: the
            // next page that connects (its Resume hosting, say) becomes owner and starts a fresh invitation (2.36).
            if (client.Ended) { graceFor = null; graceGeneration++; return; }
            // Hold the place for a reload of the same page (or the window's key), then hand hosting to the oldest view.
            graceFor = client.ClientId; long generation = ++graceGeneration;
            _ = Task.Delay(OwnerGrace).ContinueWith(_ => GraceOver(generation), TaskScheduler.Default);
        }
    }

    private void GraceOver(long generation)
    {
        lock (roles)
        {
            if (generation != graceGeneration || owner != null) return;
            graceFor = null;
            if (hosts.Count > 0) Promote(hosts[0]);
        }
    }

    // Callers hold `roles`.
    private void Promote(HostClient client) { graceFor = null; graceGeneration++; owner = client; client.Owner = true; client.Signal.Writer.TryWrite(0); }
    private static void Demote(HostClient client) { client.Owner = false; client.Signal.Writer.TryWrite(0); }

    /// <summary>Sends a page its role (when it changed) and, to a view, the latest session summary. Never runs twice at once for one page.</summary>
    private async Task Flush(HostClient client, Func<string, Task> send)
    {
        string role; JsonElement? remembered; JsonElement? kept; string? summary; long version;
        lock (roles) { role = client.Owner ? "owner" : "view"; remembered = client.Owner ? identity : null; kept = client.Owner ? settings : null; summary = session; version = sessionVersion; }
        if (role != client.RoleSent) { await send(Json.Write(new { type = "role", role, identity = remembered, settings = kept })); client.RoleSent = role; }
        if (role == "view" && summary != null && client.SessionSent != version) { await send(summary); client.SessionSent = version; }
    }

    /// <summary>
    /// End hosting (<paramref name="ended"/>) or a new session (beginSession) from the owner page (2.36). End hosting revokes
    /// every guest, so its invitation must not come back: the kept identity is forgotten (a later owner creates a new one,
    /// as Resume hosting did up to 2.35) and the views are told the session is empty.
    /// </summary>
    private void Hosting(HostClient from, bool ended)
    {
        lock (roles)
        {
            if (owner != from) return;
            from.Ended = ended;
            if (!ended) return;
            identity = null; settings = null;
            session = Json.Write(new { type = "session", invite = "", guests = Array.Empty<string>(), people = new { }, layouts = Array.Empty<object>() }); sessionVersion++;
            foreach (var view in hosts) if (view != from) view.Signal.Writer.TryWrite(0);
        }
    }

    /// <summary>Messages between host pages (2.36). Every loopback page is the host, so nothing here is guest input.</summary>
    private void Relay(HostClient from, string kind, JsonElement root)
    {
        switch (kind)
        {
            case "identity":
                // Kept for this launch only, from the owner only: a demoted page's late message must not replace it.
                if (!root.TryGetProperty("identity", out var value) || value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 16384) return;
                lock (roles) if (owner == from && !from.Ended) identity = value.Clone();
                return;
            case "sessionSettings":
                // 2.37: the same rules as the identity. Only the owner's own decisions about its guests, at most 64 KiB.
                if (!root.TryGetProperty("settings", out var kept) || kept.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(kept.GetRawText()) > MaxSessionSettingsBytes) return;
                lock (roles) if (owner == from && !from.Ended) settings = kept.Clone();
                return;
            case "session":
                lock (roles)
                {
                    // An ended session's late summary must not bring its invitation back to the views.
                    if (owner != from || from.Ended) return;
                    session = root.GetRawText(); sessionVersion++;
                    foreach (var view in hosts) if (view != from) view.Signal.Writer.TryWrite(0);
                }
                return;
            case "quitAnswer":
                {
                    // Only the window's own page answers the window's question.
                    if (!from.Keyed) return;
                    string? id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    string? choice = root.TryGetProperty("action", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString() : null;
                    if (choice is not ("keep" or "quit" or "cancel")) return;
                    var ids = new List<string>();
                    if (root.TryGetProperty("assetIds", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var item in list.EnumerateArray().Take(256))
                            if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out _)) ids.Add(item.GetString()!);
                    Action<QuitAnswer>? answered;
                    lock (roles)
                    {
                        if (id == null || id != quitPromptId) return;
                        answered = quitAnswered; quitAnswered = null; quitPromptId = null;
                    }
                    answered?.Invoke(new(choice, [.. ids]));
                    return;
                }
            case "ownerAction":
                var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                if (action is not ("rotate" or "disconnect")) return;
                string? peer = root.TryGetProperty("peer", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (peer?.Length > 128) return;
                HostClient? target;
                lock (roles) target = owner != null && owner != from ? owner : null;
                target?.Forward.Writer.TryWrite(Json.Write(new { type = "ownerAction", action, peer }));
                return;
        }
    }

    /// <summary>
    /// The window is being closed while Temp holds <paramref name="clips"/> clips (2.40): asks the window's page, which
    /// shows "N clips in Temp will be removed: Keep selected / Quit anyway / Cancel" and answers with a quitAnswer
    /// message. False when there is no page to ask (no window, an older website, a prompt left unanswered for
    /// <see cref="QuitPromptPatience"/>): the window then closes as before. <paramref name="answered"/> runs once.
    /// </summary>
    public bool AskQuit(int clips, Action<QuitAnswer> answered)
    {
        HostClient? page;
        string id = Guid.NewGuid().ToString("N");
        lock (roles)
        {
            page = owner is { Keyed: true, QuitPrompt: true, Ended: false } o ? o : hosts.FirstOrDefault(h => h.Keyed && h.QuitPrompt);
            if (page == null) return false;
            long now = Environment.TickCount64;
            if (quitPromptId != null && now - quitAskedAt >= (long)QuitPromptPatience.TotalMilliseconds) { quitPromptId = null; quitAnswered = null; return false; }
            if (quitPromptId == null) quitAskedAt = now;
            quitPromptId = id; quitAnswered = answered;
        }
        return page.Forward.Writer.TryWrite(Json.Write(new { type = "quitPrompt", id, clips }));
    }

    private static async Task<string> Receive(WebSocket socket, CancellationToken cancel)
    {
        using var data = new MemoryStream(); var buf = new byte[8192]; WebSocketReceiveResult r;
        do
        {
            r = await socket.ReceiveAsync(buf, cancel);
            if (r.MessageType == WebSocketMessageType.Close) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); throw new OperationCanceledException(); }
            if (r.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Text messages only.");
            data.Write(buf, 0, r.Count);
            if (data.Length > 200000) throw new InvalidDataException("Message too large.");
        } while (!r.EndOfMessage);
        return new UTF8Encoding(false, true).GetString(data.ToArray());
    }
    public async ValueTask DisposeAsync() { if (app != null) await app.DisposeAsync(); }
}
