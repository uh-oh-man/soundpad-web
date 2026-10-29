using SoundpadWeb;
using SoundpadWeb.Testing;
var directory = Path.Combine(Path.GetTempPath(), "soundpad-web-e2e-" + Guid.NewGuid().ToString("N"));
await using var mock = new MockSoundpad();
using var pipe = new SoundpadPipe(mock.Name);
var bridge = new Bridge(pipe, new StateStore(directory));
await bridge.Poll(true);
await using var server = new LocalServer(bridge, args.FirstOrDefault() ?? "http://127.0.0.1:5173");
await server.Start();
Console.WriteLine("TEST_READY " + Json.Write(new { port = server.Port, token = server.Token }));
using var stop = new CancellationTokenSource();
_ = Task.Run(() => { Console.ReadLine(); stop.Cancel(); });
try { while (!stop.IsCancellationRequested) { await Task.Delay(1000, stop.Token); await bridge.Poll(false); } }
catch (OperationCanceledException) { }
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
