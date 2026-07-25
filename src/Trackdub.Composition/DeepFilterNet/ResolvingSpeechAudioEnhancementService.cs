using Trackdub.Contracts;
using Trackdub.Inference.Onnx.DeepFilterNet;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.DeepFilterNet;

internal sealed class ResolvingSpeechAudioEnhancementService(
    BundledModelManifestRegistry? registry,
    IModelCacheInventory? modelCacheInventory,
    ISpeechAudioEnhancementService ffmpegFallback) : ISpeechAudioEnhancementService
{
    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        DeepFilterNetModelPaths? paths = await DeepFilterNetModelPaths
            .TryResolveAsync(registry, modelCacheInventory, cancellationToken)
            .ConfigureAwait(false);
        if (paths is not null && paths.AllFilesExist())
        {
            var deepFilterNet = new DeepFilterNetSpeechAudioEnhancementService(
                new DeepFilterNetEnhancementEngine(paths),
                ffmpegFallback);
            return await deepFilterNet.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await ffmpegFallback.EnhanceAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
