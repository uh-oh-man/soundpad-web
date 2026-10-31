using System.IO.Pipes;
using System.Text;
namespace SoundpadWeb;

public interface ISoundpad { Task<string> Request(string command, CancellationToken cancel = default); }
// The vendor writes UTF-8 commands without newline or terminator. Responses are pipe messages.
public sealed class SoundpadPipe(string name = "sp_remote_control", bool enableFixtureOwnedRemoval = false) : ISoundpad, IAtomicOwnedEntryRemoval, IDisposable
{
    public bool IsProduction => name == "sp_remote_control";
    public bool CanRemoveOwnedAtomically => enableFixtureOwnedRemoval && name.StartsWith("soundpad-web-test-", StringComparison.Ordinal);
    public async Task<bool> RemoveOwnedEntry(string path, string expectedCatalogFingerprint)
    {
        if (!CanRemoveOwnedAtomically) return false;
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(new { path, fingerprint = expectedCatalogFingerprint })));
        return (await Request("FixtureRemoveOwnedEntry(" + payload + ")")).StartsWith("R-200", StringComparison.Ordinal);
    }
    private readonly SemaphoreSlim gate = new(1);
    private NamedPipeClientStream? pipe;
    public async Task<string> Request(string command, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await gate.WaitAsync(timeout.Token);
        try
        {
            if (pipe is not { IsConnected: true })
            {
                pipe?.Dispose();
                pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeout.Token);
                pipe.ReadMode = PipeTransmissionMode.Message;
            }
            await Task.Delay(2, timeout.Token); // official reference warns against same-ms requests
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command), timeout.Token);
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            do
            {
                int read = await pipe.ReadAsync(buffer, timeout.Token);
                if (read == 0) throw new IOException("Soundpad closed the pipe.");
                result.Write(buffer, 0, read);
                if (result.Length > 16 * 1024 * 1024) throw new InvalidDataException("Soundpad response too large.");
            } while (!pipe.IsMessageComplete);
            return new UTF8Encoding(false, true).GetString(result.ToArray()).TrimEnd('\0');
        }
        catch { pipe?.Dispose(); pipe = null; throw; }
        finally { gate.Release(); }
    }
    public void Dispose() { pipe?.Dispose(); gate.Dispose(); }
}
