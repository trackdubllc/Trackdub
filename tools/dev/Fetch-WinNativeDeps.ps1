#requires -Version 5.1
<#
.SYNOPSIS
  Download Windows-native binaries that are too large for Git (and previously used Git LFS).

.DESCRIPTION
  Download URLs and archive layout are defined in the git-tracked manifest:
    runtime/win-native-deps.manifest.json

  - libmpv-2.dll: zhongfly/mpv-winbuild dev .7z (extracted with portable 7zr)
  - ffmpeg.exe / ffprobe.exe: fetched by default (see manifest per RID)
  - uv.exe: optional bootstrap tool (see manifest)

  On ARM64 Windows, artifacts go under native/win-arm64 and tools/win-arm64.
  On x64 Windows, under native/win-x64 and tools/win-x64.

  Override architecture with -Architecture for CI (e.g. fetch ARM64 deps on an AMD64 runner).

 Run from repo root or any directory; paths are resolved relative to this script.

  macOS libmpv uses the same manifest with osx-* RIDs; run tools/dev/Fetch-MacNativeDeps.ps1 on a Mac.
#>
param(
    [ValidateSet("Auto", "X64", "Arm64")]
    [string] $Architecture = "Auto",
    [switch] $SkipFfmpeg,
    [switch] $IncludeFfmpeg,
    [string] $FfmpegVersion = $env:FFMPEG_VERSION
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($SkipFfmpeg -and $IncludeFfmpeg) {
    throw "Use either -SkipFfmpeg or -IncludeFfmpeg, not both."
}

if ($IncludeFfmpeg) {
    Write-Warning "-IncludeFfmpeg is deprecated. FFmpeg and ffprobe are fetched by default; use -SkipFfmpeg to opt out."
}

function Invoke-FileDownload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Uri,
        [Parameter(Mandatory = $true)]
        [string] $OutFile,
        [int] $MaxAttempts = 3
    )

    $lastError = $null
    $curl = Get-Command "curl.exe" -ErrorAction SilentlyContinue

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile
            return
        }
        catch {
            $lastError = $_
            Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue

            if ($null -ne $curl) {
                try {
                    & $curl.Source -fL --output $OutFile $Uri | Out-Null
                    if ($LASTEXITCODE -eq 0 -and (Test-Path $OutFile)) {
                        return
                    }

                    throw "curl.exe exited with code $LASTEXITCODE while downloading $Uri"
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

$targetArch = Get-TargetArchitecture
$rid = if ($targetArch -eq "Arm64") { "win-arm64" } else { "win-x64" }

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

$SevenZipRemote = $manifest.sevenZipPortableExeUrl
$LibmpvDevArchiveUrl = $runtimeEntry.libmpvDevArchiveUrl
$libmpvExtractMember = $runtimeEntry.libmpvExtractMember
if ([string]::IsNullOrWhiteSpace($libmpvExtractMember)) {
    $libmpvExtractMember = "libmpv-2.dll"
}
$UvZipUrl = $runtimeEntry.uvZipUrl

$NativeDir = Join-Path $RepoRoot "native/$rid"
$ToolsDir = Join-Path $RepoRoot "tools/$rid"

New-Item -ItemType Directory -Force -Path $NativeDir | Out-Null
New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

Write-Host "fetch-win-native-deps: architecture=$targetArch rid=$rid manifest=$ManifestPath"

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("trackdub-fetch-" + [Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

try {
    $sevenZip = Join-Path $scratch "7zr.exe"
    Write-Host "Downloading 7zr from $SevenZipRemote"
    Invoke-FileDownload -Uri $SevenZipRemote -OutFile $sevenZip

    $libmpvArc = Join-Path $scratch "mpv-dev.7z"
    Write-Host "Downloading libmpv dev archive from $LibmpvDevArchiveUrl"
    Invoke-FileDownload -Uri $LibmpvDevArchiveUrl -OutFile $libmpvArc

    Push-Location $scratch
    try {
        & $sevenZip x $libmpvArc $libmpvExtractMember -y | Out-Host
    }
    finally {
        Pop-Location
    }

    $extractedDll = Join-Path $scratch $libmpvExtractMember
    if (-not (Test-Path $extractedDll)) {
        throw "Extracted file '$libmpvExtractMember' not found after extracting $libmpvArc"
    }
    Move-Item -Path $extractedDll -Destination (Join-Path $NativeDir "libmpv-2.dll") -Force
    Write-Host "Wrote $(Join-Path $NativeDir 'libmpv-2.dll')"

    Write-Host "Downloading uv from $UvZipUrl"
    $uvZip = Join-Path $scratch "uv.zip"
    Invoke-FileDownload -Uri $UvZipUrl -OutFile $uvZip
    $uvTemp = Join-Path $scratch "uv_temp"
    Expand-Archive -Path $uvZip -DestinationPath $uvTemp -Force
    $uvExe = Get-ChildItem -Path $uvTemp -Filter "uv.exe" -Recurse | Select-Object -First 1
    if (-not $uvExe) {
        throw "Could not find uv.exe in uv archive"
    }
    Move-Item -Path $uvExe.FullName -Destination (Join-Path $ToolsDir "uv.exe") -Force
    Write-Host "Wrote $(Join-Path $ToolsDir 'uv.exe')"

    if (-not $SkipFfmpeg) {
        $ffmpegUrl = $runtimeEntry.ffmpegZipUrl
        if ($targetArch -eq "X64" -and -not [string]::IsNullOrWhiteSpace($FfmpegVersion)) {
            $ffmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$FfmpegVersion/ffmpeg-$FfmpegVersion-essentials_build.zip"
            Write-Host "Downloading FFmpeg (x64 codex override $FfmpegVersion) from $ffmpegUrl"
        }
        else {
            Write-Host "Downloading FFmpeg from $ffmpegUrl"
        }
        $ffmpegZip = Join-Path $scratch "ffmpeg.zip"
        Invoke-FileDownload -Uri $ffmpegUrl -OutFile $ffmpegZip
        $ffmpegTemp = Join-Path $scratch "ffmpeg_temp"
        Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegTemp -Force
        $ffmpegExe = Get-ChildItem -Path $ffmpegTemp -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
        if (-not $ffmpegExe) {
            throw "Could not find ffmpeg.exe in FFmpeg archive"
        }
        Move-Item -Path $ffmpegExe.FullName -Destination (Join-Path $ToolsDir "ffmpeg.exe") -Force
        Write-Host "Wrote $(Join-Path $ToolsDir 'ffmpeg.exe')"

        $ffprobeExe = Get-ChildItem -Path $ffmpegTemp -Filter "ffprobe.exe" -Recurse | Select-Object -First 1
        if (-not $ffprobeExe) {
            throw "Could not find ffprobe.exe in FFmpeg archive (expected next to ffmpeg in GyanD/BtbN builds)"
        }
        Move-Item -Path $ffprobeExe.FullName -Destination (Join-Path $ToolsDir "ffprobe.exe") -Force
        Write-Host "Wrote $(Join-Path $ToolsDir 'ffprobe.exe')"
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
