using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace VoiceReplay.Core;

public interface ISoundpadTransport
{
    Task<string> Request(string command, CancellationToken token = default);
}
public sealed class PipeTransport(string pipeName = "sp_remote_control") : ISoundpadTransport
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string> Request(string command, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            // The vendor reference spaces requests by at least one millisecond to protect the pipe.
            await Task.Delay(1, token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            pipe.ReadMode = PipeTransmissionMode.Message;
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            do
            {
                int read = await pipe.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    throw new IOException("Soundpad closed the pipe.");
                bytes.Write(buffer, 0, read);
                if (bytes.Length > 8 * 1024 * 1024)
                    throw new IOException("Soundpad response exceeds safety limit.");
            } while (!pipe.IsMessageComplete);
            return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0', '\r', '\n');
        }
        finally { gate.Release(); }
    }
}
public sealed class SoundpadService(ISoundpadTransport transport)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Status { get; private set; } = "Not checked";
    public bool Connected
    {
        get; private set;
    }
    public static bool Installed()
    {
        using var key = Registry.ClassesRoot.OpenSubKey(@"soundpad\shell\open\command");
        return key is not null || System.Diagnostics.Process.GetProcessesByName("Soundpad").Length > 0 || File.Exists(@"C:\Program Files\Soundpad\Soundpad.exe") || File.Exists(@"C:\Program Files (x86)\Steam\steamapps\common\Soundpad\Soundpad.exe");
    }
    public async Task<bool> Probe(CancellationToken token = default)
    {
        try
        {
            Check(await transport.Request("IsAlive()", token));
            Connected = true;
            Status = "Connected · API reachable";
        }
        catch (Exception ex) { Connected = false; Status = "Unavailable · " + (ex is OperationCanceledException ? "pipe connection timed out" : ex.Message); }
        return Connected;
    }
    public static void Check(string response)
    {
        if (response.StartsWith("R-200", StringComparison.Ordinal))
            return;
        string explanation = response.StartsWith("R-403") || response.Contains("trial", StringComparison.OrdinalIgnoreCase) ? " DoAddSound is unavailable in the trial edition." : "";
        throw new IOException("Soundpad rejected request: " + response + explanation);
    }
    public async Task<string> Diagnostic()
    {
        if (!await Probe())
            return Status;
        string version = await transport.Request("GetVersion()");
        string api = await transport.Request("GetRemoteControlVersion()");
        string trial = await transport.Request("IsTrial()");
        string count = await transport.Request("GetSoundFileCount()");
        return $"Installed/detected: {Installed()}\n{Status}\nSoundpad: {version} · API: {api}\nTrial (1=yes): {trial} · Sounds: {count}\nConnection checks do not establish successful import. Check each clip's verified import status.";
    }
    public static XElement? FindSound(string xml, string path) => XDocument.Parse(xml).Descendants("Sound").FirstOrDefault(x => string.Equals((string?)x.Attribute("url"), path, StringComparison.OrdinalIgnoreCase));
    public async Task<string> Import(string path, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Clip missing", path);
            if (path.Contains('"') || path.Contains('\n') || path.Contains('\r'))
                throw new ArgumentException("Invalid pipe path.");
            var existing = FindSound(await transport.Request("GetSoundlist()", token), path);
            if (existing is not null)
                return (string?)existing.Attribute("index") ?? throw new IOException("Soundpad entry has no index.");
            async Task<XElement?> Category() => XDocument.Parse(await transport.Request("GetCategories(false,false)", token)).Descendants("Category").FirstOrDefault(x => (string?)x.Attribute("name") == "Voice Replay Studio");
            var category = await Category();
            if (category is null)
            {
                Check(await transport.Request("DoAddCategory(\"Voice Replay Studio\", -1)", token));
                for (int i = 0; i < 10 && category is null; i++)
                {
                    await Task.Delay(150, token);
                    category = await Category();
                }
            }
            if (category is null || !int.TryParse((string?)category.Attribute("index"), out int index) || index < 0)
                throw new IOException("Soundpad category was not confirmed.");
            Check(await transport.Request($"DoAddSound(\"{path}\", {index}, 1)", token));
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(200, token);
                existing = FindSound(await transport.Request("GetSoundlist()", token), path);
                if (existing is not null)
                    return (string?)existing.Attribute("index") ?? throw new IOException("Soundpad entry has no index.");
            }
            throw new IOException("Soundpad acknowledged import but the path is not yet in GetSoundlist. Will verify before retrying.");
        }
        finally { gate.Release(); }
    }
    public async Task Stop(CancellationToken token = default) => Check(await transport.Request("DoStopSound()", token));
}
