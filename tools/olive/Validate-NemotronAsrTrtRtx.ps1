#Requires -Version 7.0
<#
.SYNOPSIS
    Validate TRT-RTX optimization of the Nemotron 3.5 ASR streaming model using Microsoft Olive.

.DESCRIPTION
    Runs the Olive TRT-RTX recipes for the Nemotron ASR encoder and decoder_joint,
    each with SkipLayerNormalization + BiasGelu fusion pre-passes, then stages
    the result for the C# NemotronAsrOnnxAudioTranscriptionEngine to load.

    Pass -Mxfp8 to select the MXFP8-quantized recipes (Hopper/Ada + Blackwell).
    Without it, the fp16 recipes are used (works on every TensorRT-RTX-capable GPU).

    Requires:
      - NVIDIA GPU with TRT-RTX (NvTensorRTRTXExecutionProvider) support
      - Model files already downloaded:
          models/nemotron-3.5-asr-onnx/encoder.onnx
          models/nemotron-3.5-asr-onnx/decoder_joint.onnx
      - olive-ai[nvmo] + nvidia-modelopt[onnx] installed (Bootstrap-TrtRtxOliveVenv.ps1 auto-runs)

    On success, records results to build/nemotron-3.5-asr-trtrtx-<precision>-validation.json.
    Run .\tools\olive\Flip-TrtRtxAsrDiarization.ps1 to apply manifest + test changes.

.EXAMPLE
    .\tools\olive\Validate-NemotronAsrTrtRtx.ps1
    .\tools\olive\Validate-NemotronAsrTrtRtx.ps1 -SkipLatency
    .\tools\olive\Validate-NemotronAsrTrtRtx.ps1 -Mxfp8 -SkipLatency
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
$ResultFile     = Join-Path $BuildDir "nemotron-3.5-asr-trtrtx-validation.json"

