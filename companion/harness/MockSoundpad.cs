using System.IO.Pipes;
using System.Text;
namespace SoundpadWeb.Testing;
// Schema-derived test fixture, never included in the production executable.
public sealed class MockSoundpad : IAsyncDisposable
{
    public string Name { get; } = "soundpad-web-test-" + Guid.NewGuid().ToString("N");
    public const string Sounds = "<Soundlist><Sound index=\"1\" url=\"C:\\Private\\airhorn.wav\" title=\"Airhorn\"/><Sound index=\"2\" url=\"C:\\Private\\laugh.wav\" title=\"Laugh\"/><Sound index=\"3\" url=\"C:\\Private\\other-laugh.wav\" title=\"Laugh\"/><Sound index=\"4\" url=\"C:\\Private\\long.wav\" title=\"An extremely long sound title that should wrap gracefully on every single phone screen without breaking the grid\"/></Soundlist>";
    public const string Categories = "<Categories><Category index=\"2\" name=\"Reactions\"><Sound index=\"2\" url=\"C:\\Private\\laugh.wav\"/><Category index=\"5\" name=\"Deep cuts\"><Sound index=\"1\" url=\"C:\\Private\\airhorn.wav\"/></Category></Category><Category index=\"7\" name=\"Empty category\"/><Category index=\"8\" name=\"Favorites\"><Sound index=\"3\" url=\"C:\\Private\\other-laugh.wav\"/><Sound index=\"1\" url=\"C:\\Private\\airhorn.wav\"/></Category></Categories>";
    public string AllXml { get; set; } = Sounds;
    public string CategoryXml { get; set; } = Categories;
    public List<string> Commands { get; } = [];
    public string Status { get; private set; } = "STOPPED";
    private readonly CancellationTokenSource cancel = new();
    private readonly Task loop;
    private long startedTick, position;
    public long DurationMs { get; set; } = 2500;
    private long Position => position + (Status == "PLAYING" ? Environment.TickCount64 - startedTick : 0);
    public MockSoundpad(string? name = null) { if (name != null) Name = name; loop = Run(); }
    private async Task Run()
    {
        while (!cancel.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(cancel.Token);
                while (server.IsConnected && !cancel.IsCancellationRequested)
                {
                    byte[] buffer = new byte[4096]; int read = await server.ReadAsync(buffer, cancel.Token); if (read == 0) break;
                    var command = Encoding.UTF8.GetString(buffer, 0, read); lock (Commands) Commands.Add(command);
                    if (Status == "PLAYING" && Position >= DurationMs) Status = "STOPPED";
                    string result = command switch { "GetSoundlist()" => AllXml, "GetCategories(true, true)" => CategoryXml, "GetPlayStatus()" => Status, "GetPlaybackPositionInMs()" => Position.ToString(), "GetPlaybackDurationInMs()" => DurationMs.ToString(), "IsAlive()" => "R-200", _ => command.StartsWith("Do") ? "R-200" : "R-404" };
                    if (command.StartsWith("DoPlaySound(")) { Status = "PLAYING"; position = 0; startedTick = Environment.TickCount64; Console.WriteLine("TEST_PLAY " + command); }
                    if (command.StartsWith("DoSeekMs(")) { position = long.Parse(command[9..^1]); startedTick = Environment.TickCount64; Console.WriteLine("TEST_PLAY " + command); }
                    if (command == "DoTogglePause()") { position = Position; Status = Status == "PLAYING" ? "PAUSED" : "PLAYING"; startedTick = Environment.TickCount64; }
                    if (command == "DoStopSound()") { Status = "STOPPED"; position = 0; }
                    await server.WriteAsync(Encoding.UTF8.GetBytes(result), cancel.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { cancel.Cancel(); await loop; cancel.Dispose(); }
}
