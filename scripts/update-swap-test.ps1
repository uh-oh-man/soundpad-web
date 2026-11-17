# Process-level test of the companion's self-update (2.44, WP2). Windows PowerShell 5.1 compatible; also used by CI.
#
# Starts a real companion EXE ("old") against the harness's mock Soundpad (--mock-only) and a fake release server on
# 127.0.0.1 (a TcpListener, so no URL ACL is needed), and drives Check and Update now over the loopback WebSocket as a
# host page would. Nothing is downloaded from the internet and no real Soundpad or data folder is touched: every
# folder is a new one under %TEMP%, and nothing opens a window (--no-window, or --hidden-window once with -Hidden).
#
# Scenarios: a good update (the new EXE takes the old one's name, SoundpadWeb.previous.exe stays, the same port and
# token answer with the new version, the hand-over is gone); a wrong hash, a truncated download, a wrong version and a
# test run that fails (each refused before anything is swapped; the old companion keeps running on its port and
# token); and a new version that never becomes ready (the watchdog puts the old EXE back after 90 s and starts it
# again, which answers on the same port and token).
# 2.48, versioned names: the release serves SoundpadWeb.exe, SoundpadWeb-<new>.exe, SHA256.txt and SHA256SUMS.txt
# (and once only the first and third, as releases up to 2.47 did). A companion named SoundpadWeb-<old>.exe (or with a
# space) is updated to SoundpadWeb-<new>.exe and keeps SoundpadWeb-<old>.previous.exe; one that never becomes ready
# gets its versioned name back; and a SoundpadWeb-<new>.exe already in the folder is never replaced (nameTaken).
#
#   scripts/update-swap-test.ps1 -Exe artifacts/release/SoundpadWeb.exe          (CI, after the smoke test)
#   scripts/update-swap-test.ps1 -Dotnet <path to dotnet.exe>                     (publishes the old EXE itself)
#   -Hidden also runs the good update once with --hidden-window (the WebView2 profile-lock retry).
#   -Skip never-ready,probe-fail ... leaves scenarios out.
param(
    [string]$Exe = '',
    [string]$Dotnet = 'dotnet',
    [switch]$Hidden,
    [string[]]$Skip = @()
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$harness = Join-Path $repo 'companion\harness\bin\Release\net10.0-windows10.0.19041.0\win-x64\SoundpadWeb.Harness.exe'
if (-not (Test-Path -LiteralPath $harness)) { throw "Build the harness first: $harness" }
$root = Join-Path ([IO.Path]::GetTempPath()) ('soundpad-web-update-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$origin = 'http://127.0.0.1:5173'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

/// A fake GitHub for the update test: the releases/latest API answer, SHA256.txt and the EXE, on 127.0.0.1 only.
public class FakeReleaseServer
{
    private TcpListener listener;
    public int Port;
    public string Version = "";
    public byte[] Exe = new byte[0];
    public string Sha = "";
    public long Size;
    /// Also publish SoundpadWeb-<version>.exe and SHA256SUMS.txt, as releases do since 2.48.
    public bool Versioned = true;
    public string VersionedName { get { return "SoundpadWeb-" + Version + ".exe"; } }
    /// -1 serves the whole EXE; otherwise only that many bytes (a truncated download).
    public int ServeBytes = -1;
    public List<string> Requests = new List<string>();
    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var thread = new Thread(Loop); thread.IsBackground = true; thread.Start();
    }
    public void Stop() { try { listener.Stop(); } catch (Exception) { } }
    private void Loop()
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); } catch (Exception) { return; }
            ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
        }
    }
    private void Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 10000;
                var head = new StringBuilder(); var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n") && head.Length < 16384) { if (stream.Read(one, 0, 1) != 1) return; head.Append((char)one[0]); }
                var first = head.ToString().Split('\n')[0].Trim().Split(' ');
                string method = first[0], path = first[1];
                lock (Requests) Requests.Add(method + " " + path);
                string tag = "v" + Version, b = "http://127.0.0.1:" + Port;
                if (path == "/api/releases/latest")
                {
                    var json = "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false,\"html_url\":\"" + b + "/releases/tag/" + tag + "\",\"assets\":[" +
                        "{\"name\":\"SoundpadWeb.exe\",\"size\":" + Size + ",\"browser_download_url\":\"" + b + "/releases/download/" + tag + "/SoundpadWeb.exe\"}," +
                        (Versioned ? "{\"name\":\"" + VersionedName + "\",\"size\":" + Size + ",\"browser_download_url\":\"" + b + "/releases/download/" + tag + "/" + VersionedName + "\"}," +
                            "{\"name\":\"SHA256SUMS.txt\",\"size\":200,\"browser_download_url\":\"" + b + "/releases/download/" + tag + "/SHA256SUMS.txt\"}," : "") +
                        "{\"name\":\"SHA256.txt\",\"size\":66,\"browser_download_url\":\"" + b + "/releases/download/" + tag + "/SHA256.txt\"}]}";
                    Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(json), method);
                }
                else if (path.EndsWith("/SHA256.txt")) Send(stream, 200, "text/plain", Encoding.UTF8.GetBytes(Sha + "\r\n"), method);
                else if (Versioned && path.EndsWith("/SHA256SUMS.txt")) Send(stream, 200, "text/plain", Encoding.UTF8.GetBytes(Sha + "  SoundpadWeb.exe\n" + Sha + "  " + VersionedName + "\n"), method);
                else if (path.EndsWith("/SoundpadWeb.exe") || (Versioned && path.EndsWith("/" + VersionedName)))
                {
                    var body = Exe;
                    if (ServeBytes >= 0) { body = new byte[ServeBytes]; Array.Copy(Exe, body, ServeBytes); }
                    Send(stream, 200, "application/octet-stream", body, method);
                }
                else Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found"), method);
            }
        }
        catch (Exception) { }
    }
    private static void Send(Stream stream, int code, string type, byte[] body, string method)
    {
        var head = "HTTP/1.1 " + code + (code == 200 ? " OK" : " Not Found") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(head);
        stream.Write(bytes, 0, bytes.Length);
        if (method != "HEAD") stream.Write(body, 0, body.Length);
        stream.Flush();
    }
}

