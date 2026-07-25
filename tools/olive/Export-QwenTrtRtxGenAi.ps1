#Requires -Version 7.0
<#
.SYNOPSIS
  Export Qwen2.5-1.5B-Instruct ORT GenAI fp16 bundle for NvTensorRtRtx (TensorRT RTX).

.DESCRIPTION
  Dev-only helper for the Trackdub text-refinement model (tonythethompson/Qwen2.5-1.5B-Instruct).
  Produces the same seven-file GenAI layout as the default manifest variant.

  MXFP8 note: ORT GenAI ModelBuilder currently accepts -p int4|bf16|fp16|fp32 only.
  NVIDIA Model Optimizer MXFP8 torch quant + GenAI KV-cache export is a separate bridge;
  do not upload fp16/fp8 ONNX under mxfp8/ until a verified MXFP8 GenAI graph exists.

.PARAMETER OutputDir
  Directory for genai_config.json, model.onnx, model.onnx.data, tokenizer, and config sidecars.

.PARAMETER ModelId
  Hugging Face model id passed to ModelBuilder (-m).

.PARAMETER SkipExport
  Skip ModelBuilder; only validate an existing OutputDir layout.

.EXAMPLE
  .\tools\olive\Export-QwenTrtRtxGenAi.ps1 -OutputDir A:/Trackdub/.agent-tmp/qwen-fp16-trtrtx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDir,

    [string] $ModelId = "Qwen/Qwen2.5-1.5B-Instruct",

    [switch] $SkipExport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$requiredFiles = @(
    "genai_config.json",
    "model.onnx",
    "model.onnx.data",
    "tokenizer.json",
    "tokenizer_config.json",
    "config.json",
    "chat_template.jinja"
)

function Test-GenAiBundle([string] $Root) {
    $missing = @()
    foreach ($name in $requiredFiles) {
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path)) {
            $missing += $name
        }
    }

    if ($missing.Count -gt 0) {
        throw "GenAI bundle incomplete under '$Root'. Missing: $($missing -join ', ')"
    }
}

if (-not $SkipExport) {
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw "python not found on PATH."
    }

    $null = New-Item -ItemType Directory -Force -Path $OutputDir
    Write-Host "Exporting $ModelId to $OutputDir via ORT GenAI ModelBuilder (fp16, NvTensorRtRtx)..."

    python -m pip install -q "onnxruntime-trt-rtx==1.23.2" "onnxruntime-genai==0.11.4" "onnxruntime-genai-cuda==0.11.4" transformers

    python -m onnxruntime_genai.models.builder `
        -m $ModelId `
        -o $OutputDir `
        -p fp16 `
        -e NvTensorRtRtx

    if (-not (Test-Path -LiteralPath (Join-Path $OutputDir "config.json"))) {
        Write-Host "ModelBuilder did not emit config.json; copying from Hugging Face snapshot..."
        python -c "from pathlib import Path; from transformers import AutoConfig; out=Path(r'$OutputDir'); cfg=AutoConfig.from_pretrained(r'$ModelId', trust_remote_code=True); cfg.save_pretrained(out)"
    }
}

Test-GenAiBundle -Root $OutputDir
Write-Host "GenAI bundle layout OK: $OutputDir"
Write-Host "Next: upload root files to HF for default variant, or mxfp8/ after a verified MXFP8 GenAI export."
