using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Kokoro;

/// <summary>
/// Process-wide shared <see cref="SidecarCache{T}"/> instances for Kokoro model sidecar data.
/// </summary>
/// <remarks>
/// These caches are intentionally static / process-scoped so that tokenizer and voice-catalog
/// data survive DI scope boundaries.  A new <see cref="KokoroTtsEngine"/> instance created in
/// a fresh scope will reuse sidecar data that was already loaded by a prior instance, provided
/// the model-root path is identical.
///
/// <para>Eviction: entries are never evicted automatically.  If a model root changes at runtime
/// (hot-swap during development), call <c>KokoroSidecarCaches.Tokenizers.Remove(modelRootPath)</c>
/// and <c>KokoroSidecarCaches.VoiceCatalogs.Remove(modelRootPath)</c> to flush the stale entries.
/// </para>
/// </remarks>
internal static class KokoroSidecarCaches
{
    /// <summary>Shared tokenizer cache keyed by model-root path.</summary>
    public static readonly SidecarCache<KokoroTokenizer> Tokenizers = new();

    /// <summary>Shared voice-catalog cache keyed by model-root path.</summary>
    public static readonly SidecarCache<KokoroVoiceCatalog> VoiceCatalogs = new();
}
