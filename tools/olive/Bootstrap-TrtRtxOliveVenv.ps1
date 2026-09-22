#Requires -Version 7.0
<#
.SYNOPSIS
    Bootstrap the isolated Python venv used by the TensorRT-RTX validate scripts.

.DESCRIPTION
    Creates %LOCALAPPDATA%\Trackdub\tools\olive-env-tensorrtrtx and installs:
      - olive-ai (with [nvmo] extras)
      - nvidia-modelopt[onnx] (TensorRT Model Optimizer, used by INT4/INT8 NVMO PTQ recipes)

    This venv is referenced by:
      - Validate-WhisperOnnxTrtRtx.ps1
      - Validate-SortFormerTrtRtx.ps1
      - Validate-NemotronAsrTrtRtx.ps1
      - Flip-WhisperOnnxTrtRtx.ps1
      - Flip-TrtRtxAsrDiarization.ps1

    Without the [nvmo] extras the TRT-RTX recipes fail with "No module named modelopt"
    or "ModelBuilder pass requires TensorRT Model Optimizer".

    Idempotent: re-running after the venv already exists verifies the installed
    packages and exits 0.

.PARAMETER Force
    Recreate the venv from scratch even if it already exists. Useful when
    upgrading olive-ai or recovering from a corrupt install.

.PARAMETER SkipModelopt
    Skip installing nvidia-modelopt. Only useful when you only need the
    fp16 path (no NVMO PTQ).

.EXAMPLE
    .\tools\olive\Bootstrap-TrtRtxOliveVenv.ps1
    .\tools\olive\Bootstrap-TrtRtxOliveVenv.ps1 -Force
#>

