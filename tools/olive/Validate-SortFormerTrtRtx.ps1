#Requires -Version 7.0
<#
.SYNOPSIS
    Validate TRT-RTX optimization of the SortFormer diarization model using Microsoft Olive.

.DESCRIPTION
    Runs the Olive TRT-RTX recipe for the SortFormer 4-speaker diarization encoder
    (with SkipLayerNormalization + BiasGelu fusion pre-passes), then stages the
    result for the C# SortFormerDiarizationEngine to load.

    Pass -Mxfp8 to select the MXFP8-quantized recipe (Hopper/Ada + Blackwell).
    Without it, the fp16 recipe is used (works on every TensorRT-RTX-capable GPU).

    Requires:
      - NVIDIA GPU with TRT-RTX (NvTensorRTRTXExecutionProvider) support
      - Model files already downloaded:
          models/sortformer/cgus-diar_streaming_sortformer_4spk-v2.1-onnx/onnx/model.onnx
      - olive-ai[nvmo] + nvidia-modelopt[onnx] installed (Bootstrap-TrtRtxOliveVenv.ps1 auto-runs)

    On success, records results to build/sortformer-4spk-trtrtx-validation.json.
    Run .\tools\olive\Flip-TrtRtxAsrDiarization.ps1 to apply manifest + test changes.

.EXAMPLE
    .\tools\olive\Validate-SortFormerTrtRtx.ps1
    .\tools\olive\Validate-SortFormerTrtRtx.ps1 -SkipLatency
    .\tools\olive\Validate-SortFormerTrtRtx.ps1 -Mxfp8 -SkipLatency
#>

