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
