using System.Reflection;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Chatterbox;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Tests;

public sealed class ChatterboxVoiceCloneTtsEngineTests
{
    [Fact]
    public void BuildInitialBaseEmbedPositionIds_matches_onnx_reference_position_contract()
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("BuildInitialBaseEmbedPositionIds", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox position-id helper.");

        // Correct HF post-processor sequence: [EXAGGERATION]=6563, [START]=255, bpe..., [STOP]=0, [START_SPEECH]=6561×2
        object? rawResult = method.Invoke(null, [new long[] { 6563, 255, 42, 0, 6561, 6561 }]);
        long[] positionIds = Assert.IsType<long[]>(rawResult);

        Assert.Equal([0L, 0L, 1L, 2L, 0L, 0L], positionIds);
    }

    [Fact]
    public void BuildTextInputIds_for_turbo_appends_endoftext_speech_placeholders()
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("BuildTextInputIds", BindingFlags.NonPublic | BindingFlags.Static, [typeof(long[]), typeof(bool)])
            ?? throw new InvalidOperationException("Could not locate Chatterbox input-id helper.");

        object? rawResult = method.Invoke(null, [new long[] { 10, 20, 30 }, true]);
        long[] inputIds = Assert.IsType<long[]>(rawResult);

        Assert.Equal([10L, 20L, 30L, 50256L, 50256L], inputIds);
    }

    [Fact]
    public void BuildTextInputIds_for_non_turbo_keeps_legacy_sentinel_wrapper()
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("BuildTextInputIds", BindingFlags.NonPublic | BindingFlags.Static, [typeof(long[]), typeof(bool)])
            ?? throw new InvalidOperationException("Could not locate Chatterbox input-id helper.");

        object? rawResult = method.Invoke(null, [new long[] { 10, 20, 30 }, false]);
        long[] inputIds = Assert.IsType<long[]>(rawResult);

        Assert.Equal([6563L, 255L, 10L, 20L, 30L, 0L, 6561L, 6561L], inputIds);
    }

    [Fact]
    public void EnsureMinimumReferenceAudioLength_pads_tiny_reference_clips()
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("EnsureMinimumReferenceAudioLength", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox reference-audio padding helper.");

        object? rawResult = method.Invoke(null, [new float[] { 0.25f, -0.5f }]);
        float[] samples = Assert.IsType<float[]>(rawResult);

        Assert.Equal(1000, samples.Length);
        Assert.Equal(0.25f, samples[0]);
        Assert.Equal(-0.5f, samples[1]);
        Assert.Equal(0f, samples[^1]);
    }

    [Fact]
    public void EnsureMinimumReferenceAudioLength_keeps_long_reference_clips()
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("EnsureMinimumReferenceAudioLength", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox reference-audio padding helper.");
        float[] source = Enumerable.Range(0, 1000).Select(static value => (float)value).ToArray();

        object? rawResult = method.Invoke(null, [source]);
        float[] samples = Assert.IsType<float[]>(rawResult);

        Assert.Same(source, samples);
    }

    [Fact]
    public void Pcm16WaveReader_ResampleLinear_uses_wide_math_for_long_reference_clips()
    {
        Type pcmReaderType = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetNestedType("Pcm16WaveReader", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not locate Chatterbox PCM reader helper.");
        MethodInfo method = pcmReaderType
            .GetMethod("ResampleLinear", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox resampler helper.");
        float[] source = Enumerable.Repeat(0.25f, 386_880).ToArray();

        object? rawResult = method.Invoke(null, [source, 48_000, 24_000]);
        float[] resampled = Assert.IsType<float[]>(rawResult);

        Assert.Equal(193_440, resampled.Length);
    }

    [Theory]
    [InlineData(null, 1024)]
    [InlineData(2.0d, 128)]
    [InlineData(5.0d, 219)]
    [InlineData(60.0d, 1024)]
    public void ResolveMaxNewTokens_caps_voice_clone_generation_to_segment_budget(double? targetDurationSeconds, int expectedMaxNewTokens)
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("ResolveMaxNewTokens", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox max-token budget helper.");

        object? rawResult = method.Invoke(null, [targetDurationSeconds]);
        int maxNewTokens = Assert.IsType<int>(rawResult);

        Assert.Equal(expectedMaxNewTokens, maxNewTokens);
    }

    [Fact]
    public void CreateFloatInputForTesting_uses_half_tensor_when_metadata_requires_float16()
    {
        NamedOnnxValue value = ChatterboxVoiceCloneTtsEngine.CreateFloatInputForTesting(
            "inputs_embeds",
            typeof(Half),
            [1.5f, -2.25f],
            [1, 2]);

        Tensor<Half> tensor = Assert.IsAssignableFrom<Tensor<Half>>(value.Value);
        Assert.Equal([1, 2], tensor.Dimensions.ToArray());
        Assert.Equal((Half)1.5f, tensor[0, 0]);
        Assert.Equal((Half)(-2.25f), tensor[0, 1]);
    }

    [Fact]
    public void CreateFloatInputForTesting_keeps_float_tensor_when_metadata_requires_float32()
    {
        NamedOnnxValue value = ChatterboxVoiceCloneTtsEngine.CreateFloatInputForTesting(
            "inputs_embeds",
            typeof(float),
            [1.5f, -2.25f],
            [1, 2]);

        Tensor<float> tensor = Assert.IsAssignableFrom<Tensor<float>>(value.Value);
        Assert.Equal([1, 2], tensor.Dimensions.ToArray());
        Assert.Equal(1.5f, tensor[0, 0]);
        Assert.Equal(-2.25f, tensor[0, 1]);
    }

    [Fact]
    public void CreatePastInputForTesting_converts_float_cache_to_half_when_metadata_requires_float16()
    {
        NamedOnnxValue value = ChatterboxVoiceCloneTtsEngine.CreatePastInputForTesting(
            "past_key_values.0.key",
            typeof(Half),
            [1.5f, -2.25f],
            [1, 1, 1, 2]);

        Tensor<Half> tensor = Assert.IsAssignableFrom<Tensor<Half>>(value.Value);
        Assert.Equal([1, 1, 1, 2], tensor.Dimensions.ToArray());
        Assert.Equal((Half)1.5f, tensor[0, 0, 0, 0]);
        Assert.Equal((Half)(-2.25f), tensor[0, 0, 0, 1]);
    }

    [Theory]
    [InlineData("present.0.key", "past_key_values.0.key")]
    [InlineData("present.15.value", "past_key_values.15.value")]
    [InlineData("present_key_values.0.key", "past_key_values.0.key")]
    [InlineData("present_key_values.3.value", "past_key_values.3.value")]
    public void MapLanguageModelPresentOutputToPastInputName_matches_export_naming_variants(string outputName, string expectedPastInputName)
    {
        Assert.Equal(expectedPastInputName, ChatterboxVoiceCloneTtsEngine.MapLanguageModelPresentOutputToPastInputName(outputName));
    }

    [Theory]
    [InlineData(ExecutionProviderKind.Cpu, ExecutionProviderKind.Cpu)]
    [InlineData(ExecutionProviderKind.DirectMl, ExecutionProviderKind.Cpu)]
    [InlineData(ExecutionProviderKind.TensorRTRtx, ExecutionProviderKind.Cpu)]
    public void ResolveReferenceConditioningProvider_keeps_directml_incompatible_chatterbox_conditioning_on_cpu(
        ExecutionProviderKind plannedProvider,
        ExecutionProviderKind expectedSidecarProvider)
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("ResolveReferenceConditioningProvider", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox reference-conditioning provider helper.");

        object? rawResult = method.Invoke(null, [plannedProvider]);
        ExecutionProviderKind provider = Assert.IsType<ExecutionProviderKind>(rawResult);

        Assert.Equal(expectedSidecarProvider, provider);
    }

    [Theory]
    [InlineData(ExecutionProviderKind.Cpu, ExecutionProviderKind.Cpu)]
    [InlineData(ExecutionProviderKind.DirectMl, ExecutionProviderKind.Cpu)]
    [InlineData(ExecutionProviderKind.TensorRTRtx, ExecutionProviderKind.Cpu)]
    public void ResolveLanguageModelProvider_keeps_chatterbox_autoregressive_tokens_on_cpu(
        ExecutionProviderKind plannedProvider,
        ExecutionProviderKind expectedLanguageModelProvider)
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("ResolveLanguageModelProvider", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox language-model provider helper.");

        object? rawResult = method.Invoke(null, [plannedProvider]);
        ExecutionProviderKind provider = Assert.IsType<ExecutionProviderKind>(rawResult);

        Assert.Equal(expectedLanguageModelProvider, provider);
    }

    [Theory]
    [InlineData(ExecutionProviderKind.Cpu)]
    [InlineData(ExecutionProviderKind.DirectMl)]
    [InlineData(ExecutionProviderKind.TensorRTRtx)]
    public void ResolveConditionalDecoderProvider_keeps_decoder_on_planned_provider(ExecutionProviderKind plannedProvider)
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("ResolveConditionalDecoderProvider", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox conditional decoder provider helper.");

        object? rawResult = method.Invoke(null, [plannedProvider]);
        ExecutionProviderKind provider = Assert.IsType<ExecutionProviderKind>(rawResult);

        Assert.Equal(plannedProvider, provider);
    }

    [Fact]
    public void BuildHybridProviderBootstrapDetail_reports_conditioning_cpu_split()
    {
        MethodInfo method = typeof(ChatterboxVoiceCloneTtsEngine)
            .GetMethod("BuildHybridProviderBootstrapDetail", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Chatterbox hybrid-provider detail helper.");

        object? rawResult = method.Invoke(null, [
            "cpu",
            "cpu",
            "cpu",
            "dml",
            "Windows ML bootstrap succeeded via RegisterInstalledCertified for packaged DirectML route."
        ]);
        string detail = Assert.IsType<string>(rawResult);

        Assert.Contains("speech encoder", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("embedding", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("language model", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decoder", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CPU", detail, StringComparison.Ordinal);
        Assert.Contains("AveragePool", detail, StringComparison.Ordinal);
        Assert.Contains("Slice", detail, StringComparison.Ordinal);
        Assert.Contains("DirectML", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePastInputForTesting_uses_onnxruntime_float16_when_metadata_requires_float16()
    {
        NamedOnnxValue value = ChatterboxVoiceCloneTtsEngine.CreatePastInputForTesting(
            "past_key_values.0.key",
            typeof(Float16),
            [1.5f, -2.25f],
            [1, 1, 1, 2]);

        Tensor<Float16> tensor = Assert.IsAssignableFrom<Tensor<Float16>>(value.Value);
        Assert.Equal([1, 1, 1, 2], tensor.Dimensions.ToArray());
        Assert.Equal(1.5f, (float)tensor[0, 0, 0, 0]);
        Assert.Equal(-2.25f, (float)tensor[0, 0, 0, 1]);
    }
}
