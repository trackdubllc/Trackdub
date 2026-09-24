using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Inference.Onnx.DeepFilterNet;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.DeepFilterNet;

// DeepFilterNet3 is the only speech enhancement backend. When its model is not cached the
// stage is skipped (RequiredModelNotAvailableException) and downstream stages use the mix:
// the ffmpeg denoise/speechnorm chain flagged music as speech for VAD and hurt ASR, so it is
// not a safe fallback.
internal sealed class ResolvingSpeechAudioEnhancementService(
    BundledModelManifestRegistry? registry,
    IModelCacheInventory? modelCacheInventory) : ISpeechAudioEnhancementService
{
    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeepFilterNetModelPaths? paths = await DeepFilterNetModelPaths
            .TryResolveAsync(registry, modelCacheInventory, cancellationToken)
            .ConfigureAwait(false);
        if (paths is null || !paths.AllFilesExist())
        {
            throw new RequiredModelNotAvailableException(
                "Rikorose/DeepFilterNet3",
                paths?.RootDirectory ?? string.Empty);
        }

        return await new DeepFilterNetEnhancementEngine(paths)
            .EnhanceAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
