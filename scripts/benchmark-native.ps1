param([string]$Executable = 'artifacts/v2/SoundpadWeb.exe', [string]$Output = 'artifacts/v2/native-performance.json')
$ErrorActionPreference = 'Stop'
$taskRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('soundpad-web-perf-' + [guid]::NewGuid().ToString('N'))
function Start-IsolatedProcess([string]$Path, [string[]]$Arguments) {
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Path).Path)
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $process.StartInfo.ArgumentList.Add($argument) }
    if (-not $process.Start()) { throw 'Isolated benchmark process failed to start.' }
    return $process
}
function Measure-ProcessWindow($Process) {
    $Process.Refresh()
    $cpuBefore = $Process.TotalProcessorTime.TotalMilliseconds
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 8
    $Process.Refresh()
    $elapsedMs = $timer.Elapsed.TotalMilliseconds
    return @{
        elapsedMs = $elapsedMs
        cpuMs = $Process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
        cpuPercentOfOneCore = 100 * ($Process.TotalProcessorTime.TotalMilliseconds - $cpuBefore) / $elapsedMs
        workingSetMiB = $Process.WorkingSet64 / 1MB
        privateMemoryMiB = $Process.PrivateMemorySize64 / 1MB
    }
}
$mockProcess = $null
$companionProcess = $null
try {
    $mockProcess = Start-IsolatedProcess 'companion/harness/bin/Release/net10.0-windows10.0.19041.0/win-x64/SoundpadWeb.Harness.exe' @('--mock-only')
    $ready = $mockProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if ($ready -notmatch '^TEST_PIPE (soundpad-web-test-[a-f0-9]+)$') { throw 'Isolated mock did not start.' }
    $pipeName = $Matches[1]
    $companionProcess = Start-IsolatedProcess $Executable @('--no-browser','--dev','--site','http://127.0.0.1:5173/','--pipe',$pipeName,'--data-dir',$taskRoot)
    $startup = $companionProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if ($startup -notmatch 'loopback port') { throw 'Companion benchmark startup failed.' }
    Start-Sleep -Seconds 2
    $idle = Measure-ProcessWindow $companionProcess
    $mockProcess.StandardInput.WriteLine('play')
    Start-Sleep -Seconds 2
    $playing = Measure-ProcessWindow $companionProcess
    $report = @{
        scope = 'Packaged EXE, four-sound mock pipe, no browser or guests, no actual audio. CPU is percent of one core; short windows are approximate.'
        idle = $idle
        simulatedPlaying = $playing
        executableSha256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    } | ConvertTo-Json -Depth 5
    $report | Set-Content -LiteralPath $Output -Encoding utf8
    Write-Output $report
} finally {
    foreach ($process in @($companionProcess,$mockProcess)) {
        if ($null -eq $process) { continue }
        if (-not $process.HasExited) {
            $process.StandardInput.WriteLine('exit')
            if (-not $process.WaitForExit(5000)) { throw 'An isolated benchmark process did not exit gracefully.' }
        }
        $process.Dispose()
    }
    # Only these fixed app-owned files are created by this benchmark; no recursive deletion.
    foreach ($name in @('state.json','state.json.tmp','companion.lock')) {
        $ownedFile = Join-Path $taskRoot $name
        if (Test-Path -LiteralPath $ownedFile) { Remove-Item -LiteralPath $ownedFile }
    }
    if (Test-Path -LiteralPath $taskRoot) { Remove-Item -LiteralPath $taskRoot }
}
