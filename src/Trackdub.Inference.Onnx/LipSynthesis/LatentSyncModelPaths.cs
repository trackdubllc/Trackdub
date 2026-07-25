namespace Trackdub.Inference.Onnx.LipSynthesis;

/// <summary>
/// Model path helpers for LatentSync and related face analysis engines.
/// Paths are resolved dynamically via BenchmarkModelPathResolver.Discover(), not hardcoded;
/// helper methods validate existence and build platform-agnostic paths from model root.
/// </summary>
internal static class LatentSyncModelPaths
{
    public const string EngineFamily = "latentsync-diffusion";
    public const string ManifestAlias = "latentsync";
    public const string ModelId = "ByteDance/LatentSync-1.6";
    public const string ScfrdEngineFamily = "scrfd";
    public const string LandmarkEngineFamily = "insightface-2d106";
    public const string LandmarkModelAlias = "InsightFace/2d106det";

    public static string UNetPath(string modelRoot) =>
        Path.Combine(modelRoot, "unet.onnx");

    public static string VaeEncoderPath(string modelRoot) =>
        Path.Combine(modelRoot, "vae_encoder.onnx");

    public static string VaeDecoderPath(string modelRoot) =>
        Path.Combine(modelRoot, "vae_decoder.onnx");

    public static string WhisperEncoderPath(string modelRoot) =>
        Path.Combine(modelRoot, "whisper_encoder.onnx");

    public static string ScfrdModelPath(string modelRoot) =>
        Path.Combine(modelRoot, "scrfd_500m.onnx");

    public static bool AreLatentSyncFilesPresent(string modelRoot) =>
        Directory.Exists(modelRoot) &&
        File.Exists(UNetPath(modelRoot)) &&
        File.Exists(VaeEncoderPath(modelRoot)) &&
        File.Exists(VaeDecoderPath(modelRoot)) &&
        File.Exists(WhisperEncoderPath(modelRoot));

    public static string Landmark2D106ModelPath(string modelRoot) =>
        Path.Combine(modelRoot, "2d106det.onnx");

    public static bool AreScfrdFilesPresent(string modelRoot) =>
        Directory.Exists(modelRoot) &&
        File.Exists(ScfrdModelPath(modelRoot));

    public static bool AreLandmarkFilesPresent(string modelRoot) =>
        Directory.Exists(modelRoot) &&
        File.Exists(Landmark2D106ModelPath(modelRoot));
}
