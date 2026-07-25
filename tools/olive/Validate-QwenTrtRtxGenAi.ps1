#Requires -Version 7.0
<#
.SYNOPSIS
  Smoke-test a local Qwen ORT GenAI bundle with NvTensorRtRtx.

.PARAMETER ModelDir
  Folder containing genai_config.json (default variant root or mxfp8/ subfolder).

.EXAMPLE
  .\tools\olive\Validate-QwenTrtRtxGenAi.ps1 -ModelDir A:/Trackdub/.agent-tmp/qwen-fp16-trtrtx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ModelDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath (Join-Path $ModelDir "genai_config.json"))) {
    throw "genai_config.json not found under $ModelDir"
}

Write-Host "Running NvTensorRtRtx GenAI smoke in $ModelDir ..."

python -m pip install -q "onnxruntime-trt-rtx==1.23.2" "onnxruntime-genai==0.11.4" "onnxruntime-genai-cuda==0.11.4" "onnxruntime-gpu==1.23.2"

python -c @"
import onnxruntime_genai as og
model_dir = r'$ModelDir'
config = og.Config(model_dir)
config.clear_providers()
config.append_provider('NvTensorRtRtx')
model = og.Model(config)
tokenizer = og.Tokenizer(model)
sequences = tokenizer.encode('Hello')
params = og.GeneratorParams(model)
generator = og.Generator(model, params)
generator.append_token_sequences(sequences)
generator.generate_next_token()
print('NvTensorRtRtx GenAI smoke OK:', model_dir)
"@
