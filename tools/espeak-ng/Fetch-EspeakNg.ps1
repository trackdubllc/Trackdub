<#
.SYNOPSIS
    Downloads and extracts the espeak-ng distribution for phonemization development.
.DESCRIPTION
    Fetches the expected espeak-ng release, verifies SHA-256 checksums, and extracts
    binaries and data into tools/espeak-ng/.
.NOTES
    espeak-ng is GPL-3.0-or-later. Used for development only; not shipped.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

$manifest = Get-Content (Join-Path $scriptDir 'espeak-ng.manifest.json') | ConvertFrom-Json
$version = $manifest.version
$releaseBase = "https://github.com/espeak-ng/espeak-ng/releases/download/$version"
$zipName = "espeak-ng-$version-win-x64.zip"
$zipUrl = "$releaseBase/$zipName"
$zipPath = Join-Path $scriptDir $zipName

Write-Host "Fetching espeak-ng $version..."

if (-not (Test-Path $zipPath)) {
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
    Write-Host "Downloaded: $zipName"
} else {
    Write-Host "Using cached: $zipName"
}

# Extract
$extractDir = Join-Path $scriptDir "extracted"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

# Copy expected files and verify checksums
foreach ($fileName in @('espeak-ng.exe', 'libespeak-ng.dll')) {
    $src = Get-ChildItem -Path $extractDir -Filter $fileName -Recurse | Select-Object -First 1
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

# Copy data directory
$dataSrc = Get-ChildItem -Path $extractDir -Directory -Filter "espeak-ng-data" -Recurse | Select-Object -First 1
if ($dataSrc) {
    $dataDest = Join-Path $scriptDir "espeak-ng-data"
    if (Test-Path $dataDest) { Remove-Item $dataDest -Recurse -Force }
    Copy-Item $dataSrc.FullName $dataDest -Recurse
    Write-Host "  Extracted: espeak-ng-data/"
} else {
    Write-Warning "espeak-ng-data directory not found in archive."
}

# Cleanup
Remove-Item $extractDir -Recurse -Force
Write-Host "espeak-ng $version ready."
