using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.NemotronAsr;

internal static class NemotronAsrEncoderTrtProfiles
{
    private const string ProfileWithoutPromptIndex =
        "processed_signal:1x65x128,processed_signal_length:1,cache_last_channel:1x24x70x1024,cache_last_time:1x24x1024x8,cache_last_channel_len:1";

    private const string PromptIndexSuffix = ",prompt_index:1";

    private static readonly ConcurrentDictionary<string, bool> PromptInputCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, string> BuildOptions(string encoderModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderModelPath);
        return BuildOptions(EncoderHasPromptInput(encoderModelPath));
    }

    internal static IReadOnlyDictionary<string, string> BuildOptions(bool hasPromptInput)
    {
        string shapes = hasPromptInput
            ? ProfileWithoutPromptIndex + PromptIndexSuffix
            : ProfileWithoutPromptIndex;

        return new Dictionary<string, string>
        {
            // Bundled Nemotron export expects time-major [B,T,mel]; TRT profiles must match encoder inputs.
            ["trt_profile_min_shapes"] = shapes,
            ["trt_profile_max_shapes"] = shapes,
            ["trt_profile_opt_shapes"] = shapes,
        };
    }

    internal static bool EncoderHasPromptInput(string encoderModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderModelPath);
        return PromptInputCache.GetOrAdd(encoderModelPath, static path =>
        {
            using InferenceSession session = new(path);
            return session.InputMetadata.ContainsKey("prompt_index");
        });
    }
}
