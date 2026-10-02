<#
.SYNOPSIS
    Reference Coding Search tool for Trackdub using local XERJ engine.

.DESCRIPTION
    Searches peer open-source implementations ('ref-*') or Trackdub core ('trackdub-*')
    to inspect how reference architectures solved speech recognition, diarization, VAD,
    TTS, and ONNX tensor pipelines before writing code.

.PARAMETER Query
    Free-text search query for concept, phrase, or implementation pattern.

.PARAMETER Def
    Exact symbol name to locate definition and signature.

.PARAMETER Target
    Corpus to search: 'ref' (peer repos, default), 'trackdub' (this repo), or 'all'.

.PARAMETER Count
    Number of passages to return (default: 5).

.PARAMETER Full
    Maximum characters per passage (default: 800).

.EXAMPLE
    .\tools\ref-search.ps1 "silero vad onnx state tensor"
    .\tools\ref-search.ps1 -Def "SileroVadSpeechRegionDetector" -Target trackdub
    .\tools\ref-search.ps1 "kokoro voices audio generate" -Count 3
#>
[CmdletBinding(DefaultParameterSetName = 'Query')]
param(
    [Parameter(Position = 0, ParameterSetName = 'Query')]
    [string]$Query,

    [Parameter(ParameterSetName = 'Definition', Mandatory = $true)]
    [string]$Def,

    [ValidateSet('ref', 'trackdub', 'all')]
    [string]$Target = 'ref',

    [int]$Count = 5,

    [int]$Full = 800,

    [switch]$Json
)

$xerj = Join-Path $env:LOCALAPPDATA "Programs\xerj\xerj.exe"
if (-not (Test-Path $xerj)) {
    $xerj = (Get-Command xerj -ErrorAction SilentlyContinue)?.Source
}
if (-not $xerj -or -not (Test-Path $xerj)) {
    Write-Error "xerj executable not found. Ensure XERJ is installed at %LOCALAPPDATA%\Programs\xerj\xerj.exe or on PATH."
    exit 1
}

$prefixes = switch ($Target) {
    'ref'      { @('ref') }
    'trackdub' { @('trackdub') }
    'all'      { @('trackdub', 'ref') }
}

foreach ($prefix in $prefixes) {
    if ($prefixes.Count -gt 1) {
        Write-Host "`n=== Corpus: $prefix ===" -ForegroundColor Cyan
    }

    if ($PSCmdlet.ParameterSetName -eq 'Definition') {
        & $xerj def --prefix $prefix $Def
    }
    else {
        if ([string]::IsNullOrWhiteSpace($Query)) {
            Write-Warning "Please specify a query string or -Def <symbol>."
            return
        }
        $argsList = @('search', '--prefix', $prefix, $Query, '-k', $Count, '--full', $Full)
        if ($Json) {
            $argsList += '--json'
        }
        & $xerj @argsList
    }
}