# ---------------------------------------------------------------------------
# Model layout (matches bundled-models.manifest.json nemotron-asr entry)
# ---------------------------------------------------------------------------
# Path matches where Trackdub.Cli lands the model when TRACKDUB_MODEL_CACHE=$RepoRoot\models:
# the model id "tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx" gets split on "/" into two
# path segments. The bundled-models.manifest.json `root_path` resolves to a different layout
# (../../../../models/nemotron-3.5-asr-onnx); for the validate step we follow the download layout.
$modelRoot      = Join-Path $RepoRoot 'models\tonythethompson\nemotron-3.5-asr-streaming-0.6b-onnx'
$encoderSrc     = Join-Path $modelRoot 'encoder.onnx'
$decoderSrc     = Join-Path $modelRoot 'decoder_joint.onnx'
$recipeDir      = Join-Path $RepoRoot 'resources\olive-recipes\nemotron-3.5-asr-streaming-0.6b-onnx\NvTensorRtRtx'
$encoderRecipe  = Join-Path $recipeDir "encoder_trtrtx_$Precision.json"
$decoderRecipe  = Join-Path $recipeDir "decoder_joint_trtrtx_$Precision.json"
$encoderOutputDirName = "nemotron-3.5-asr-onnx_encoder_trtrtx_$Precision"
$decoderOutputDirName = "nemotron-3.5-asr-onnx_decoder_joint_trtrtx_$Precision"
$stagingDirName       = "nemotron-3.5-asr-onnx-trtrtx-validated-$Precision"

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
if (-not (Test-Path $OliveExe)) {
    Write-Warning "olive.exe not found at $OliveExe. Bootstrapping TRT-RTX olive venv (olive-ai[nvmo])..."
    & (Join-Path $PSScriptRoot 'Bootstrap-TrtRtxOliveVenv.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to bootstrap olive-ai[nvmo] venv."; exit 1 }
    if (-not (Test-Path $OliveExe)) {
        Write-Error "olive.exe still not found at $OliveExe after bootstrap."
        exit 1
    }
}

if (-not (Test-Path $encoderSrc)) {
    Write-Error "Nemotron encoder model not found: $encoderSrc`nDownload with: dotnet run --project src/Trackdub.Tools -- ingest --model tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx"
    exit 1
}

if (-not (Test-Path $decoderSrc)) {
    Write-Error "Nemotron decoder_joint model not found: $decoderSrc"
    exit 1
}

if (-not (Test-Path $encoderRecipe)) {
    Write-Error "Olive encoder recipe not found: $encoderRecipe"
    exit 1
}

# ---------------------------------------------------------------------------
# Patch recipes: substitute ${MODEL_ROOT} with the absolute model path
# ---------------------------------------------------------------------------
$TempDir = Join-Path $env:TEMP "trackdub-olive-trtrtx-nemotron-$([System.Diagnostics.Process]::GetCurrentProcess().Id)"
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

function Resolve-Recipe {
    param([string] $SrcPath, [string] $DestPath)
    $content = Get-Content -Raw $SrcPath
    $content = $content -replace '\$\{MODEL_ROOT\}', ($modelRoot -replace '\\', '/')
    $content = $content -replace '\$\{ENCODER_OUTPUT_DIR\}', ("build/$encoderOutputDirName" -replace '\\', '/')
    Set-Content -Path $DestPath -Value $content -Encoding UTF8
}

$encoderRecipeDst = Join-Path $TempDir "encoder_trtrtx_$Precision.json"
$decoderRecipeDst = Join-Path $TempDir "decoder_joint_trtrtx_$Precision.json"
$latencyRecipeDst = Join-Path $TempDir 'eval_latency.json'
Resolve-Recipe $encoderRecipe $encoderRecipeDst
Resolve-Recipe $decoderRecipe $decoderRecipeDst
Resolve-Recipe (Join-Path $recipeDir 'eval_latency.json') $latencyRecipeDst

$origDir = Get-Location
Set-Location $RepoRoot

$results = [ordered]@{
    model_id       = 'tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx'
    model_root     = $modelRoot
    precision      = $Precision
    timestamp_utc  = $null
    encoder        = $null
    decoder        = $null
    latency        = $null
    staging_dir    = $null
    pass           = $false
}

try {
    # ---------------------------------------------------------------------------
    # Step 1: Optimize encoder
    # ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "=== Nemotron encoder optimization ($Precision + fusion + TRT-RTX session params) ===" -ForegroundColor Cyan
    & $OliveExe run --config $encoderRecipeDst
    if ($LASTEXITCODE -ne 0) { Write-Error "Encoder optimization failed (exit $LASTEXITCODE)."; exit 1 }
    $results.encoder = @{ status = 'ok'; output = "build/$encoderOutputDirName"; precision = $Precision }

    # ---------------------------------------------------------------------------
    # Step 2: Optimize decoder_joint
    # ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "=== Nemotron decoder_joint optimization ($Precision + fusion + TRT-RTX session params) ===" -ForegroundColor Cyan
    & $OliveExe run --config $decoderRecipeDst
    if ($LASTEXITCODE -ne 0) { Write-Error "Decoder_joint optimization failed (exit $LASTEXITCODE)."; exit 1 }
    $results.decoder = @{ status = 'ok'; output = "build/$decoderOutputDirName"; precision = $Precision }

    # ---------------------------------------------------------------------------
    # Step 3 (optional): encoder latency evaluation on synthetic input
    # ---------------------------------------------------------------------------
    if (-not $SkipLatency) {
        Write-Host ""
        Write-Host "=== Encoder latency evaluation on synthetic mel input ===" -ForegroundColor Cyan
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
    # Step 4: Stage combined output for the C# validation test
    # ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "=== Staging combined output for C# validation test ===" -ForegroundColor Cyan

    $StagingDir     = Join-Path $BuildDir $stagingDirName
    New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

    # Carry over companion files (tokenizer, config, README, LICENSE).
    foreach ($asset in @("README.md", "NOTICE.md", "LICENSE.OpenMDW-1.1", "config.json",
                          "tokenizer.model", "encoder.onnx.data")) {
        $assetSrc = Join-Path $modelRoot $asset
        if (Test-Path $assetSrc) {
            Copy-Item $assetSrc (Join-Path $StagingDir $asset) -Force
        }
    }

    # Copy optimized encoder.
    $encoderOutputDir = Join-Path $BuildDir $encoderOutputDirName
    $encoderOnnxSrc   = Get-ChildItem $encoderOutputDir -Filter "encoder.onnx" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($encoderOnnxSrc) {
        Copy-Item $encoderOnnxSrc.FullName (Join-Path $StagingDir "encoder.onnx") -Force
        Get-ChildItem $encoderOnnxSrc.Directory -Filter "encoder.onnx.data" -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $StagingDir "encoder.onnx.data") -Force }
    } else {
        Write-Warning "No encoder.onnx found in $encoderOutputDir - staging incomplete."
    }

    # Copy optimized decoder_joint.
    $decoderOutputDir = Join-Path $BuildDir $decoderOutputDirName
    $decoderOnnxSrc   = Get-ChildItem $decoderOutputDir -Filter "decoder_joint.onnx" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($decoderOnnxSrc) {
        Copy-Item $decoderOnnxSrc.FullName (Join-Path $StagingDir "decoder_joint.onnx") -Force
        Get-ChildItem $decoderOnnxSrc.Directory -Filter "decoder_joint.onnx.data" -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $StagingDir "decoder_joint.onnx.data") -Force }
    } else {
        Write-Warning "No decoder_joint.onnx found in $decoderOutputDir - staging incomplete."
    }

    $results.staging_dir = $StagingDir
    $results.pass = ($null -ne $encoderOnnxSrc) -and ($null -ne $decoderOnnxSrc)

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
    Write-Host "PASS - TRT-RTX ($Precision) optimization succeeded for Nemotron 3.5 ASR." -ForegroundColor Green
    Write-Host "Results written to: $ResultFile"
    Write-Host ""
    Write-Host "Staging directory: $($results.staging_dir)"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Add a TRT-RTX smoke test in tests/Trackdub.Inference.Onnx.Tests/NemotronAsrEncoderTrtRtxValidationTests.cs"
    Write-Host "     (mirror WhisperOnnxTrtRtxValidationTests.cs; load the staging dir via Discover())."
    Write-Host "  2. dotnet test tests/Trackdub.Inference.Onnx.Tests --filter 'FullyQualifiedName~NemotronAsrEncoderTrtRtx'"
    Write-Host "  3. If that passes: run .\tools\olive\Flip-TrtRtxAsrDiarization.ps1 to enable trt-rtx in the manifest and tests."
} else {
    Write-Host "FAIL - see errors above." -ForegroundColor Red
    exit 1
}
