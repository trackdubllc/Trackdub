using System.Text.Json.Serialization;
using Trackdub.Domain;
using Trackdub.Infrastructure.Components.NvidiaAfx;
using Trackdub.Infrastructure.Runtime.TrtRtxEp;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure;

/// <summary>
/// Compile-time JSON serialization metadata for local-first startup-path stores.
/// Storage config, smoke verdicts, and runtime bundle manifests deserialize through
/// pre-generated metadata, with identical wire format (compact, case-insensitive
/// property matching) to the reflection-based options they replace.
///
/// This is not a measured startup-latency win: timed cold, the generated-metadata reads
/// for these stores were no faster than the reflection path they replaced (see
/// docs/benchmarks/json-sourcegen-startup-latency.md).
///
/// Types whose serialization depends on runtime-registered converters
/// (<see cref="JsonStudioSettingsService"/>) deliberately stay on reflection-based
/// options: their tolerant converters accept shapes the generated contract would
/// reject, and preserving that behavior outranks metadata-source consistency for
/// those types.
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
///
/// The newline is pinned to "\n" rather than left at the platform default, so the index
/// is byte-identical on every operating system: an index written on Windows carried CRLF
/// while one written on Linux or macOS carried LF, which made checksums, backups, and any
/// external tooling see different files for the same cache state. Indented output is the
/// only thing that has a newline at all, and this context is the only writer of the index.
/// Files written in the other convention still load, because a JSON reader treats CRLF
/// and LF alike, and the next save rewrites them in the pinned form.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LocalModelCacheRecord[]))]
internal sealed partial class LocalModelCacheSerializationContext : JsonSerializerContext
{
}
