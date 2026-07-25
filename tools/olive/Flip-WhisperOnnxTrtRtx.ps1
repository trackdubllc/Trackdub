#Requires -Version 7.0
<#
.SYNOPSIS
    Flip whisper-onnx manifest + test assertions to enable trt-rtx after hardware validation.

.DESCRIPTION
    Applies two changes:
      1. bundled-models.manifest.json: adds "trt-rtx" to supported_providers and adds
         recipe_bindings for each whisper-onnx model.
      2. ModelManifestTests.cs: changes DoesNotContain(TensorRtRtx) to Contains(TensorRtRtx)
         for the whisper-onnx assertion block.

    Requires .\tools\olive\Validate-WhisperOnnxTrtRtx.ps1 to have produced a passing
    result in build/whisper-onnx-trtrtx-validation.json first.

.PARAMETER Force
    Skip the validation result check and apply changes unconditionally.

.EXAMPLE
    .\tools\olive\Flip-WhisperOnnxTrtRtx.ps1
    .\tools\olive\Flip-WhisperOnnxTrtRtx.ps1 -Force
#>

param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot    = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$ManifestPath = Join-Path $RepoRoot 'src\Trackdub.Inference\Runtime\ModelManifest\bundled-models.manifest.json'
$TestPath     = Join-Path $RepoRoot 'tests\Trackdub.Inference.Tests\ModelManifestTests.cs'
$ResultFile   = Join-Path $RepoRoot 'build\whisper-onnx-trtrtx-validation.json'

