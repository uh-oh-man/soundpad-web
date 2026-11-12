# -Hidden runs the companion window without ever showing it (--hidden-window), for checks on a PC someone is using.
param([string]$Executable = 'artifacts/release/SoundpadWeb.exe', [switch]$UseEmbeddedWebsite, [switch]$Hidden)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Executable).Path
$workspacePath = (Get-Location).Path
$smokePath = Join-Path ([System.IO.Path]::GetTempPath()) ('soundpad-web-smoke-' + [guid]::NewGuid().ToString('N'))
$process = [System.Diagnostics.Process]::new()
try {
    $process.StartInfo = [System.Diagnostics.ProcessStartInfo]::new($exePath)
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $arguments = @('--no-browser','--pipe',('soundpad-web-smoke-' + [guid]::NewGuid().ToString('N')),'--data-dir',$smokePath)
    if (-not $UseEmbeddedWebsite) { $arguments += @('--dev','--site','http://127.0.0.1:5173/') }
    if ($Hidden) { $arguments += '--hidden-window' }
    foreach ($argument in $arguments) { $process.StartInfo.ArgumentList.Add($argument) }
    if (-not $process.Start()) { throw 'EXE failed to start' }
    $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if ($line -notmatch 'loopback port (\d+)') { throw 'EXE did not report a loopback listener' }
    $port = [int]$Matches[1]
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port)
    if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or $listeners[0].OwningProcess -ne $process.Id) { throw 'Unexpected listener binding' }
    $process.StandardInput.WriteLine('')
    # Since 2.36 the window's WebView2 also has to let go of its profile; allow for a slow runner.
    if (-not $process.WaitForExit(20000)) { throw 'Graceful shutdown timed out' }
    if ($process.ExitCode -ne 0) { throw 'EXE exited unsuccessfully' }
    # The EXE is a desktop (GUI) app since 2.36: PowerShell only captures such an app's output mid-pipeline.
    $licenses = @(& $exePath --licenses | Write-Output)
    if (($licenses -join "`n") -notmatch '2017 Leppsoft' -or ($licenses -join "`n") -notmatch 'Microsoft' -or ($licenses -join "`n") -notmatch 'Mark Heath' -or ($licenses -join "`n") -notmatch 'Andrew Ward') { throw 'Embedded notices missing' }
    Write-Output 'PASS: standalone EXE startup, numeric loopback-only listener, clean Enter shutdown, embedded notices. No browser or Soundpad launched; no audio played.'
} catch {
    # A public annotation, so a failed CI run says why without a login.
    $output = ''
    try { if ($process.HasExited) { $output = $process.StandardOutput.ReadToEnd() } } catch { }
    Write-Output ("::error title=EXE smoke test::" + $_.Exception.Message + ' | ' + (($output -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 6) -join ' | '))
    throw
} finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
    $resolvedSmoke = [System.IO.Path]::GetFullPath($smokePath)
    $expectedRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedSmoke.StartsWith($expectedRoot,[System.StringComparison]::OrdinalIgnoreCase) -and [System.IO.Path]::GetFileName($resolvedSmoke).StartsWith('soundpad-web-smoke-') -and (Test-Path -LiteralPath $resolvedSmoke)) { Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force }
}
