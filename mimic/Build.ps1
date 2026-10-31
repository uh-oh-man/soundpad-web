param([switch]$SkipTests,[string]$OutputDirectory='Release/VoiceReplayStudio-V1.1')
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'
$dotnet=Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if(-not(Test-Path $dotnet)){$dotnet='dotnet'}
Push-Location $PSScriptRoot
try {
    if(-not $SkipTests) {
        & $dotnet run --project tests/VoiceReplay.Tests -c Release
        if($LASTEXITCODE -ne 0){throw 'Tests failed'}
    }
    & $dotnet publish src/VoiceReplay.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $OutputDirectory
    if($LASTEXITCODE -ne 0){throw 'Publish failed'}
    Copy-Item README.md,THIRD-PARTY.md,TEST-RESULTS.md -Destination $OutputDirectory
    Copy-Item Licenses -Destination $OutputDirectory -Recurse -Force
} finally {Pop-Location}
