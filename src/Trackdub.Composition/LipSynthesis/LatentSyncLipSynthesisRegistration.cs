using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.FaceAnalysis;
using Trackdub.Inference.Onnx.LipSynthesis;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Media.Extraction;

namespace Trackdub.Composition.LipSynthesis;

internal static class LatentSyncLipSynthesisRegistration
{
    internal static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<FfmpegVideoFrameExtractor>();
        services.TryAddSingleton<IVideoFrameExtractor>(
            sp => sp.GetRequiredService<FfmpegVideoFrameExtractor>());
        services.TryAddSingleton<IVideoFrameAssembler>(
            sp => sp.GetRequiredService<FfmpegVideoFrameExtractor>());

        services.TryAddSingleton<IAudioSegmentExtractor, FfmpegAudioSegmentExtractor>();

        services.TryAddSingleton<ILipSynthesisEngine>(sp =>
            new LatentSyncOnnxLipSynthesisEngine(
                sp.GetRequiredService<IRuntimePlanner>(),
                sp.GetRequiredService<BenchmarkModelPathResolver>(),
                sp.GetRequiredService<IVideoFrameExtractor>(),
                sp.GetRequiredService<IVideoFrameAssembler>(),
                sp.GetRequiredService<IAudioSegmentExtractor>(),
                sp.GetRequiredService<BundledModelManifestRegistry>()));

        services.TryAddSingleton<IFaceDetector>(sp =>
            new ScfrdOnnxFaceDetector(
                sp.GetRequiredService<BenchmarkModelPathResolver>(),
                sp.GetRequiredService<IVideoFrameExtractor>()));

        services.TryAddSingleton<GeometryLandmarkProvider>(sp =>
            new GeometryLandmarkProvider(
                sp.GetRequiredService<IFaceDetector>(),
                sp.GetRequiredService<IVideoFrameExtractor>(),
                sp.GetRequiredService<BenchmarkModelPathResolver>()));
        services.TryAddSingleton<IFaceLandmarkProvider>(
            sp => sp.GetRequiredService<GeometryLandmarkProvider>());

        services.TryAddSingleton<IFacePoseEstimator>(sp =>
            new PoseFromLandmarksEstimator(
                sp.GetRequiredService<IFaceLandmarkProvider>()));
    }
}
