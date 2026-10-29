$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath 'dist/index.html') -or -not (Test-Path -LiteralPath 'artifacts/release/SoundpadWeb.exe')) { throw 'Build the website and publish the companion first.' }
New-Item -ItemType Directory -Force artifacts | Out-Null
Compress-Archive -Path 'dist/*' -DestinationPath 'artifacts/SoundpadWeb-static.zip' -Force
$sourceFiles = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0) { throw 'Git source enumeration failed.' }
$archivePath = Join-Path (Get-Location) 'artifacts/SoundpadWeb-source.zip'
$stream = [System.IO.File]::Open($archivePath,[System.IO.FileMode]::Create)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($stream,[System.IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($file in $sourceFiles) {
            if ($file -match '(^|/)(node_modules|bin|obj|artifacts|dist|\.tools|\.git|test-results)/') { throw "Generated path unexpectedly included: $file" }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path (Get-Location) $file),$file,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Write-Output "Packaged static website and $($sourceFiles.Count) source files."
