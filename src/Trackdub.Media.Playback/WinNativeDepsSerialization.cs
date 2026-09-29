using System.Text.Json.Serialization;

namespace Trackdub.Media.Playback;

/// <summary>
/// Compile-time JSON metadata for the bundled win-native-deps manifest, which is
/// read on every startup that bootstraps media playback. Deserialize through
/// source-generated metadata instead of reflection-based first-call deserialization.
/// Comment/trailing-comma tolerance is layered at the call site's options wrapper,
/// matching the hand-edited manifest shape.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WinNativeDepsManifestRoot))]
internal sealed partial class WinNativeDepsSerializationContext : JsonSerializerContext
{
}
