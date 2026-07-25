using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Reflection;

namespace Trackdub.Inference.Tests;

public sealed class OnnxExecutionProviderSmokeTesterTests
{
    [Fact]
    public void ResolveSeparationSmokeInputDimensions_SpleeterShape()
    {
        int[] dimensions = OnnxExecutionProviderSmokeTester.ResolveSeparationSmokeInputDimensionsForTesting(
            [1, 2, 44100]);

        Assert.Equal([1, 2, 44100], dimensions);
    }

    [Fact]
    public void ResolveDiarizationInputNames_prefers_audio_length_and_generic_single_float_waveform_inputs()
    {
        MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
            .GetMethod("ResolveDiarizationInputNames", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate diarization input-name resolver.");

        object? rawResult = method.Invoke(
            null,
            [
                new Dictionary<string, Type>
                {
                    ["audio_signal"] = typeof(float),
                    ["audio_length"] = typeof(long)
                }
            ]);
        (string waveformName, string? lengthName) inputNames = Assert.IsType<(string, string?)>(rawResult);

        Assert.Equal("audio_signal", inputNames.waveformName);
        Assert.Equal("audio_length", inputNames.lengthName);
    }

    [Fact]
    public void ResolveDiarizationInputNames_falls_back_to_single_float_input_and_any_length_named_long_input()
    {
        MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
            .GetMethod("ResolveDiarizationInputNames", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate diarization input-name resolver.");

        object? rawResult = method.Invoke(
            null,
            [
                new Dictionary<string, Type>
                {
                    ["samples"] = typeof(float),
                    ["chunk_lengths"] = typeof(long)
                }
            ]);
        (string waveformName, string? lengthName) inputNames = Assert.IsType<(string, string?)>(rawResult);

        Assert.Equal("samples", inputNames.waveformName);
        Assert.Equal("chunk_lengths", inputNames.lengthName);
    }

    [Fact]
    public void CreateTtsInputValue_supports_onnxruntime_float16_metadata()
    {
        MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
            .GetMethod("CreateTtsInputValue", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate TTS smoke-input helper.");

        object? rawResult = method.Invoke(null, ["conditioning", typeof(Float16), new[] { 1, 2 }]);
        NamedOnnxValue value = Assert.IsType<NamedOnnxValue>(rawResult);
        Tensor<Float16> tensor = Assert.IsAssignableFrom<Tensor<Float16>>(value.Value);

        Assert.Equal([1, 2], tensor.Dimensions.ToArray());
        Assert.Equal(0f, (float)tensor[0, 0]);
        Assert.Equal(0f, (float)tensor[0, 1]);
    }

    [Fact]
    public void CreateTtsInputValue_supports_half_metadata()
    {
        MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
            .GetMethod("CreateTtsInputValue", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate TTS smoke-input helper.");

        object? rawResult = method.Invoke(null, ["conditioning", typeof(Half), new[] { 1, 2 }]);
        NamedOnnxValue value = Assert.IsType<NamedOnnxValue>(rawResult);
        Tensor<Half> tensor = Assert.IsAssignableFrom<Tensor<Half>>(value.Value);

        Assert.Equal([1, 2], tensor.Dimensions.ToArray());
        Assert.Equal((Half)0f, tensor[0, 0]);
        Assert.Equal((Half)0f, tensor[0, 1]);
    }

    [Fact]
    public void ResolveTtsProbeModelPath_uses_chatterbox_conditional_decoder_for_planned_provider_probe()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "onnx"));
        try
        {
            string languageModelPath = Path.Combine(root, "onnx", "language_model_q4f16.onnx");
            string conditionalDecoderPath = Path.Combine(root, "onnx", "conditional_decoder_q4f16.onnx");
            File.WriteAllText(languageModelPath, string.Empty);
            File.WriteAllText(conditionalDecoderPath, string.Empty);
            MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
                .GetMethod("ResolveTtsProbeModelPath", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not locate TTS smoke probe resolver.");

            object? rawResult = method.Invoke(
                null,
                [
                    "ResembleAI/chatterbox-turbo-ONNX",
                    "chatterbox-turbo-onnx",
                    root,
                    languageModelPath,
                    "q4f16"
                ]);
            string probePath = Assert.IsType<string>(rawResult);

            Assert.Equal(conditionalDecoderPath, probePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveTtsProbeModelPath_falls_back_to_default_chatterbox_conditional_decoder()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "onnx"));
        try
        {
            string languageModelPath = Path.Combine(root, "onnx", "language_model_fp16.onnx");
            string conditionalDecoderPath = Path.Combine(root, "onnx", "conditional_decoder.onnx");
            File.WriteAllText(languageModelPath, string.Empty);
            File.WriteAllText(conditionalDecoderPath, string.Empty);
            MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
                .GetMethod("ResolveTtsProbeModelPath", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not locate TTS smoke probe resolver.");

            object? rawResult = method.Invoke(
                null,
                [
                    "onnx-community/chatterbox-ONNX",
                    "chatterbox-onnx",
                    root,
                    languageModelPath,
                    "fp16"
                ]);
            string probePath = Assert.IsType<string>(rawResult);

            Assert.Equal(conditionalDecoderPath, probePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveTtsProbeModelPath_uses_entry_path_directory_when_model_root_is_blank()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string onnxRoot = Path.Combine(root, "onnx");
        Directory.CreateDirectory(onnxRoot);
        try
        {
            string languageModelPath = Path.Combine(onnxRoot, "language_model_q4f16.onnx");
            string conditionalDecoderPath = Path.Combine(onnxRoot, "conditional_decoder_q4f16.onnx");
            File.WriteAllText(languageModelPath, string.Empty);
            File.WriteAllText(conditionalDecoderPath, string.Empty);
            MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
                .GetMethod("ResolveTtsProbeModelPath", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not locate TTS smoke probe resolver.");

            object? rawResult = method.Invoke(
                null,
                [
                    "ResembleAI/chatterbox-turbo-ONNX",
                    "chatterbox-turbo-onnx",
                    string.Empty,
                    languageModelPath,
                    "q4f16"
                ]);
            string probePath = Assert.IsType<string>(rawResult);

            Assert.Equal(conditionalDecoderPath, probePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveTtsProbeModelPath_keeps_entry_path_for_non_chatterbox_tts_models()
    {
        MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
            .GetMethod("ResolveTtsProbeModelPath", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate TTS smoke probe resolver.");
        string entryPath = Path.Combine(Path.GetTempPath(), "kokoro.onnx");

        object? rawResult = method.Invoke(
            null,
            [
                "onnx-community/Kokoro-82M-v1.0-ONNX",
                "kokoro",
                Path.GetTempPath(),
                entryPath,
                "default"
            ]);
        string probePath = Assert.IsType<string>(rawResult);

        Assert.Equal(entryPath, probePath);
    }

    [Fact]
    public void ResolveNemotronDecoderJointPath_uses_decoder_joint_next_to_encoder()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encoderPath = Path.Combine(root, "encoder.onnx");
            string decoderJointPath = Path.Combine(root, "decoder_joint.onnx");
            File.WriteAllText(encoderPath, string.Empty);
            File.WriteAllText(decoderJointPath, string.Empty);
            MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
                .GetMethod("ResolveNemotronDecoderJointPath", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not locate Nemotron smoke decoder resolver.");

            object? rawResult = method.Invoke(null, [encoderPath]);
            string resolved = Assert.IsType<string>(rawResult);

            Assert.Equal(decoderJointPath, resolved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateNemotronDecoderInputs_defaults_to_int64_token_tensors()
    {
        MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
            .GetMethod("CreateNemotronDecoderInputs", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Nemotron smoke decoder input builder.");
        var encoded = new DenseTensor<float>(new float[1024], [1, 1024, 1]);

        using var inputSet = Assert.IsAssignableFrom<IDisposable>(method.Invoke(
            null,
            [
                new Dictionary<string, NodeMetadata>(),
                encoded
            ]));
        object values = inputSet.GetType().GetProperty("Values")!.GetValue(inputSet)!;
        IReadOnlyList<NamedOnnxValue> namedValues = Assert.IsAssignableFrom<IReadOnlyList<NamedOnnxValue>>(values);
        NamedOnnxValue targets = namedValues.Single(static value => value.Name == "targets");
        NamedOnnxValue targetLength = namedValues.Single(static value => value.Name == "target_length");

        // Per review (P2 on PR #335): smoke path must default to int64 to match the Nemotron
        // decoder export. Empty metadata dict triggers the (now-correct) Int64 fallback.
        Assert.Equal(0L, targets.AsTensor<long>().Single());
        Assert.Equal(1L, targetLength.AsTensor<long>().Single());
    }

    [Theory]
    [InlineData(ExecutionProviderKind.Cpu, "cpu")]
    [InlineData(ExecutionProviderKind.DirectMl, "dml")]
    [InlineData(ExecutionProviderKind.TensorRTRtx, "tensorrt-rtx")]
    public void EnsureSelectedProviderMatchesRequested_accepts_matching_provider(
        ExecutionProviderKind requestedProvider,
        string selectedProvider)
    {
        MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
            .GetMethod("EnsureSelectedProviderMatchesRequested", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate selected-provider assertion helper.");

        method.Invoke(null, [requestedProvider, selectedProvider]);
    }

    [Fact]
    public void EnsureSelectedProviderMatchesRequested_rejects_tensorrt_fallback_to_directml()
    {
        MethodInfo method = typeof(OnnxExecutionProviderSmokeTester)
            .GetMethod("EnsureSelectedProviderMatchesRequested", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate selected-provider assertion helper.");

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [ExecutionProviderKind.TensorRTRtx, "dml"]));
        InvalidOperationException inner = Assert.IsType<InvalidOperationException>(exception.InnerException);

        Assert.Contains("requested provider 'tensorrt-rtx'", inner.Message, StringComparison.Ordinal);
        Assert.Contains("effective provider 'dml'", inner.Message, StringComparison.Ordinal);
    }

}