/// A host page's side of the loopback WebSocket, enough to check for an update and install it.
public class BridgeClient : IDisposable
{
    private ClientWebSocket socket;
    private readonly List<string> messages = new List<string>();
    public static BridgeClient Connect(int port, string token, string origin, int timeoutMs)
    {
        var client = new BridgeClient();
        client.socket = new ClientWebSocket();
        client.socket.Options.SetRequestHeader("Origin", origin);
        if (!client.socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/bridge"), CancellationToken.None).Wait(timeoutMs)) throw new TimeoutException("connect");
        client.Send("{\"token\":\"" + token + "\",\"protocol\":3,\"clientId\":\"update-swap-test\",\"updates\":true}");
        var reader = new Thread(client.Read); reader.IsBackground = true; reader.Start();
        return client;
    }
    public static BridgeClient TryConnect(int port, string token, string origin, int totalMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(totalMs);
        while (DateTime.UtcNow < until)
        {
            try { var c = Connect(port, token, origin, 3000); if (c.WaitFor("\"type\":\"state\"", 5000)) return c; c.Dispose(); }
            catch (Exception) { }
            Thread.Sleep(500);
        }
        return null;
    }
    private void Send(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(5000);
    }
    private void Read()
    {
        var buffer = new byte[1 << 16];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var data = new MemoryStream(); WebSocketReceiveResult r;
                do { r = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result; data.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) return;
                lock (messages) messages.Add(Encoding.UTF8.GetString(data.ToArray()));
            }
        }
        catch (Exception) { }
    }
    public void Command(string op, string body)
    {
        long at = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        Send("{\"actor\":\"host\",\"command\":{\"v\":2,\"id\":\"" + Guid.NewGuid().ToString("D") + "\",\"at\":" + at + ",\"op\":\"" + op + "\",\"body\":" + body + "}}");
    }
    /// True once a message containing the text arrived.
    public bool WaitFor(string text, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until) { lock (messages) foreach (var m in messages) if (m.Contains(text)) return true; Thread.Sleep(50); }
        return false;
    }
    /// The "update" object of the newest state or tick, as JSON text, or "".
    public string LastUpdate()
    {
        lock (messages)
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                int at = messages[i].IndexOf("\"update\":{", StringComparison.Ordinal);
                if (at < 0 || !(messages[i].StartsWith("{\"type\":\"state\"") || messages[i].StartsWith("{\"type\":\"tick\""))) continue;
                int end = messages[i].IndexOf('}', at);
                return messages[i].Substring(at + 9, end - at - 8);
            }
        return "";
    }
    public bool Open { get { return socket.State == WebSocketState.Open; } }
    public void Dispose() { try { socket.Abort(); socket.Dispose(); } catch (Exception) { } }
}