param(
    [switch] $SkipLatency,
    [switch] $Mxfp8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir      = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot       = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$VenvPath       = Join-Path $env:LOCALAPPDATA 'Trackdub\tools\olive-env-tensorrtrtx'
$OliveExe       = Join-Path $VenvPath 'Scripts\olive.exe'
$BuildDir       = Join-Path $RepoRoot 'build'
$Precision      = if ($Mxfp8) { 'mxfp8' } else { 'fp16' }
$ResultFile     = Join-Path $BuildDir "sortformer-4spk-trtrtx-validation.json"

# ---------------------------------------------------------------------------
# Model layout (matches bundled-models.manifest.json sortformer entry)
# ---------------------------------------------------------------------------
$modelRoot      = Join-Path $RepoRoot 'models\sortformer\cgus-diar_streaming_sortformer_4spk-v2.1-onnx'
$modelSrc       = Join-Path $modelRoot 'onnx\model.onnx'
$recipeDir      = Join-Path $RepoRoot 'resources\olive-recipes\cgus-diar_streaming_sortformer_4spk-v2.1-onnx\NvTensorRtRtx'
$recipeSrc      = Join-Path $recipeDir "encoder_trtrtx_$Precision.json"
$encoderOutputDirName = "sortformer-4spk-onnx_encoder_trtrtx_$Precision"
$stagingDirName      = "sortformer-4spk-onnx-trtrtx-validated-$Precision"

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
if (-not (Test-Path $OliveExe)) {
    Write-Warning "olive.exe not found at $OliveExe. Bootstrapping TRT-RTX olive venv (olive-ai[nvmo])..."
    try { & (Join-Path $PSScriptRoot 'Bootstrap-TrtRtxOliveVenv.ps1') }
    catch { Write-Host "Failed to bootstrap olive-ai[nvmo] venv: $_" -ForegroundColor Red; exit 1 }
    if (-not (Test-Path $OliveExe)) {
        Write-Error "olive.exe still not found at $OliveExe after bootstrap."
        exit 1
    }
}

if (-not (Test-Path $modelSrc)) {
    Write-Error "SortFormer encoder model not found: $modelSrc`nDownload with: dotnet run --project src/Trackdub.Tools -- ingest --model cgus/diar_streaming_sortformer_4spk-v2.1-onnx"
    exit 1
}

if (-not (Test-Path $recipeSrc)) {
    Write-Error "Olive recipe not found: $recipeSrc"
    exit 1
}

# ---------------------------------------------------------------------------
# Patch recipe: substitute ${MODEL_ROOT} with the absolute model path
# ---------------------------------------------------------------------------
$TempDir = Join-Path $env:TEMP "trackdub-olive-trtrtx-sortformer-$([System.Diagnostics.Process]::GetCurrentProcess().Id)"
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

function Resolve-Recipe {
    param([string] $SrcPath, [string] $DestPath)
    $content = Get-Content -Raw $SrcPath
    $content = $content -replace '\$\{MODEL_ROOT\}', ($modelRoot -replace '\\', '/')
    $content = $content -replace '\$\{ENCODER_OUTPUT_DIR\}', ("build/$encoderOutputDirName" -replace '\\', '/')
    Set-Content -Path $DestPath -Value $content -Encoding UTF8
}

$encoderRecipeDst = Join-Path $TempDir "encoder_trtrtx_$Precision.json"
$latencyRecipeDst = Join-Path $TempDir 'eval_latency.json'
Resolve-Recipe $recipeSrc $encoderRecipeDst
Resolve-Recipe (Join-Path $recipeDir 'eval_latency.json') $latencyRecipeDst

$origDir = Get-Location
Set-Location $RepoRoot

$results = [ordered]@{
    model_id       = 'cgus/diar_streaming_sortformer_4spk-v2.1-onnx'
    model_root     = $modelRoot
    precision      = $Precision
    timestamp_utc  = $null
    encoder        = $null
    latency        = $null
    staging_dir    = $null
    pass           = $false
}

try {
    # ---------------------------------------------------------------------------
    # Step 1: Optimize encoder (fp16 or mxfp8 + SkipLayerNorm/BiasGelu fusion + TRT-RTX session params)
    # ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "=== SortFormer encoder optimization ($Precision + fusion + TRT-RTX session params) ===" -ForegroundColor Cyan
    & $OliveExe run --config $encoderRecipeDst
    if ($LASTEXITCODE -ne 0) { Write-Error "Encoder optimization failed (exit $LASTEXITCODE)."; exit 1 }
    $results.encoder = @{ status = 'ok'; output = "build/$encoderOutputDirName"; precision = $Precision }

    # ---------------------------------------------------------------------------
    # Step 2 (optional): encoder latency evaluation on synthetic input
    # ---------------------------------------------------------------------------
    if (-not $SkipLatency) {
        Write-Host ""
        Write-Host "=== Encoder latency evaluation on synthetic waveform ===" -ForegroundColor Cyan
        & $OliveExe run --config $latencyRecipeDst
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Latency evaluation failed (exit $LASTEXITCODE)."
            $results.latency = @{ status = 'failed' }
        } else {
            $results.latency = @{ status = 'ok' }
        }
    } else {
        Write-Host "Skipping encoder latency evaluation (-SkipLatency)." -ForegroundColor Yellow
        $results.latency = @{ status = 'skipped' }
    }

    # ---------------------------------------------------------------------------
    # Step 3: Stage combined output for the C# validation test
    # ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "=== Staging combined output for C# validation test ===" -ForegroundColor Cyan

    $StagingDir     = Join-Path $BuildDir $stagingDirName
    $StagingOnnxDir = Join-Path $StagingDir "onnx"
    New-Item -ItemType Directory -Force -Path $StagingOnnxDir | Out-Null

    $encoderOutputDir = Join-Path $BuildDir $encoderOutputDirName
    $encoderOnnxSrc   = Get-ChildItem $encoderOutputDir -Filter "*.onnx" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($encoderOnnxSrc) {
        Copy-Item $encoderOnnxSrc.FullName (Join-Path $StagingOnnxDir "model.onnx") -Force
        Get-ChildItem $encoderOnnxSrc.Directory -Filter "*.onnx.data" -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $StagingOnnxDir "model.onnx.data") -Force }
    } else {
        Write-Warning "No encoder *.onnx found in $encoderOutputDir - staging incomplete."
    }

    $results.staging_dir = $StagingDir
    $results.pass = ($null -ne $encoderOnnxSrc)

} finally {
    Set-Location $origDir
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Write result file
# ---------------------------------------------------------------------------
$results.timestamp_utc = (Get-Date).ToUniversalTime().ToString('o')
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
$results | ConvertTo-Json -Depth 4 | Set-Content -Path $ResultFile -Encoding UTF8

Write-Host ""
if ($results.pass) {
    Write-Host "PASS - TRT-RTX ($Precision) optimization succeeded for SortFormer 4-spk." -ForegroundColor Green
    Write-Host "Results written to: $ResultFile"
    Write-Host ""
    Write-Host "Staging directory: $($results.staging_dir)"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Add a TRT-RTX smoke test in tests/Trackdub.Inference.Tests/SortFormerDiarizationEngineTests.cs"
    Write-Host "     (mirror WhisperOnnxTrtRtxValidationTests.cs; load the staging dir via Discover())."
    Write-Host "  2. dotnet test tests/Trackdub.Inference.Tests --filter 'FullyQualifiedName~SortFormer'"
    Write-Host "  3. If that passes: run .\tools\olive\Flip-TrtRtxAsrDiarization.ps1 to enable trt-rtx in the manifest and tests."
} else {
    Write-Host "FAIL - see errors above." -ForegroundColor Red
    exit 1
}
