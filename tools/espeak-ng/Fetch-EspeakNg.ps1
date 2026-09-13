<#
.SYNOPSIS
    Downloads and extracts the espeak-ng distribution for phonemization development.
.DESCRIPTION
    Fetches the expected espeak-ng Windows MSI, verifies SHA-256 checksums, and extracts
    binaries and data into tools/espeak-ng/. Upstream no longer publishes a win-x64 zip.
.NOTES
    espeak-ng is GPL-3.0-or-later. Used for development only; not shipped.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

$manifest = Get-Content (Join-Path $scriptDir 'espeak-ng.manifest.json') | ConvertFrom-Json
$version = $manifest.version
$assetName = if ($manifest.windows_asset) { [string]$manifest.windows_asset } else { "espeak-ng-$version-win-x64.zip" }
$releaseBase = "https://github.com/espeak-ng/espeak-ng/releases/download/$version"
$assetUrl = "$releaseBase/$assetName"
$assetPath = Join-Path $scriptDir $assetName

Write-Host "Fetching espeak-ng $version ($assetName)..."

if (-not (Test-Path $assetPath)) {
    Invoke-WebRequest -Uri $assetUrl -OutFile $assetPath -UseBasicParsing
    Write-Host "Downloaded: $assetName"
} else {
    Write-Host "Using cached: $assetName"
}

$extractDir = Join-Path $scriptDir "extracted"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
New-Item -ItemType Directory -Path $extractDir | Out-Null

$extension = [System.IO.Path]::GetExtension($assetName).ToLowerInvariant()
if ($extension -eq '.msi') {
    # Prefer Sysnative so 32-bit PowerShell reaches the 64-bit msiexec, not SysWOW64.
    $msiexec = Join-Path $env:SystemRoot 'Sysnative\msiexec.exe'
    if (-not (Test-Path -LiteralPath $msiexec)) {
        $msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    }

    # Pass one msiexec argument string. An ArgumentList array with quoted TARGETDIR
    # makes msiexec return 1639 (ERROR_INVALID_COMMAND_LINE).
    $process = Start-Process -FilePath $msiexec -ArgumentList "/a `"$assetPath`" TARGETDIR=`"$extractDir`" /qn" -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "msiexec administrative extract failed with exit code $($process.ExitCode)."
    }
} elseif ($extension -eq '.zip') {
    Expand-Archive -Path $assetPath -DestinationPath $extractDir -Force
} else {
    throw "Unsupported espeak-ng asset type: $assetName"
}

foreach ($fileName in @('espeak-ng.exe', 'libespeak-ng.dll')) {
    $src = Get-ChildItem -Path $extractDir -Filter $fileName -Recurse -File | Select-Object -First 1
    if (-not $src) { throw "File not found in archive: $fileName" }

    $dest = Join-Path $scriptDir $fileName
    Copy-Item $src.FullName $dest -Force

    $expectedHash = $manifest.files.$fileName.sha256
    $actualHash = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        Remove-Item $dest -Force
        throw "SHA-256 mismatch for $fileName. Expected: $expectedHash, Got: $actualHash"
    }
    Write-Host "  Verified: $fileName"
}

$dataSrc = Get-ChildItem -Path $extractDir -Directory -Filter "espeak-ng-data" -Recurse | Select-Object -First 1
if ($dataSrc) {
    $dataDest = Join-Path $scriptDir "espeak-ng-data"
    if (Test-Path $dataDest) { Remove-Item $dataDest -Recurse -Force }
    Copy-Item $dataSrc.FullName $dataDest -Recurse
    Write-Host "  Extracted: espeak-ng-data/"
} else {
    Write-Warning "espeak-ng-data directory not found in archive."
}

Remove-Item $extractDir -Recurse -Force
Write-Host "espeak-ng $version ready."