/// Keeps reading a child's standard output so its pipe never fills (the companion logs to it).
public class OutputPump
{
    private readonly StringBuilder text = new StringBuilder();
    public static OutputPump Start(System.Diagnostics.Process process)
    {
        var pump = new OutputPump();
        var t = new Thread(delegate () { try { string line; while ((line = process.StandardOutput.ReadLine()) != null) lock (pump.text) pump.text.AppendLine(line); } catch (Exception) { } });
        t.IsBackground = true; t.Start();
        var e = new Thread(delegate () { try { process.StandardError.ReadToEnd(); } catch (Exception) { } });
        e.IsBackground = true; e.Start();
        return pump;
    }
    public string Text { get { lock (text) return text.ToString(); } }
}
'@

function Version-Of([string]$path) { ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion -split '\+')[0] }
function Publish([string]$project, [string]$out, [string]$version) {
    $props = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $out, '-m:1', '-nologo', '-v', 'q')
    if ($version) { $props += "-p:Version=$version" }
    & $Dotnet publish $project @props | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}
function Start-Hidden([string]$file, [string]$arguments) {
    $p = [Diagnostics.Process]::new()
    $p.StartInfo = [Diagnostics.ProcessStartInfo]::new($file)
    $p.StartInfo.UseShellExecute = $false; $p.StartInfo.CreateNoWindow = $true
    $p.StartInfo.RedirectStandardInput = $true; $p.StartInfo.RedirectStandardOutput = $true; $p.StartInfo.RedirectStandardError = $true
    $p.StartInfo.Arguments = $arguments
    if (-not $p.Start()) { throw "$file did not start" }
    return $p
}
function Owner-Of([int]$port) {
    $listen = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
    if ($listen.Count -gt 0) { return [int]$listen[0].OwningProcess }
    return 0
}
function Stop-Pid([int]$id) { if ($id -gt 0) { try { Stop-Process -Id $id -Force -ErrorAction Stop } catch { } } }
function Remove-Folder([string]$path) {
    for ($i = 0; $i -lt 40 -and (Test-Path -LiteralPath $path); $i++) { try { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 } }
}
function Assert([bool]$condition, [string]$what) { if (-not $condition) { throw "FAILED: $what" } }

