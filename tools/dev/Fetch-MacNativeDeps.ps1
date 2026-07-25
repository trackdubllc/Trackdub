#requires -Version 5.1
<#
.SYNOPSIS
  Download macOS libmpv binaries that are too large for Git.

.DESCRIPTION
  Download URLs and archive layout are defined in the git-tracked manifest:
    runtime/win-native-deps.manifest.json

  - libmpv.2.dylib: media-kit/libmpv-darwin-build tar.gz (extracted with tar)

  Artifacts go under native/osx-arm64 or native/osx-x64 at the repo root.

  Run on macOS from repo root or any directory; paths are resolved relative to this script.
#>
param(
    [ValidateSet("Auto", "X64", "Arm64")]
    [string] $Architecture = "Auto"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-FileDownload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Uri,
        [Parameter(Mandatory = $true)]
        [string] $OutFile,
        [int] $MaxAttempts = 3
    )

    $lastError = $null

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile
            return
        }
        catch {
            $lastError = $_
            Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue

            $curl = Get-Command "curl" -ErrorAction SilentlyContinue
            if ($null -eq $curl) {
                $curl = Get-Command "curl.exe" -ErrorAction SilentlyContinue
            }

            if ($null -ne $curl) {
                try {
                    & $curl.Source -fL --output $OutFile $Uri | Out-Null
                    if ($LASTEXITCODE -eq 0 -and (Test-Path $OutFile)) {
                        return
                    }

                    throw "curl exited with code $LASTEXITCODE while downloading $Uri"
                }
                catch {
                    $lastError = $_
                    Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
                }
            }

            if ($attempt -eq $MaxAttempts) {
                throw $lastError
            }

            $delaySeconds = [int][Math]::Pow(2, $attempt - 1)
            Write-Warning "Download attempt $attempt for $Uri failed: $($lastError.Exception.Message). Retrying in $delaySeconds second(s)."
            Start-Sleep -Seconds $delaySeconds
        }
    }

    throw $lastError
}

function Assert-LibmpvArchiveSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ArchivePath,
        [string] $ExpectedSha256
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        return
    }

    $expected = $ExpectedSha256.Trim()
    $actual = (Get-FileHash -Path $ArchivePath -Algorithm SHA256).Hash
    if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "libmpv archive integrity check failed. Expected SHA256: $expected, Actual: $actual"
    }

    Write-Host "libmpv archive SHA256 verified ($expected)"
}

function Get-TargetArchitecture {
    if ($Architecture -ne "Auto") {
        return $Architecture
    }

    $osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    switch ($osArchitecture) {
        "Arm64" { return "Arm64" }
        "X64" { return "X64" }
        default {
            throw "Unsupported OS architecture for this script: $osArchitecture (expected Arm64 or X64)."
        }
    }
}

if (-not $IsMacOS) {
    throw "Fetch-MacNativeDeps.ps1 must run on macOS."
}

$targetArch = Get-TargetArchitecture
$rid = if ($targetArch -eq "Arm64") { "osx-arm64" } else { "osx-x64" }

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$ManifestPath = Join-Path $RepoRoot "runtime/win-native-deps.manifest.json"
if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Missing native deps manifest: $ManifestPath"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported win-native-deps manifest schemaVersion: $($manifest.schemaVersion) (expected 1)."
}

$runtimeEntry = $manifest.runtimes.$rid
if ($null -eq $runtimeEntry) {
    throw "Manifest has no runtimes entry for RID '$rid'."
}

$LibmpvArchiveUrl = $runtimeEntry.libmpvDevArchiveUrl
$libmpvExtractMember = $runtimeEntry.libmpvExtractMember
if ([string]::IsNullOrWhiteSpace($libmpvExtractMember)) {
    $libmpvExtractMember = "lib/libmpv.2.dylib"
}

$NativeDir = Join-Path $RepoRoot "native/$rid"
New-Item -ItemType Directory -Force -Path $NativeDir | Out-Null

Write-Host "fetch-mac-native-deps: architecture=$targetArch rid=$rid manifest=$ManifestPath"

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("trackdub-fetch-mac-" + [Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

try {
    $archivePath = Join-Path $scratch "libmpv.tar.gz"
    Write-Host "Downloading libmpv archive from $LibmpvArchiveUrl"
    Invoke-FileDownload -Uri $LibmpvArchiveUrl -OutFile $archivePath
    Assert-LibmpvArchiveSha256 -ArchivePath $archivePath -ExpectedSha256 $runtimeEntry.libmpvDevArchiveSha256

    $extractRoot = Join-Path $scratch "extract"
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    tar -xzf $archivePath -C $extractRoot

    $extracted = Join-Path $extractRoot ($libmpvExtractMember -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $extracted)) {
        $extracted = Get-ChildItem -Path $extractRoot -Filter "libmpv*.dylib" -Recurse | Select-Object -First 1
        if ($null -eq $extracted) {
            throw "Could not find libmpv dylib after extracting $archivePath"
        }
        $extracted = $extracted.FullName
    }

    Move-Item -Path $extracted -Destination (Join-Path $NativeDir "libmpv.2.dylib") -Force
    Write-Host "Wrote $(Join-Path $NativeDir 'libmpv.2.dylib')"
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
