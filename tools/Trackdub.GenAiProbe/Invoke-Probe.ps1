<#
.SYNOPSIS
GenAI native-pair construction probe.

.DESCRIPTION
Run Invoke-Probe.ps1 with -ModelDirectory pointing to an existing GenAI model directory.
The script builds a Windows-TFM probe and constructs a CPU Config/Model in a fresh child
process. A native crash, managed failure or timeout fails the parent script without loading
GenAI into the parent. No model is downloaded. A successful construction is not a generation
or accelerator validation. The probe prints selected paths, package flavor and versions.

The current explicit support policy covers Windows ML 2.4.89 / native ORT 1.27.1 /
GenAI.WinML 0.17.1, and stock ORT 1.30.0 / GenAI CPU or CUDA 0.17.1.
Upgrades require updating and validating the policy. DNNL and unknown native deployments
are not verified; a Windows GenAI request fails before Config or Model construction.
Build-generated hashes are provenance checks, not a security boundary against someone
who can replace both the managed assembly and native binaries.

Unit tests use synthetic files to test provenance and selection policy; only this child
probe exercises real native model construction. Run it separately after targeted unit tests.

To opt into the integration test, set TRACKDUB_GENAI_PROBE_EXE to the built
Trackdub.GenAiProbe.exe and TRACKDUB_GENAI_PROBE_MODEL to the model directory, then run
GenAiNativeConstructionProbeTests on the Windows TFM. Without those fixtures it is
explicitly skipped, not passed. The integration test has a 120-second child timeout.
#>
param(
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier='win-x64',
    [ValidateRange(1,3600)][int]$TimeoutSeconds=120
)
$ErrorActionPreference='Stop'
if (-not (Test-Path -LiteralPath $ModelDirectory -PathType Container)) { throw 'Model directory does not exist.' }
$model=(Resolve-Path -LiteralPath $ModelDirectory).Path
$project=Join-Path $PSScriptRoot 'Trackdub.GenAiProbe.csproj'
$output=Join-Path $PSScriptRoot "bin/probe/$RuntimeIdentifier"
& dotnet build $project -c Debug -r $RuntimeIdentifier --self-contained false -o $output -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $LASTEXITCODE" }
$info=New-Object System.Diagnostics.ProcessStartInfo
$info.FileName=Join-Path $output 'Trackdub.GenAiProbe.exe'
$info.Arguments='"'+$model.TrimEnd('\')+'"'
$info.WorkingDirectory=$output
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
$process=New-Object System.Diagnostics.Process
$process.StartInfo=$info
try {
    if (-not $process.Start()) { throw 'Failed to start probe.' }
    $stdout=$process.StandardOutput.ReadToEndAsync()
    $stderr=$process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds*1000)) {
        $process.Kill()
        $process.WaitForExit()
        throw "GenAI construction probe timed out after $TimeoutSeconds seconds."
    }
    $out=$stdout.GetAwaiter().GetResult()
    $err=$stderr.GetAwaiter().GetResult()
    if ($out) { Write-Output $out }
    if ($err) { Write-Output $err }
    if ($process.ExitCode -ne 0) { throw "GenAI construction child failed or crashed; exit code $($process.ExitCode)." }
} finally { $process.Dispose() }
