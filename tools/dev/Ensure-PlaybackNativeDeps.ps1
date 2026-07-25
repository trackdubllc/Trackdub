#requires -Version 5.1
<#
.SYNOPSIS
  Ensure libmpv is present under native/{rid}/ for playback in a repo or worktree root.

.DESCRIPTION
  Used by agent worktree creation and Avalonia build scripts. If libmpv is missing:
  1. Copy from -SourceRoot when that tree already has native/{rid}/libmpv-2.dll
  2. Otherwise run tools/dev/Fetch-WinNativeDeps.ps1 (-SkipFfmpeg for speed)
#>
Set-StrictMode -Version Latest

function Ensure-WindowsPlaybackNativeDeps {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [string] $SourceRoot
    )

    if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return
    }

    $root = [System.IO.Path]::GetFullPath($Root)
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') {
        'Arm64'
    }
    else {
        'X64'
    }

    $rid = if ($arch -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
    $libMpv = Join-Path $root "native\$rid\libmpv-2.dll"
    if (Test-Path -LiteralPath $libMpv) {
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($SourceRoot)) {
        $sourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
        $sourceLibMpv = Join-Path $sourceRoot "native\$rid\libmpv-2.dll"
        if (Test-Path -LiteralPath $sourceLibMpv) {
            $destDir = Join-Path $root "native\$rid"
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            Copy-Item -LiteralPath $sourceLibMpv -Destination (Join-Path $destDir 'libmpv-2.dll') -Force
            Write-Host "Copied playback native libmpv from $sourceRoot to $root"
            return
        }
    }

    $fetchScript = Join-Path $root 'tools\dev\Fetch-WinNativeDeps.ps1'
    if (-not (Test-Path -LiteralPath $fetchScript)) {
        Write-Warning "Missing $fetchScript; libmpv will not be bundled until Fetch-WinNativeDeps runs."
        return
    }

    Write-Host "Fetching Windows playback natives (libmpv) for $rid..." -ForegroundColor Yellow
    & $fetchScript -Architecture $arch -SkipFfmpeg
    if ($LASTEXITCODE -ne 0) {
        throw "Fetch-WinNativeDeps failed with exit code $LASTEXITCODE."
    }
}