param(
    [switch] $Force,
    [switch] $SkipModelopt
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$VenvPath   = Join-Path $env:LOCALAPPDATA 'Trackdub\tools\olive-env-tensorrtrtx'
$PythonExe  = Join-Path $VenvPath 'Scripts\python.exe'
$PipExe     = Join-Path $VenvPath 'Scripts\pip.exe'
$OliveExe   = Join-Path $VenvPath 'Scripts\olive.exe'
# Keep in step with the Microsoft.ML.OnnxRuntime version the C# runtime ships.
$OnnxRuntimeVersion = '1.30.0'

# ---------------------------------------------------------------------------
# 1. Ensure we have a system Python 3.10+ to bootstrap with
# ---------------------------------------------------------------------------
function Get-PythonExe {
    foreach ($candidate in @('python', 'python3', 'py')) {
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
    Write-Error "Python 3.10+ not found on PATH. Install from https://python.org and retry."
    exit 1
}

Write-Host "Using system Python: $(& $systemPython --version 2>&1)"

# ---------------------------------------------------------------------------
# 2. (Re)create the venv
# ---------------------------------------------------------------------------
if ($Force -and (Test-Path $VenvPath)) {
    Write-Host "Removing existing venv at $VenvPath (-Force)..."
    Remove-Item -Recurse -Force $VenvPath
}

if (-not (Test-Path $PythonExe)) {
    Write-Host "Creating venv at $VenvPath..."
    & $systemPython -m venv $VenvPath
    if ($LASTEXITCODE -ne 0) { Write-Error "venv creation failed."; exit 1 }
}
else {
    Write-Host "Venv already exists at $VenvPath (use -Force to recreate)."
}

# ---------------------------------------------------------------------------
# 3. Upgrade pip + install olive-ai[nvmo]
# ---------------------------------------------------------------------------
Write-Host "Upgrading pip..."
& $PythonExe -m pip install --upgrade pip --quiet
if ($LASTEXITCODE -ne 0) { Write-Error "pip upgrade failed."; exit 1 }

Write-Host "Installing olive-ai[nvmo] (this pulls in onnx, transformers, and the modelopt bridge)..."
& $PipExe install --quiet --upgrade "olive-ai[nvmo]"
if ($LASTEXITCODE -ne 0) { Write-Error "pip install olive-ai[nvmo] failed."; exit 1 }

# olive-ai imports these without declaring them: `requests` (telemetry, at CLI startup) and
# `psutil` (OrtSessionParamsTuning).
& $PipExe install --quiet --upgrade requests psutil
if ($LASTEXITCODE -ne 0) { Write-Error "pip install requests psutil failed."; exit 1 }

# ---------------------------------------------------------------------------
# 4. Install TensorRT Model Optimizer (modelopt[onnx]) when not skipped
# ---------------------------------------------------------------------------
if (-not $SkipModelopt) {
    Write-Host "Installing nvidia-modelopt[onnx] (TensorRT Model Optimizer)..."
    try {
        & $PipExe install --quiet --upgrade "nvidia-modelopt[onnx]"
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "nvidia-modelopt[onnx] install failed (exit $LASTEXITCODE)."
            Write-Warning "NVMO PTQ recipes will not work. fp16 recipes still do."
            Write-Warning "Re-run with -Force after fixing network access, or with -SkipModelopt to suppress."
        }
    }
    catch {
        Write-Warning "nvidia-modelopt[onnx] install threw: $_"
        Write-Warning "Continuing — fp16 / ModelBuilder recipes still work without modelopt."
    }
}
else {
    Write-Host "Skipping nvidia-modelopt[onnx] install (-SkipModelopt)."
}

# ---------------------------------------------------------------------------
# 4b. Pin onnxruntime to an EP-ABI-capable build
# ---------------------------------------------------------------------------
# TensorRT RTX ships as a standalone EP ABI plugin DLL, registered with
# onnxruntime.register_execution_provider_library and discovered via get_ep_devices.
# onnxruntime-gpu 1.22 (pulled in by olive-ai[nvmo]) has neither API, so Olive rejects
# NvTensorRTRTXExecutionProvider as unavailable. Match the C# runtime's ORT version.
Write-Host "Pinning onnxruntime==$OnnxRuntimeVersion (EP ABI plugin support)..."
& $PipExe uninstall --yes --quiet onnxruntime-gpu 2>$null | Out-Null
# onnxruntime and onnxruntime-gpu install into the same onnxruntime/ package directory, so
# uninstalling the gpu wheel deletes files the CPU wheel's metadata still claims. Force a
# reinstall so pip restores them instead of reporting "already satisfied".
& $PipExe install --quiet --force-reinstall --no-deps "onnxruntime==$OnnxRuntimeVersion"
if ($LASTEXITCODE -ne 0) { Write-Error "pip install onnxruntime==$OnnxRuntimeVersion failed."; exit 1 }

# ---------------------------------------------------------------------------
# 5. Verify the install
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Verifying install..."
& $PythonExe -c "import olive; print('olive:', olive.__version__)" 2>&1 | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { Write-Error "olive import verification failed."; exit 1 }
if (-not $SkipModelopt) {
    & $PythonExe -c "import modelopt; print('modelopt:', modelopt.__version__)" 2>&1 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { Write-Warning "modelopt import verification failed. NVMO PTQ recipes will not work; fp16 recipes still do." }
    & $PythonExe -c "from modelopt.onnx.quantization.int4 import quantize as q4; print('modelopt.onnx.quantization.int4: importable')" 2>&1 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { Write-Warning "modelopt int4 quantization import failed." }
}

if (-not (Test-Path $OliveExe)) {
    Write-Warning "olive.exe not found at $OliveExe after install."
    exit 1
}

Write-Host ""
Write-Host "Bootstrap complete." -ForegroundColor Green
Write-Host "  Venv:    $VenvPath"
Write-Host "  Olive:   $OliveExe"
Write-Host "  Python:  $PythonExe"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  .\tools\olive\Validate-SortFormerTrtRtx.ps1"
Write-Host "  .\tools\olive\Validate-NemotronAsrTrtRtx.ps1"
