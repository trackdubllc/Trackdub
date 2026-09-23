#Requires -Version 7.0
<#
.SYNOPSIS
    Flip sortformer + nemotron-asr manifest + test assertions to enable trt-rtx after hardware validation.

.DESCRIPTION
    Applies two changes:

    1. bundled-models.manifest.json:
       - Adds "trt-rtx" to supported_providers for the cgus sortformer diarization model
         (already absent; the model has no optimization block today).
       - Adds an optimization.olive block for the cgus sortformer diarization model with a
         trt-rtx recipe binding pointing at the new NvTensorRtRtx encoder recipe.
       - Adds "trt-rtx" to supported_providers for the nemotron-3.5-asr-streaming-0.6b model
         and a trt-rtx recipe binding pointing at the new NvTensorRtRtx encoder + decoder_joint recipes.

    2. ModelManifestTests.cs:
       - For the nemotron-asr assertion block, changes the trt-rtx absence assertion to a presence
         assertion. SortFormer has no test assertion today; this script does not modify tests for it.

    Requires:
      - .\tools\olive\Validate-SortFormerTrtRtx.ps1 produced build/sortformer-4spk-trtrtx-validation.json
        with pass=true (or use -Force).
      - .\tools\olive\Validate-NemotronAsrTrtRtx.ps1 produced build/nemotron-3.5-asr-trtrtx-validation.json
        with pass=true (or use -Force).

.PARAMETER Force
    Skip the validation result check and apply changes unconditionally.

.EXAMPLE
    .\tools\olive\Flip-TrtRtxAsrDiarization.ps1
    .\tools\olive\Flip-TrtRtxAsrDiarization.ps1 -Force
#>

param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir       = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot        = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$ManifestPath    = Join-Path $RepoRoot 'src\Trackdub.Inference\Runtime\ModelManifest\bundled-models.manifest.json'
$TestPath        = Join-Path $RepoRoot 'tests\Trackdub.Inference.Tests\ModelManifestTests.cs'
$SortformerResult = Join-Path $RepoRoot 'build\sortformer-4spk-trtrtx-validation.json'
$NemotronResult   = Join-Path $RepoRoot 'build\nemotron-3.5-asr-trtrtx-validation.json'

