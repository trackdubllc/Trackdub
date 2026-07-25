#Requires -Version 7.0
<#
.SYNOPSIS
  Upload a premade ONNX variant folder to Hugging Face for starter-pack download.

.DESCRIPTION
  Wraps `hf upload` with Trackdub naming defaults. Folder layout must match manifest
  relative paths (see docs/internal/premade-hf-variants.md).

.PARAMETER LocalPath
  Directory containing variant files (e.g. onnx/model_int8.onnx).

.PARAMETER RepoId
  Full HF repo id. Default: tonythethompson/trackdub-{ShortName}-{Variant}

.PARAMETER ShortName
  Model short name when RepoId is omitted (e.g. silero-vad, kokoro, phi-4-mini).

.PARAMETER Variant
  Variant alias (e.g. int8, q8f16, gpu-int4).

.PARAMETER Private
  Create/upload as a private repo.

.PARAMETER DryRun
  Print the hf command without executing.

.EXAMPLE
  .\tools\models\Publish-TrackdubPackVariant.ps1 -LocalPath .\staging\silero-int8 -ShortName silero-vad -Variant int8

.EXAMPLE
  .\tools\models\Publish-TrackdubPackVariant.ps1 -LocalPath .\staging\phi-gpu-int4 -RepoId tonythethompson/trackdub-phi-4-mini-gpu-int4
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $LocalPath,

    [string] $RepoId,

    [string] $ShortName,

    [string] $Variant,

    [switch] $Private,

    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LocalPath -PathType Container)) {
    throw "LocalPath not found or not a directory: $LocalPath"
}

if ([string]::IsNullOrWhiteSpace($RepoId)) {
    if ([string]::IsNullOrWhiteSpace($ShortName) -or [string]::IsNullOrWhiteSpace($Variant)) {
        throw 'Provide -RepoId or both -ShortName and -Variant.'
    }

    $RepoId = "tonythethompson/trackdub-$ShortName-$Variant"
}

$hf = Get-Command hf -ErrorAction SilentlyContinue
if (-not $hf) {
    throw 'hf CLI not found on PATH. Install Hugging Face Hub CLI and authenticate with `hf auth login`.'
}

$resolvedLocal = (Resolve-Path -LiteralPath $LocalPath).Path
$args = @(
    'upload',
    $RepoId,
    $resolvedLocal,
    '--type', 'model',
    '--exclude', '.cache/**'
)

if ($Private) {
    $args += '--private'
}

$display = "hf $($args -join ' ')"
Write-Host $display

if ($DryRun) {
    return
}

if ($PSCmdlet.ShouldProcess($RepoId, 'hf upload')) {
    & hf @args
    if ($LASTEXITCODE -ne 0) {
        throw "hf upload failed with exit code $LASTEXITCODE"
    }
}
