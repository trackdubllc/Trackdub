using System.Text.Json.Serialization;
using Trackdub.Domain;
using Trackdub.Infrastructure.Components.NvidiaAfx;
using Trackdub.Infrastructure.Runtime.TrtRtxEp;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure;

/// <summary>
/// Compile-time JSON serialization metadata for local-first startup-path stores.
/// Storage config, smoke verdicts, and runtime bundle manifests deserialize through
/// pre-generated metadata instead of reflection-based first-call deserialization,
/// with identical wire format (compact, case-insensitive property matching).
///
/// Types whose serialization depends on runtime-registered converters
/// (<see cref="JsonStudioSettingsService"/>) deliberately stay on reflection-based
/// options: their tolerant converters accept shapes the generated contract would
/// reject, and preserving that behavior outranks the latency win.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TrackdubStoragePathResolver.StorageConfig))]
[JsonSerializable(typeof(FileSmokeVerdictStore.SmokeVerdictFilePayload))]
[JsonSerializable(typeof(TrtRtxEpBundleManifestLoader.TrtRtxEpBundleManifestDto))]
[JsonSerializable(typeof(NvidiaAfxRuntimeManifest))]
internal sealed partial class InfrastructureSerializationContext : JsonSerializerContext
{
}

/// <summary>
/// Indented variant for the local model cache index, whose on-disk format is
/// human-readable JSON (WriteIndented = true). Kept separate because source
/// generation options are per-context.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LocalModelCacheRecord[]))]
internal sealed partial class LocalModelCacheSerializationContext : JsonSerializerContext
{
}
