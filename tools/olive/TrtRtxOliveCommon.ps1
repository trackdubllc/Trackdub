# Shared helpers for the Olive TRT-RTX validator scripts. Dot-source this file.

<#
.SYNOPSIS
    Resolves the model cache directory the way the Trackdub CLI/app does.

.DESCRIPTION
    Mirrors TrackdubStoragePathResolver + TrackdubStoragePaths.ModelCacheDirectory so the
    validators look where `trackdub`/Trackdub.Tools ingest actually downloaded the models:

      1. TRACKDUB_MODEL_CACHE          explicit model cache directory (benchmark/test convention)
      2. TRACKDUB_CACHE_ROOT or
         TRACKDUB_DATA_ROOT            <cache root, else data root>\model-cache
      3. TRACKDUB_PORTABLE_DATA_ROOT   <portable data root>\model-cache (when TRACKDUB_PORTABLE is set)
      4. storage.json                  %LOCALAPPDATA%\Trackdub\storage.json, then
                                       %PROGRAMDATA%\Trackdub\storage.json:
                                       <userCacheRoot, else userDataRoot>\model-cache
      5. default                       %LOCALAPPDATA%\Trackdub\model-cache

    Portable installs detected only from a marker file next to the app binaries cannot be
    seen from here; set TRACKDUB_MODEL_CACHE for those.
#>
function Resolve-TrackdubModelCacheRoot {
    if ($env:TRACKDUB_MODEL_CACHE) {
        return $env:TRACKDUB_MODEL_CACHE
    }

    if ($env:TRACKDUB_CACHE_ROOT -or $env:TRACKDUB_DATA_ROOT) {
        $root = if ($env:TRACKDUB_CACHE_ROOT) { $env:TRACKDUB_CACHE_ROOT } else { $env:TRACKDUB_DATA_ROOT }
        return Join-Path $root 'model-cache'
    }

    if ($env:TRACKDUB_PORTABLE -and $env:TRACKDUB_PORTABLE -notin @('0', 'false', 'no') -and $env:TRACKDUB_PORTABLE_DATA_ROOT) {
        return Join-Path $env:TRACKDUB_PORTABLE_DATA_ROOT 'model-cache'
    }

    foreach ($configPath in @(
            (Join-Path $env:LOCALAPPDATA 'Trackdub\storage.json'),
            (Join-Path $env:ProgramData 'Trackdub\storage.json'))) {
        if (-not (Test-Path $configPath)) { continue }
        try {
            $config = Get-Content -Raw $configPath | ConvertFrom-Json
        } catch {
            continue
        }
        $configured = @($config.userCacheRoot, $config.userDataRoot) | Where-Object { $_ } | Select-Object -First 1
        if ($configured) {
            return Join-Path $configured 'model-cache'
        }
    }

    return Join-Path $env:LOCALAPPDATA 'Trackdub\model-cache'
}
