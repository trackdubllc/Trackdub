[CmdletBinding()]
param(
    [string]$OnnxRuntimeVersion,
    [ValidateSet("win-x64", "linux-x64", "osx-x64")]
    [string]$RuntimeIdentifier,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$SourceRoot = (Join-Path $env:TEMP "trackdub-onnxruntime-dnnl-src"),
    [string]$ArtifactsRoot = (Join-Path $env:TEMP "trackdub-onnxruntime-dnnl-artifacts")
)

$ErrorActionPreference = "Stop"

$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($hostArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw "Build-OnnxRuntimeDnnlNativePackage.ps1 currently supports x64 hosts only. Host architecture was $hostArchitecture. Build x64 assets on an x64 machine, or add explicit cross-architecture build flags before using DNNL packaging."
}

if (-not $OnnxRuntimeVersion) {
    $packagesContent = Get-Content -Raw (Join-Path $RepoRoot "Directory.Packages.props")
    if ($packagesContent -match '<OnnxRuntimeVersion>([^<]+)</OnnxRuntimeVersion>') {
        $OnnxRuntimeVersion = $Matches[1].Trim()
    } else {
        throw "Could not read OnnxRuntimeVersion from Directory.Packages.props. Pass -OnnxRuntimeVersion explicitly."
    }
}

if (-not $RuntimeIdentifier) {
    if ($IsWindows) { $RuntimeIdentifier = "win-x64" }
    elseif ($IsLinux) { $RuntimeIdentifier = "linux-x64" }
    elseif ($IsMacOS) { $RuntimeIdentifier = "osx-x64" }
    else { throw "Unsupported host OS. Pass -RuntimeIdentifier win-x64, linux-x64, or osx-x64." }
}

$packageRoot = Join-Path $RepoRoot "src\Trackdub.OnnxRuntime.Dnnl.Native"
$nativeOut = Join-Path $packageRoot "runtimes\$RuntimeIdentifier\native"
$provenanceOut = Join-Path $packageRoot "provenance"
$tag = "v$OnnxRuntimeVersion"

New-Item -ItemType Directory -Force -Path $SourceRoot, $ArtifactsRoot, $nativeOut, $provenanceOut | Out-Null

if (-not (Test-Path (Join-Path $SourceRoot ".git"))) {
    git clone --branch $tag --depth 1 https://github.com/microsoft/onnxruntime.git $SourceRoot
}

Push-Location $SourceRoot
try {
    git fetch --tags --depth 1 origin $tag
    git checkout $tag

    if ($RuntimeIdentifier -eq "win-x64") {
        .\build.bat --config Release --build_shared_lib --parallel --use_dnnl --skip_tests --compile_no_warning_as_error
        $buildNative = Join-Path $SourceRoot "build\Windows\Release\Release"
        $assetName = "onnxruntime.dll"
    } else {
        ./build.sh --config Release --build_shared_lib --parallel --use_dnnl --skip_tests --compile_no_warning_as_error
        $buildNative = Join-Path $SourceRoot "build/Linux/Release"
        if ($RuntimeIdentifier -eq "osx-x64") {
            $buildNative = Join-Path $SourceRoot "build/MacOS/Release"
            $assetName = "libonnxruntime.dylib"
        } else {
            $assetName = "libonnxruntime.so"
        }
    }

    $assetPath = Join-Path $buildNative $assetName
    if (-not (Test-Path $assetPath)) {
        throw "Expected ORT native asset not found: $assetPath"
    }

    Copy-Item -LiteralPath $assetPath -Destination (Join-Path $nativeOut $assetName) -Force
    Get-ChildItem -LiteralPath $buildNative -Filter "*onnxruntime_providers*" -File |
        Copy-Item -Destination $nativeOut -Force

    $sha = @{}
    Get-ChildItem -LiteralPath $nativeOut -File |
        Where-Object { $_.Name -ne ".gitkeep" } |
        ForEach-Object {
            $sha[$_.Name] = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }

    $provenance = [ordered]@{
        package_id = "Trackdub.OnnxRuntime.Dnnl.Native"
        onnxruntime_version = $OnnxRuntimeVersion
        onnxruntime_git_tag = $tag
        git_commit = (git rev-parse HEAD)
        runtime_identifier = $RuntimeIdentifier
        expected_runtime = "onnxruntime-dnnl"
        build_flags = @("--build_shared_lib", "--use_dnnl")
        sha256 = $sha
        generated_utc = (Get-Date).ToUniversalTime().ToString("O")
    }

    $provenancePath = Join-Path $provenanceOut "$RuntimeIdentifier.json"
    $provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8

    dotnet pack (Join-Path $packageRoot "Trackdub.OnnxRuntime.Dnnl.Native.csproj") -c Release -o $ArtifactsRoot
}
finally {
    Pop-Location
}
