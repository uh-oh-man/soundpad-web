param([string]$Executable = 'artifacts/release/SoundpadWeb.exe', [switch]$UseEmbeddedWebsite)
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
    foreach ($argument in $arguments) { $process.StartInfo.ArgumentList.Add($argument) }
    if (-not $process.Start()) { throw 'EXE failed to start' }
    $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if ($line -notmatch 'loopback port (\d+)') { throw 'EXE did not report a loopback listener' }
    $port = [int]$Matches[1]
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port)
    if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or $listeners[0].OwningProcess -ne $process.Id) { throw 'Unexpected listener binding' }
    $process.StandardInput.WriteLine('')
    if (-not $process.WaitForExit(10000)) { throw 'Graceful shutdown timed out' }
    if ($process.ExitCode -ne 0) { throw 'EXE exited unsuccessfully' }
    $licenses = & $exePath --licenses
    if (($licenses -join "`n") -notmatch '2017 Leppsoft' -or ($licenses -join "`n") -notmatch 'Microsoft') { throw 'Embedded notices missing' }
    Write-Output 'PASS: standalone EXE startup, numeric loopback-only listener, clean Enter shutdown, embedded notices. No browser or Soundpad launched; no audio played.'
} finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
    $resolvedSmoke = [System.IO.Path]::GetFullPath($smokePath)
    $expectedRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedSmoke.StartsWith($expectedRoot,[System.StringComparison]::OrdinalIgnoreCase) -and [System.IO.Path]::GetFileName($resolvedSmoke).StartsWith('soundpad-web-smoke-') -and (Test-Path -LiteralPath $resolvedSmoke)) { Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force }
}