# ---------------------------------------------------------------------------
# Check validation results
# ---------------------------------------------------------------------------
if (-not $Force) {
    foreach ($resultPath in @($SortformerResult, $NemotronResult)) {
        if (-not (Test-Path $resultPath)) {
            Write-Error "Validation result not found at: $resultPath`nRun the corresponding Validate-*.ps1 script first (or use -Force to skip)."
            exit 1
        }
        $result = Get-Content -Raw $resultPath | ConvertFrom-Json
        if (-not $result.pass) {
            Write-Error "Validation result has pass=false in $resultPath.`nThe validator's 'trackdub providers trt-rtx verify' step did not confirm the staged model runs on TensorRT RTX; see provider_check.detail in that file. Re-run the validator, or use -Force."
            exit 1
        }
    }
    Write-Host "Validation results OK for sortformer + nemotron-asr." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 1. Build the Python manifest-patch script (executed in step 2b, after the
#    ModelManifestTests.cs pre-check below confirms the flip can complete atomically)
# ---------------------------------------------------------------------------
$VenvPath  = Join-Path $env:LOCALAPPDATA 'Trackdub\tools\olive-env-tensorrtrtx'
$PythonExe = Join-Path $VenvPath 'Scripts\python.exe'

if (-not (Test-Path $PythonExe)) {
    # Try system Python
    $PythonExe = 'python'
}

$pythonScript = @'
import json, sys

manifest_path = sys.argv[1]

# Model IDs whose optimization block / supported_providers list we mutate.
SORTFORMER_MODEL_ID = "cgus/diar_streaming_sortformer_4spk-v2.1-onnx"
NEMOTRON_MODEL_ID  = "tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx"

# Recipe binding templates. The recipe paths mirror the existing
# WhisperOnnxTrtRtx convention (recipe dirs under resources/olive-recipes/).
def sortformer_recipe_bindings():
    return [
        {
            "provider": "trt-rtx",
            "precision": "fp16",
            "component": "onnx/model.onnx",
            "config_relative_path": "cgus-diar_streaming_sortformer_4spk-v2.1-onnx/NvTensorRtRtx/encoder_trtrtx_fp16.json",
            "operations": ["provider_optimization"],
            "expected_output": "onnx_components"
        }
    ]

def nemotron_recipe_bindings():
    return [
        {
            "provider": "trt-rtx",
            "precision": "fp16",
            "component": "encoder.onnx",
            "config_relative_path": "nemotron-3.5-asr-streaming-0.6b-onnx/NvTensorRtRtx/encoder_trtrtx_fp16.json",
            "operations": ["provider_optimization"],
            "expected_output": "onnx_components"
        },
        {
            "provider": "trt-rtx",
            "precision": "fp16",
            "component": "decoder_joint.onnx",
            "config_relative_path": "nemotron-3.5-asr-streaming-0.6b-onnx/NvTensorRtRtx/decoder_joint_trtrtx_fp16.json",
            "operations": ["provider_optimization"],
            "expected_output": "onnx_components"
        }
    ]

def ensure_provider(providers, provider):
    if provider not in providers:
        providers.append(provider)
        return True
    return False

def ensure_recipe_binding(olive, provider, new_bindings):
    bindings = olive.setdefault("recipe_bindings", [])
    existing = {b.get("config_relative_path"): b for b in bindings}
    added = False
    for binding in new_bindings:
        current = existing.get(binding["config_relative_path"])
        if current is None:
            bindings.append(binding)
            added = True
        elif current.get("component") != binding["component"]:
            # Without a component, OliveRecipeResolver cannot tell a model's bindings apart.
            current["component"] = binding["component"]
            added = True
    return added

with open(manifest_path, "r", encoding="utf-8") as f:
    catalog = json.load(f)

sortformer_changed = False
nemotron_changed = False
sortformer_found = False
nemotron_found = False

for model in catalog.get("models", []):
    model_id = model.get("model_id")

    # ---- SortFormer (cgus) --------------------------------------------------
    # Has no optimization block today; we create one in existing-onnx-components mode
    # pointing at onnx/model.onnx, matching what the bundled model ships with.
    if model_id == SORTFORMER_MODEL_ID:
        sortformer_found = True
        optimization = model.setdefault("optimization", {})
        olive = optimization.setdefault("olive", {})
        olive.setdefault("mode", "existing-onnx-components")
        olive.setdefault("components", ["onnx/model.onnx"])
        providers = olive.setdefault("supported_providers", [])
        if ensure_provider(providers, "trt-rtx"):
            sortformer_changed = True
        # Make sure the non-NVIDIA providers are still listed so non-NVIDIA hardware keeps working.
        for p in ("cpu", "dml", "cuda", "tensorrt"):
            ensure_provider(providers, p)
        if ensure_recipe_binding(olive, "trt-rtx", sortformer_recipe_bindings()):
            sortformer_changed = True

    # ---- Nemotron ASR -------------------------------------------------------
    # Already has an optimization block (existing-onnx-components) but trt-rtx is not yet listed.
    elif model_id == NEMOTRON_MODEL_ID:
        nemotron_found = True
        optimization = model.setdefault("optimization", {})
        olive = optimization.setdefault("olive", {})
        olive.setdefault("mode", "existing-onnx-components")
        olive.setdefault("components", ["encoder.onnx", "decoder_joint.onnx"])
        providers = olive.setdefault("supported_providers", [])
        if ensure_provider(providers, "trt-rtx"):
            nemotron_changed = True
        for p in ("cpu", "dml", "cuda", "tensorrt"):
            ensure_provider(providers, p)
        if ensure_recipe_binding(olive, "trt-rtx", nemotron_recipe_bindings()):
            nemotron_changed = True

if not sortformer_found or not nemotron_found:
    missing = []
    if not sortformer_found:
        missing.append(SORTFORMER_MODEL_ID)
    if not nemotron_found:
        missing.append(NEMOTRON_MODEL_ID)
    print(f"ERROR: model_id(s) not found in manifest, refusing to write: {missing}", file=sys.stderr)
    sys.exit(1)

with open(manifest_path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(catalog, f, indent=2, ensure_ascii=False)
    f.write("\n")

print(f"Done. sortformer_changed={sortformer_changed} nemotron_changed={nemotron_changed}")
'@

# ---------------------------------------------------------------------------
# 2a. Pre-check ModelManifestTests.cs BEFORE touching the manifest, so a missing
#     or already-edited test block aborts before any file is written (no partial flip).
# ---------------------------------------------------------------------------
$testContent = Get-Content -Raw $TestPath

$oldBlock = @'
        Assert.Equal(
            ["encoder.onnx", "decoder_joint.onnx"],
            manifest.Optimization!.Olive!.Components);
    }

    [Fact]
    public void LoadCatalog_LoadsNewestOliveProvidersAndRecipeMetadata()
'@

$newBlock = @'
        Assert.Equal(
            ["encoder.onnx", "decoder_joint.onnx"],
            manifest.Optimization!.Olive!.Components);
        Assert.Contains(OliveOptimizationProvider.TensorRtRtx, manifest.Optimization.Olive.SupportedProviders);
        Assert.Contains(
            manifest.Optimization.Olive.RecipeBindings,
            binding => binding.Provider == "trt-rtx" &&
                       binding.ConfigRelativePath.Contains(
                           "nemotron-3.5-asr-streaming-0.6b-onnx/NvTensorRtRtx/encoder_trtrtx_fp16.json",
                           StringComparison.Ordinal));
    }

    [Fact]
    public void LoadCatalog_LoadsNewestOliveProvidersAndRecipeMetadata()
'@

$testNeedsPatch = $testContent.Contains($oldBlock)
$testAlreadyFlipped = $testContent.Contains("Assert.Contains(OliveOptimizationProvider.TensorRtRtx, manifest.Optimization.Olive.SupportedProviders);") -and
                      $testContent.Contains("nemotron-3.5-asr-streaming-0.6b-onnx/NvTensorRtRtx/encoder_trtrtx_fp16.json")

if (-not $testNeedsPatch -and -not $testAlreadyFlipped) {
    Write-Error "Expected Nemotron-ASR assertion block not found in $TestPath -- refusing to touch the manifest. Manual edit required: in LoadCatalog_NemotronAsrEntryMatchesPinnedOnnxBundle, after the components assertion, add:`n    Assert.Contains(OliveOptimizationProvider.TensorRtRtx, manifest.Optimization.Olive.SupportedProviders);`n    Assert.Contains(manifest.Optimization.Olive.RecipeBindings, binding => binding.Provider == `"trt-rtx`" && binding.ConfigRelativePath.Contains(`"nemotron-3.5-asr-streaming-0.6b-onnx/NvTensorRtRtx/encoder_trtrtx_fp16.json`", StringComparison.Ordinal));"
    exit 1
}

