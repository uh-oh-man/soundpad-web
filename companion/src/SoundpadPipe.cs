using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
namespace SoundpadWeb;

/// <summary>
/// Soundpad reads commands in the Windows ANSI code page (its own reference client sends them with the
/// platform default charset) and replies in UTF-8. Sending UTF-8 garbled every non-ASCII character: the
/// Temp category "Temp · Soundpad Web" arrived as "Temp Â· Soundpad Web", was never recognised, and was
/// created again on every attempt (2.15.9).
/// </summary>
public static class SoundpadText
{
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    private static readonly Lazy<Encoding> command = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int page = OperatingSystem.IsWindows() ? (int)GetACP() : 1252;
        return Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    });
    /// <summary>The encoding Soundpad decodes commands with.</summary>
    public static Encoding Command => command.Value;
    public static bool CanSend(string text)
    {
        try { Command.GetBytes(text); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    /// <summary>Replaces characters Soundpad can't receive (emoji, other scripts) with '_', e.g. in managed file names.</summary>
    public static string Sendable(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            int width = char.IsSurrogatePair(text, i) ? 2 : 1;
            var piece = text.Substring(i, width);
            result.Append(CanSend(piece) ? piece : "_");
            i += width - 1;
        }
        return result.ToString();
    }
}

public interface ISoundpad { Task<string> Request(string command, CancellationToken cancel = default); }
// Commands go without newline or terminator, in the ANSI code page (see SoundpadText). Responses are UTF-8 pipe messages.
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
            byte[] request;
            try { request = SoundpadText.Command.GetBytes(command); }
            catch (EncoderFallbackException) { throw new InvalidDataException("This name or path has characters Soundpad can't receive on this PC. Rename it using plain letters, then retry."); }
            await Task.Delay(2, timeout.Token); // official reference warns against same-ms requests
            await pipe.WriteAsync(request, timeout.Token);
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