# ---------------------------------------------------------------------------
# Check validation result
# ---------------------------------------------------------------------------
if (-not $Force) {
    if (-not (Test-Path $ResultFile)) {
        Write-Error "Validation result not found at: $ResultFile`nRun .\tools\olive\Validate-WhisperOnnxTrtRtx.ps1 first (or use -Force to skip)."
        exit 1
    }
    $result = Get-Content -Raw $ResultFile | ConvertFrom-Json
    if (-not $result.pass) {
        Write-Error "Last validation run did not pass (pass=false in $ResultFile).`nRe-run .\tools\olive\Validate-WhisperOnnxTrtRtx.ps1 or use -Force."
        exit 1
    }
    Write-Host "Validation result OK (model_size=$($result.model_size), ts=$($result.timestamp_utc))." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 1. Patch bundled-models.manifest.json via Python json manipulation
# ---------------------------------------------------------------------------
Write-Host "Patching bundled-models.manifest.json…"

$VenvPath  = Join-Path $env:LOCALAPPDATA 'Trackdub\tools\olive-env-tensorrtrtx'
$PythonExe = Join-Path $VenvPath 'Scripts\python.exe'

if (-not (Test-Path $PythonExe)) {
    # Try system Python
    $PythonExe = 'python'
}

$pythonScript = @'
import json, sys

manifest_path = sys.argv[1]

WHISPER_ONNX_FAMILIES = {"whisper-onnx"}

RECIPE_BINDING_TEMPLATE = {
    "provider": "trt-rtx",
    "precision": "fp16",
    "operations": ["provider_optimization"],
    "expected_output": "onnx_components"
}

MODEL_RECIPE_DIRS = {
    "onnx-community/whisper-tiny":  "onnx-community-whisper-tiny",
    "onnx-community/whisper-base":  "onnx-community-whisper-base",
    "onnx-community/whisper-small": "onnx-community-whisper-small",
    "Xenova/whisper-medium":        "Xenova-whisper-medium",
    "Xenova/whisper-large-v3":      "Xenova-whisper-large-v3",
}

with open(manifest_path, "r", encoding="utf-8") as f:
    catalog = json.load(f)

changed = 0
for model in catalog.get("models", []):
    if model.get("engine_family") not in WHISPER_ONNX_FAMILIES:
        continue
    olive = model.get("optimization", {}).get("olive")
    if not olive:
        continue

    providers = olive.get("supported_providers", [])
    if "trt-rtx" not in providers:
        providers.append("trt-rtx")
        olive["supported_providers"] = providers
        changed += 1

    recipe_dir = MODEL_RECIPE_DIRS.get(model["model_id"])
    if recipe_dir:
        bindings = olive.setdefault("recipe_bindings", [])
        already = any(
            b.get("provider") == "trt-rtx"
            for b in bindings
        )
        if not already:
            binding = dict(RECIPE_BINDING_TEMPLATE)
            binding["config_relative_path"] = (
                f"{recipe_dir}/NvTensorRtRtx/encoder_trtrtx_fp16.json"
            )
            bindings.append(binding)

with open(manifest_path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(catalog, f, indent=6, ensure_ascii=False)
    f.write("\n")

print(f"Done. {changed} models updated.")
'@

$tmpPy = Join-Path $env:TEMP "flip_trtrtx_$([System.Diagnostics.Process]::GetCurrentProcess().Id).py"
Set-Content -Path $tmpPy -Value $pythonScript -Encoding UTF8

try {
    & $PythonExe $tmpPy $ManifestPath
    if ($LASTEXITCODE -ne 0) { Write-Error "Python manifest patch failed."; exit 1 }
} finally {
    Remove-Item -Force $tmpPy -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 2. Patch ModelManifestTests.cs: flip DoesNotContain → Contains for TRT-RTX
# ---------------------------------------------------------------------------
Write-Host "Patching ModelManifestTests.cs…"

$testContent = Get-Content -Raw $TestPath

# Replace the two-line DoesNotContain block for whisper-onnx TRT providers
$oldBlock = @'
        // Standard ONNX whisper entries must use existing-onnx-components mode and must not list TRT providers
        // (TRT optimization of standard ONNX whisper models has not been validated).
        Assert.All(
            catalog.Models.Where(m => m.Task is ModelTask.Asr && m.EngineFamily == "whisper-onnx"),
            manifest =>
            {
                Assert.Equal("existing-onnx-components", manifest.Optimization!.Olive!.Mode);
                Assert.DoesNotContain(OliveOptimizationProvider.TensorRt, manifest.Optimization.Olive.SupportedProviders);
                Assert.DoesNotContain(OliveOptimizationProvider.TensorRtRtx, manifest.Optimization.Olive.SupportedProviders);
            });
'@

$newBlock = @'
        // Standard ONNX whisper entries must use existing-onnx-components mode.
        // TRT-RTX validated on hardware (see build/whisper-onnx-trtrtx-validation.json).
        Assert.All(
            catalog.Models.Where(m => m.Task is ModelTask.Asr && m.EngineFamily == "whisper-onnx"),
            manifest =>
            {
                Assert.Equal("existing-onnx-components", manifest.Optimization!.Olive!.Mode);
                Assert.DoesNotContain(OliveOptimizationProvider.TensorRt, manifest.Optimization.Olive.SupportedProviders);
                Assert.Contains(OliveOptimizationProvider.TensorRtRtx, manifest.Optimization.Olive.SupportedProviders);
            });
'@

if (-not $testContent.Contains($oldBlock)) {
    Write-Warning "Expected assertion block not found in $TestPath — may have already been flipped, or the file changed."
    Write-Warning "Manual edit required: change DoesNotContain(TensorRtRtx) → Contains(TensorRtRtx) in LoadCatalog_WhisperAsrEntriesHaveOliveOptimizationProfile."
} else {
    $testContent = $testContent.Replace($oldBlock, $newBlock)
    Set-Content -Path $TestPath -Value $testContent -Encoding UTF8 -NoNewline
    Write-Host "Test assertion flipped." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Flip complete." -ForegroundColor Green
Write-Host "Next steps:"
Write-Host "  1. dotnet build Trackdub.sln       — verify zero errors"
Write-Host "  2. dotnet test tests/Trackdub.Inference.Tests  — verify tests pass"
Write-Host "  3. Commit: 'Enable trt-rtx for whisper-onnx models (hardware validated)'"