# ---------------------------------------------------------------------------
# 2b. Patch bundled-models.manifest.json via Python json manipulation
# ---------------------------------------------------------------------------
Write-Host "Patching bundled-models.manifest.json..."

$tmpPy = Join-Path $env:TEMP "flip_trtrtx_asr_diarization_$([System.Diagnostics.Process]::GetCurrentProcess().Id).py"
Set-Content -Path $tmpPy -Value $pythonScript -Encoding UTF8

try {
    & $PythonExe $tmpPy $ManifestPath
    if ($LASTEXITCODE -ne 0) { Write-Error "Python manifest patch failed."; exit 1 }
} finally {
    Remove-Item -Force $tmpPy -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 3. Patch ModelManifestTests.cs: flip Nemotron trt-rtx assertion
# ---------------------------------------------------------------------------
Write-Host "Patching ModelManifestTests.cs..."

if ($testNeedsPatch) {
    $testContent = $testContent.Replace($oldBlock, $newBlock)
    Set-Content -Path $TestPath -Value $testContent -Encoding UTF8 -NoNewline
    Write-Host "Test assertion flipped for Nemotron-ASR trt-rtx presence." -ForegroundColor Green
} else {
    Write-Host "Test assertion already flipped for Nemotron-ASR trt-rtx presence." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Flip complete." -ForegroundColor Green
Write-Host "Next steps:"
Write-Host "  1. dotnet build Trackdub.sln       - verify zero errors"
Write-Host "  2. dotnet test tests/Trackdub.Inference.Tests --filter 'FullyQualifiedName~NemotronAsr|FullyQualifiedName~SortFormer'"
Write-Host "  3. Commit: 'Enable trt-rtx for sortformer + nemotron-asr (hardware validated)'"
