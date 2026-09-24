using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Onnx.SortFormer;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class EpContextTrtProfilesTests
{
    private static readonly string[] SortFormerStreamingInputs =
        ["chunk", "chunk_lengths", "spkcache", "spkcache_lengths", "fifo", "fifo_lengths"];

    [Fact]
    public void SortFormer_streaming_export_resolves_the_production_profile()
    {
        Assert.Same(SortFormerDiarizationEngine.TrtOptions, EpContextTrtProfiles.Resolve(SortFormerStreamingInputs));
    }

    [Fact]
    public void SortFormer_profile_names_only_real_streaming_inputs_with_consistent_bounds()
    {
        foreach (string key in new[] { "trt_profile_min_shapes", "trt_profile_opt_shapes", "trt_profile_max_shapes" })
        {
            Dictionary<string, int[]> shapes = ParseProfile(SortFormerDiarizationEngine.TrtOptions[key]);
            Assert.Subset(SortFormerStreamingInputs.ToHashSet(), shapes.Keys.ToHashSet());
            Assert.Equal([1, 3040, 128], shapes["chunk"]);
        }

        Dictionary<string, int[]> min = ParseProfile(SortFormerDiarizationEngine.TrtOptions["trt_profile_min_shapes"]);
        Dictionary<string, int[]> max = ParseProfile(SortFormerDiarizationEngine.TrtOptions["trt_profile_max_shapes"]);
        // The first streaming step feeds an empty speaker cache and FIFO.
        Assert.Equal([1, 0, 512], min["spkcache"]);
        Assert.Equal([1, 0, 512], min["fifo"]);
        Assert.Equal([1, 188, 512], max["spkcache"]);
        Assert.Equal([1, 40, 512], max["fifo"]);
    }

    [Fact]
    public void Nemotron_encoder_resolves_prompt_aware_profile()
    {
        IReadOnlyDictionary<string, string>? withPrompt = EpContextTrtProfiles.Resolve(
            ["processed_signal", "processed_signal_length", "cache_last_channel", "cache_last_time", "cache_last_channel_len", "prompt_index"]);
        IReadOnlyDictionary<string, string>? withoutPrompt = EpContextTrtProfiles.Resolve(
            ["processed_signal", "processed_signal_length", "cache_last_channel", "cache_last_time", "cache_last_channel_len"]);

        Assert.NotNull(withPrompt);
        Assert.NotNull(withoutPrompt);
        Assert.Contains("prompt_index:1", withPrompt["trt_profile_min_shapes"], StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_index", withoutPrompt["trt_profile_min_shapes"], StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_graphs_keep_the_default_dynamic_profile()
    {
        Assert.Null(EpContextTrtProfiles.Resolve(["input", "state", "sr"]));
    }

    private static Dictionary<string, int[]> ParseProfile(string profile) =>
        profile.Split(',')
            .Select(static entry => entry.Split(':'))
            .ToDictionary(
                static parts => parts[0],
                static parts => parts[1].Split('x').Select(int.Parse).ToArray());
}
