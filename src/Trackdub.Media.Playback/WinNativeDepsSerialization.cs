using System.Text.Json.Serialization;

namespace Trackdub.Media.Playback;

/// <summary>
/// Compile-time JSON metadata for the bundled win-native-deps manifest, which is
/// read on every startup that bootstraps media playback.
/// Comment/trailing-comma tolerance is layered at the call site's options wrapper,
/// matching the hand-edited manifest shape.
///
/// Timed cold, this manifest's generated-metadata read was not faster than the
/// reflection path it replaced (see docs/benchmarks/json-sourcegen-startup-latency.md).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WinNativeDepsManifestRoot))]
internal sealed partial class WinNativeDepsSerializationContext : JsonSerializerContext
{
}
