#Requires -Version 7.0
<#
.SYNOPSIS
    Optimize a Trackdub model with Microsoft Olive (optimize) on the current machine.

.DESCRIPTION
    Bootstraps Python 3.10+ and an isolated venv, installs olive-ai, then calls the
    Trackdub.Tools modellab pipeline. All arguments after -- are forwarded to modellab.

.EXAMPLE
    .\tools\trackdub-optimize.ps1 -- --model openai/whisper-tiny --model-root whisper-tiny-genai --no-benchmark
#>

param(
    [Parameter(ValueFromRemainingArguments)]
    [string[]] $ModelLabArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$VenvPath = Join-Path $env:LOCALAPPDATA 'Trackdub\tools\olive-env'
$PythonExe = Join-Path $VenvPath 'Scripts\python.exe'
$PipExe    = Join-Path $VenvPath 'Scripts\pip.exe'
$OliveExe  = Join-Path $VenvPath 'Scripts\olive.exe'

# ---------------------------------------------------------------------------
# 1. Ensure Python 3.10+
# ---------------------------------------------------------------------------
function Get-PythonExe {
    foreach ($candidate in @('python', 'python3')) {
        try {
            $ver = & $candidate --version 2>&1
            if ($ver -match 'Python (\d+)\.(\d+)') {
                $major = [int]$Matches[1]; $minor = [int]$Matches[2]
                if ($major -gt 3 -or ($major -eq 3 -and $minor -ge 10)) {
                    return $candidate
                }
            }
        } catch { }
    }
    return $null
}

$systemPython = Get-PythonExe
if (-not $systemPython) {
    Write-Host 'Python not found — installing via winget…'
    winget install Python.Python.3.11 --silent --accept-package-agreements --accept-source-agreements
    $systemPython = Get-PythonExe
    if (-not $systemPython) {
        Write-Error 'Python 3.10+ could not be installed automatically. Install from https://python.org and retry.'
        exit 1
    }
}

# ---------------------------------------------------------------------------
# 2. Create venv if absent
# ---------------------------------------------------------------------------
if (-not (Test-Path $PythonExe)) {
    Write-Host "Creating venv at $VenvPath…"
    & $systemPython -m venv $VenvPath
    if ($LASTEXITCODE -ne 0) { Write-Error 'Failed to create venv.'; exit 1 }
}

# ---------------------------------------------------------------------------
# 3. Upgrade pip, then install / upgrade olive-ai
# ---------------------------------------------------------------------------
Write-Host 'Upgrading pip…'
& $PythonExe -m pip install --upgrade pip --quiet
if ($LASTEXITCODE -ne 0) { Write-Error 'pip upgrade failed.'; exit 1 }

Write-Host 'Installing olive-ai…'
& $PipExe install olive-ai --quiet --upgrade
if ($LASTEXITCODE -ne 0) { Write-Error 'pip install olive-ai failed.'; exit 1 }

# ---------------------------------------------------------------------------
# 4. Locate Trackdub.Tools
# ---------------------------------------------------------------------------
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir

$PublishedExe = Join-Path $RepoRoot 'src\Trackdub.Tools\bin\Trackdub.Tools.exe'
if (Test-Path $PublishedExe) {
    $ToolsExe    = $PublishedExe
    $ToolsPrefix = @($ToolsExe)
} else {
    $ToolsProject = Join-Path $RepoRoot 'src\Trackdub.Tools\Trackdub.Tools.csproj'
    if (-not (Test-Path $ToolsProject)) {
        Write-Error "Cannot locate Trackdub.Tools (tried $PublishedExe and dotnet run)."
        exit 1
    }
    $ToolsPrefix = @('dotnet', 'run', '--project', $ToolsProject, '--')
}

# ---------------------------------------------------------------------------
# 5. Invoke modellab
# ---------------------------------------------------------------------------
$mlArgs = @('modellab', '--python', $PythonExe, '--olive', $OliveExe) + $ModelLabArgs

Write-Host ''
Write-Host "Running: $($ToolsPrefix -join ' ') $($mlArgs -join ' ')"
Write-Host ''

& $ToolsPrefix[0] @($ToolsPrefix | Select-Object -Skip 1) @mlArgs
exit $LASTEXITCODE
