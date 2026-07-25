#Requires -Version 7.0
<#
.SYNOPSIS
  Smoke-check premade starter-pack HF variant mirrors declared in bundled manifest.

.DESCRIPTION
  Downloads every tonythethompson/trackdub-* download_file_sources URL and verifies
  SHA-256 when download_file_hashes is present. Use -Quick for one reachability probe per mirror repo
  (HEAD or 1-byte range; no full weight download).

.EXAMPLE
  .\tools\models\Smoke-PremadePackVariantDownloads.ps1

.EXAMPLE
  .\tools\models\Smoke-PremadePackVariantDownloads.ps1 -Quick
#>
[CmdletBinding()]
param(
    [switch] $Quick
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script = Join-Path $repoRoot 'tools\ci\smoke-premade-pack-variants.py'
if (-not (Test-Path -LiteralPath $script)) {
    throw "Missing smoke script: $script"
}

$args = @($script)
if ($Quick) {
    $args += '--quick'
}

python @args
if ($LASTEXITCODE -ne 0) {
    throw "Premade pack variant smoke failed with exit code $LASTEXITCODE"
}
