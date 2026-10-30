using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
namespace SoundpadWeb;

public sealed class LocalServer(Bridge bridge, string origin) : IAsyncDisposable
{
    private WebApplication? app;
    private int connected;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public int Port { get; private set; }
    public async Task Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, 0); k.Limits.MaxRequestBodySize = 200000; });
        app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/bridge", async context =>
        {
            // Exact origin, exact numeric Host. No wildcard CORS and no token in access URLs/logs.
            if (context.Request.Headers.Origin != origin || context.Request.Host.Host != "127.0.0.1" || !context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 403; return; }
            if (Interlocked.CompareExchange(ref connected, 1, 0) != 0) { context.Response.StatusCode = 409; return; }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                using var sendGate = new SemaphoreSlim(1);
                async Task Send(object value)
                {
                    await sendGate.WaitAsync(lifetime.Token);
                    try { await socket.SendAsync(Encoding.UTF8.GetBytes(Json.Write(value)), WebSocketMessageType.Text, true, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(8)); }
                    finally { sendGate.Release(); }
                }
                string auth;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) { deadline.CancelAfter(5000); auth = await Receive(socket, deadline.Token); }
                var hello = JsonDocument.Parse(auth);
                var supplied = hello.RootElement.GetProperty("token").GetString() ?? "";
                bool modern = hello.RootElement.TryGetProperty("protocol", out var protocol) && protocol.TryGetInt32(out int version) && version >= 2;
                if (supplied.Length != Token.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(Token))) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None); return; }
                var changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
                void Changed(Snapshot _) => changes.Writer.TryWrite(0);
                bridge.Changed += Changed; changes.Writer.TryWrite(0);
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
                var bucket = new Queue<long>();
                using var queries = new SemaphoreSlim(16);
                var running = new List<Task>();
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var input = await Receive(socket, lifetime.Token);
                        var now = Environment.TickCount64;
                        while (bucket.Count > 0 && bucket.Peek() < now - 1000) bucket.Dequeue();
                        using var doc = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 24 });
                        var root = doc.RootElement;
                        var actor = root.GetProperty("actor").GetString() == "host" ? "host" : "guest";
                        var command = root.GetProperty("command").Deserialize<Command>(Json.Options) ?? throw new InvalidDataException();
                        if (bucket.Count >= 32) { await Send(new Reply("ack", command.Id, false, "Too many requests. Try again shortly.")); continue; }
                        bucket.Enqueue(now);
                        Participant? person = root.TryGetProperty("participant", out var p) ? p.Deserialize<Participant>(Json.Options) : null;
                        if (command.Op is "waveform" or "spotify")
                        {
                            running.RemoveAll(t => t.IsCompleted);
                            if (!await queries.WaitAsync(0)) { await Send(new Reply("ack", command.Id, false, "Too many background requests.")); continue; }
                            running.Add(Task.Run(async () => { try { await Send(await bridge.Execute(command, actor, person).WaitAsync(lifetime.Token)); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or TimeoutException) { } finally { queries.Release(); } }));
                        }
                        else await Send(await bridge.Execute(command, actor, person));
                    }
                }
                finally { bridge.Changed -= Changed; lifetime.Cancel(); try { await sender; await Task.WhenAll(running); } catch (Exception) { } }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IOException or InvalidDataException or TimeoutException) { }
            finally { Interlocked.Exchange(ref connected, 0); }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Port = new Uri(address).Port;
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
