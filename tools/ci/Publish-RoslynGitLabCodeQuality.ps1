# Emits gl-code-quality-roslyn.json (GitLab Code Quality / Code Climate JSON) from an MSBuild SARIF log.
# Intended for GitLab Windows runners after Fetch-WinNativeDeps + dotnet restore.
# See: https://docs.gitlab.com/ci/testing/code_quality/

$ErrorActionPreference = 'Stop'

$root = if (-not [string]::IsNullOrWhiteSpace($env:CI_PROJECT_DIR)) {
    $env:CI_PROJECT_DIR
}
else {
    (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

$toolDir = Join-Path $root '.tools-cq'
# Write SARIF outside the repo tree: a single shared ErrorLog path plus parallel
# project builds can hit CS0016 ("user-mapped section open") on Windows.
$sarifPath = Join-Path ([System.IO.Path]::GetTempPath()) "trackdub-gl-roslyn-$PID.sarif"
$outPath = Join-Path $root 'gl-code-quality-roslyn.json'

dotnet tool install --tool-path $toolDir dotnetcodequalitytogitlab --version 2.2.0 --add-source 'https://api.nuget.org/v3/index.json' | Out-Null

$cq = Join-Path $toolDir 'cq.exe'
if (-not (Test-Path -LiteralPath $cq)) {
    $cq = Join-Path $toolDir 'cq'
}

Push-Location $root
try {
    dotnet restore Trackdub.slnx -p:Platform=x64
    dotnet @(
        'build', 'Trackdub.slnx',
        '--configuration', 'Release',
        '--no-restore',
        '-m:1',
        '-p:Platform=x64',
        '-p:BuildInParallel=false',
        '-p:UseSharedCompilation=false',
        '-p:TreatWarningsAsErrors=false',
        "-p:ErrorLog=$sarifPath",
        '-p:ErrorLogFormat=sarif-version=2.1'
    )
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $sarifPath)) {
    [System.IO.File]::WriteAllText($outPath, '[]')
    exit 0
}

& $cq sarif $sarifPath $outPath $root

if (Test-Path -LiteralPath $sarifPath) {
    Remove-Item -LiteralPath $sarifPath -Force -ErrorAction SilentlyContinue
}