$mock = $null; $server = $null; $results = @()
try {
    # The "old" EXE: the one given, or published from this checkout.
    if (-not $Exe) { Publish (Join-Path $repo 'companion\SoundpadWeb.Companion.csproj') (Join-Path $root 'old') ''; $Exe = Join-Path $root 'old\SoundpadWeb.exe' }
    $Exe = (Resolve-Path -LiteralPath $Exe).Path
    $old = Version-Of $Exe
    $parts = $old -split '\.'; $new = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
    Write-Output "Old version $old, new version $new"
    Publish (Join-Path $repo 'companion\SoundpadWeb.Companion.csproj') (Join-Path $root 'new') $new
    Publish (Join-Path $repo 'companion\update-fixture\UpdateFixture.csproj') (Join-Path $root 'fixture') $new
    $newExe = Join-Path $root 'new\SoundpadWeb.exe'; $fixtureExe = Join-Path $root 'fixture\SoundpadWeb.exe'
    Assert ((Version-Of $newExe) -eq $new) 'the new build has the new version'
    Assert ((Version-Of $fixtureExe) -eq $new) 'the fixture has the new version'

    $mock = Start-Hidden $harness '--mock-only'
    $pipeLine = $mock.StandardOutput.ReadLine()
    Assert ($pipeLine -match '^TEST_PIPE (\S+)') 'the mock Soundpad started'
    $pipe = $Matches[1]
    $server = [FakeReleaseServer]::new(); $server.Start()

    # $appName: the companion's file name (2.48: a versioned one is updated to the new version's name).
    # $plainOnly: the release has only SoundpadWeb.exe and SHA256.txt, as releases up to 2.47 did.
    function Run-Scenario([string]$name, [string]$serve, [int]$serveBytes, [string]$sha, [string]$mode, [string]$expect, [string]$windowFlag, [string]$appName = 'SoundpadWeb.exe', [switch]$plainOnly) {
        if ($Skip -contains $name) { return "SKIP $name" }
        $app = Join-Path $root "app-$name"; $data = Join-Path $root "data-$name"
        New-Item -ItemType Directory -Path $app, $data | Out-Null
        $appExe = Join-Path $app $appName
        Copy-Item -LiteralPath $Exe -Destination $appExe
        $stem = [IO.Path]::GetFileNameWithoutExtension($appName)
        $targetExe = $appExe
        if ($appName -match '^SoundpadWeb([- ])\d+\.\d+\.\d+\.exe$') { $targetExe = Join-Path $app "SoundpadWeb$($Matches[1])$new.exe" }
        $previousExe = Join-Path $app "$stem.previous.exe"
        $stalePrevious = Join-Path $app 'SoundpadWeb-0.0.1.previous.exe'
        if ($targetExe -ne $appExe) { Set-Content -LiteralPath $stalePrevious -Value 'an older previous version' -Encoding ascii }
        if ($expect -eq 'refused:nameTaken') { Set-Content -LiteralPath $targetExe -Value 'the host''s own copy' -Encoding ascii }
        $server.Versioned = -not $plainOnly
        [Threading.Monitor]::Enter($server.Requests); try { $server.Requests.Clear() } finally { [Threading.Monitor]::Exit($server.Requests) }
        if ($mode) { Set-Content -LiteralPath (Join-Path $app 'update-fixture.mode') -Value $mode -Encoding ascii }
        $bytes = [IO.File]::ReadAllBytes($serve)
        $server.Version = $new; $server.Exe = $bytes; $server.Size = $bytes.Length; $server.ServeBytes = $serveBytes
        $server.Sha = if ($sha) { $sha } else { (Get-FileHash -LiteralPath $serve -Algorithm SHA256).Hash }
        $launch = Join-Path $data 'launch.txt'
        $arguments = "$windowFlag --no-browser --dev --site $origin/ --pipe $pipe --data-dir `"$data`" --update-source http://127.0.0.1:$($server.Port)/ --launch-file `"$launch`""
        $proc = Start-Hidden $appExe $arguments
        $pump = [OutputPump]::Start($proc)
        $client = $null; $after = $null; $child = 0
        try {
            for ($i = 0; $i -lt 100 -and -not (Test-Path -LiteralPath $launch); $i++) { Start-Sleep -Milliseconds 200 }
            Assert (Test-Path -LiteralPath $launch) "$name : the companion started ($($pump.Text))"
            Assert ((Get-Content -LiteralPath $launch -Raw) -match '#host=(\d+)\.([a-f0-9]{64})') "$name : launch link"
            $port = [int]$Matches[1]; $token = $Matches[2]
            $client = [BridgeClient]::TryConnect($port, $token, $origin, 20000)
            Assert ($client -ne $null) "$name : connected over the loopback socket"
            $client.Command('updateCheck', '{}')
            Assert ($client.WaitFor('"state":"available"', 20000)) "$name : the check found $new ($($client.LastUpdate()))"
            $client.Command('updateInstall', "{`"version`":`"$new`",`"confirm`":true}")
            if ($expect -like 'refused:*') {
                $reason = $expect.Substring(8)
                Assert ($client.WaitFor("`"reason`":`"$reason`"", 60000)) "$name : refused with $reason ($($client.LastUpdate()))"
                Assert ((Version-Of $appExe) -eq $old) "$name : the EXE was not replaced"
                Assert (-not (Test-Path -LiteralPath $previousExe)) "$name : nothing was swapped"
                if ($expect -eq 'refused:nameTaken') { Assert ((Get-Content -LiteralPath $targetExe -Raw).Trim() -eq 'the host''s own copy') "$name : the file with the new name was left alone" }
                Assert (-not $proc.HasExited) "$name : the old companion keeps running"
                $after = [BridgeClient]::TryConnect($port, $token, $origin, 10000)
                Assert ($after -ne $null -and $after.WaitFor("`"companionVersion`":`"$old`"", 5000)) "$name : the same port and token answer"
                Assert (@(Get-ChildItem -LiteralPath $app -Filter '*.download' -Force).Count -eq 0) "$name : the refused download is gone"
            }
            else {
                Assert ($client.WaitFor('"type":"updating"', 60000)) "$name : pages were told the companion is updating"
                $wait = if ($expect -eq 'rolledBack') { 200 } else { 90 }
                Assert ($proc.WaitForExit($wait * 1000)) "$name : the old process finished its watchdog ($($pump.Text))"
                Assert ($proc.ExitCode -eq 0) "$name : the old process exited 0 ($($proc.ExitCode)): $($pump.Text)"
                $want = if ($expect -eq 'updated') { $new } else { $old }
                $after = [BridgeClient]::TryConnect($port, $token, $origin, 30000)
                Assert ($after -ne $null) "$name : the same port and token answer after the update ($($pump.Text))"
                Assert ($after.WaitFor("`"companionVersion`":`"$want`"", 10000)) "$name : version $want answers"
                $child = Owner-Of $port
                $installed = if ($expect -eq 'updated') { $targetExe } else { $appExe }
                Assert ((Version-Of $installed) -eq $want) "$name : $([IO.Path]::GetFileName($installed)) is version $want"
                if ($expect -eq 'updated') {
                    Assert ((Version-Of $previousExe) -eq $old) "$name : the previous EXE stays beside it"
                    $wantedExe = if ($plainOnly) { 'SoundpadWeb.exe' } else { $server.VersionedName }
                    $requests = @($server.Requests.ToArray())
                    Assert ($requests -contains "GET /releases/download/v$new/$wantedExe") "$name : $wantedExe was downloaded ($($requests -join ', '))"
                    if ($targetExe -ne $appExe) {
                        Assert (-not (Test-Path -LiteralPath $appExe)) "$name : the old versioned name is gone"
                        Assert (-not (Test-Path -LiteralPath $stalePrevious)) "$name : an older version's previous EXE was removed"
                    }
                }
                else {
                    Assert (Test-Path -LiteralPath (Join-Path $app "$stem.failed-$new.exe")) "$name : the failed EXE was put aside"
                    if ($targetExe -ne $appExe) {
                        Assert (-not (Test-Path -LiteralPath $targetExe)) "$name : nothing has the new name"
                        Assert (Test-Path -LiteralPath $stalePrevious) "$name : nothing was cleaned without a successful start"
                    }
                }
                Assert (@(Get-ChildItem -LiteralPath $data -Filter 'update-handoff.v1.bin*' -Force).Count -eq 0) "$name : the hand-over is gone"
            }
            return "PASS $name"
        }
        finally {
            if ($client) { $client.Dispose() }
            if ($after) { $after.Dispose() }
            Stop-Pid $child
            if (-not $proc.HasExited) { try { $proc.StandardInput.WriteLine(''); [void]$proc.WaitForExit(15000) } catch { } }
            if (-not $proc.HasExited) { $proc.Kill() }
            # Anything a relaunch left listening (it would hold the data folder).
            if (Test-Path -LiteralPath (Join-Path $data 'launch.txt')) {
                if ((Get-Content -LiteralPath (Join-Path $data 'launch.txt') -Raw) -match '#host=(\d+)\.') { Stop-Pid (Owner-Of ([int]$Matches[1])) }
            }
            Start-Sleep -Milliseconds 500
            Remove-Folder $app; Remove-Folder $data
        }
    }

    $results += Run-Scenario 'success' $newExe -1 '' '' 'updated' '--no-window'
    $results += Run-Scenario 'wrong-hash' $newExe -1 ('0' * 64) '' 'refused:hashMismatch' '--no-window'
    $results += Run-Scenario 'truncated' $newExe 1048576 '' '' 'refused:sizeMismatch' '--no-window'
    $results += Run-Scenario 'wrong-version' $Exe -1 '' '' 'refused:versionMismatch' '--no-window'
    $results += Run-Scenario 'probe-fail' $fixtureExe -1 '' 'probe-fail' 'refused:blocked' '--no-window'
    $results += Run-Scenario 'never-ready' $fixtureExe -1 '' '' 'rolledBack' '--no-window'
    $results += Run-Scenario 'plain-only-release' $newExe -1 '' '' 'updated' '--no-window' -plainOnly
    $results += Run-Scenario 'versioned' $newExe -1 '' '' 'updated' '--no-window' "SoundpadWeb-$old.exe"
    $results += Run-Scenario 'versioned-space' $newExe -1 '' '' 'updated' '--no-window' "SoundpadWeb $old.exe"
    $results += Run-Scenario 'versioned-name-taken' $newExe -1 '' '' 'refused:nameTaken' '--no-window' "SoundpadWeb-$old.exe"
    $results += Run-Scenario 'versioned-never-ready' $fixtureExe -1 '' '' 'rolledBack' '--no-window' "SoundpadWeb-$old.exe"
    if ($Hidden) { $results += Run-Scenario 'success-hidden-window' $newExe -1 '' '' 'updated' '--hidden-window' }
    $results | ForEach-Object { Write-Output $_ }
    Write-Output 'PASS: self-update swap, verification refusals and rollback, with a local fake release server only. No internet, no real Soundpad, no window.'
}
catch {
    $results | ForEach-Object { Write-Output $_ }
    Write-Output ('::error title=Update swap test::' + $_.Exception.Message)
    throw
}
finally {
    if ($server) { $server.Stop() }
    if ($mock -and -not $mock.HasExited) { try { $mock.StandardInput.WriteLine('exit'); [void]$mock.WaitForExit(5000) } catch { }; if (-not $mock.HasExited) { $mock.Kill() } }
    Remove-Folder $root
}
