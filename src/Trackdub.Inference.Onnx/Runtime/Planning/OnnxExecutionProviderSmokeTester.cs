using Microsoft.ML.OnnxRuntimeGenAI;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

public sealed class OnnxExecutionProviderSmokeTester : IExecutionProviderSmokeTester
{
    public async Task<ExecutionProviderSmokeTestResult> SmokeTestAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            switch (request.Stage)
            {
                case RuntimeStage.Vad:
                    await SmokeTestVadAsync(request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.Asr:
                    await SmokeTestAsrAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.Separation:
                    await SmokeTestSeparationAsync(request.ModelId, request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.Translation:
                    await SmokeTestTranslationAsync(request.EntryPath, request.ModelAlias, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.Diarization:
                    await SmokeTestDiarizationAsync(request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.Tts:
                    await SmokeTestTtsAsync(
                        request.ModelId,
                        request.ModelAlias,
                        request.ModelRootPath,
                        request.EntryPath,
                        request.Variant,
                        request.ExecutionProvider,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.TextRefinement:
                    await SmokeTestTextRefinementGenAiAsync(
                        request.ModelRootPath,
                        request.ExecutionProvider,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    return new ExecutionProviderSmokeTestResult(
                        false,
                        $"Smoke testing is not implemented for runtime stage '{request.Stage}'.");
            }

            return new ExecutionProviderSmokeTestResult(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExecutionProviderSmokeTestResult(false, ex.Message);
        }
    }

    private static async Task SmokeTestTextRefinementGenAiAsync(
        string modelRootPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        string configPath = Path.Combine(modelRootPath, "genai_config.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "Text refinement smoke test requires genai_config.json in the model root.",
                configPath);
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Model model = CreateGenAiSmokeModel(modelRootPath, provider);
            using Tokenizer tokenizer = new(model);
            using GeneratorParams generatorParams = new(model);
            using Sequences input = tokenizer.Encode("Hello");
            using Generator generator = new(model, generatorParams);
            generator.AppendTokenSequences(input);
            generator.GenerateNextToken();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Model CreateGenAiSmokeModel(string modelRootPath, ExecutionProviderKind provider)
    {
        if (provider is ExecutionProviderKind.Cpu)
        {
            return new Model(modelRootPath);
        }

        using Config config = new(modelRootPath);
        config.ClearProviders();
        config.AppendProvider(provider switch
        {
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRTRtx => "trt-rtx",
            ExecutionProviderKind.CoreMl => "coreml",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported GenAI smoke provider.")
        });
        return new Model(config);
    }

    private static async Task SmokeTestVadAsync(
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreateSingleAsync(modelPath, provider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var input = CreateVadInputs();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.Session.Run(input.Values);
    }

    private static async Task SmokeTestWhisperAsync(
        string encoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        string decoderModelPath = ResolveWhisperDecoderPath(encoderModelPath);
        using OnnxExecutionSessionFactory.WhisperSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreateWhisperAsync(encoderModelPath, decoderModelPath, provider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var encoderInputs = CreateWhisperEncoderInputs();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults = sessionLease.EncoderSession.Run(encoderInputs.Values);
        Tensor<float> hiddenStates = encoderResults.Single().AsTensor<float>();

        using var decoderInputs = CreateWhisperDecoderInputs(hiddenStates);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.DecoderSession.Run(decoderInputs.Values);
    }

    private static async Task SmokeTestAsrAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken)
    {
        string engineFamily = request.EngineFamily?.Trim() ?? string.Empty;
        if (engineFamily.Equals("qwen3-asr", StringComparison.OrdinalIgnoreCase))
        {
            await SmokeTestQwen3AsrAsync(request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (engineFamily.Equals("nemotron-asr", StringComparison.OrdinalIgnoreCase))
        {
            await SmokeTestNemotronAsrAsync(request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SmokeTestWhisperAsync(request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SmokeTestQwen3AsrAsync(
        string encoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        string root = Path.GetDirectoryName(encoderModelPath)
            ?? throw new InvalidOperationException("Qwen3-ASR smoke test could not resolve model root.");
        string decoderInitPath = Path.Combine(root, "decoder_init.onnx");
        string decoderStepPath = Path.Combine(root, "decoder_step.onnx");
        using OnnxExecutionSessionFactory.Qwen3AsrSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledQwen3AsrAsync("qwen3-asr", encoderModelPath, decoderInitPath, decoderStepPath, provider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var encoderInputs = new InputSet([
            NamedOnnxValue.CreateFromTensor("mel", new DenseTensor<float>(new float[128], [1, 128, 1]))
        ]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.EncoderSession.Run(encoderInputs.Values);
    }

    private static async Task SmokeTestNemotronAsrAsync(
        string encoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        string decoderJointPath = ResolveNemotronDecoderJointPath(encoderModelPath);
        using OnnxExecutionSessionFactory.NemotronAsrSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledNemotronAsrAsync("nemotron-asr", encoderModelPath, decoderJointPath, provider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var encoderInputs = CreateNemotronEncoderInputs(sessionLease.EncoderSession.InputMetadata);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults =
            sessionLease.EncoderSession.Run(encoderInputs.Values);
        Tensor<float> encoded = encoderResults.Single(static result => result.Name == "encoded").AsTensor<float>();
        using var decoderInputs = CreateNemotronDecoderInputs(sessionLease.DecoderJointSession.InputMetadata, encoded);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.DecoderJointSession.Run(decoderInputs.Values);
    }

    private static async Task SmokeTestSeparationAsync(
        string modelId,
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        string runnableModelPath = modelPath;
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreateSingleAsync(runnableModelPath, provider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var inputs = CreateSeparationInputs(sessionLease.Session);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.Session.Run(inputs.Values);
    }

    private static async Task SmokeTestDiarizationAsync(
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreateSingleAsync(modelPath, provider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var inputs = CreateDiarizationInputs(sessionLease.Session);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.Session.Run(inputs.Values);
    }

    private static async Task SmokeTestTtsAsync(
        string modelId,
        string modelAlias,
        string modelRootPath,
        string entryPath,
        string variant,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        string modelPath = ResolveTtsProbeModelPath(modelId, modelAlias, modelRootPath, entryPath, variant);
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreateSingleAsync(modelPath, provider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var inputs = CreateTtsInputs(sessionLease.Session.InputMetadata);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.Session.Run(inputs.Values);
    }

    private static InputSet CreateVadInputs()
    {
        IReadOnlyList<NamedOnnxValue> values =
        [
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(new float[512], [1, 512])),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(new float[2 * 128], [2, 1, 128])),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16000 }, [1]))
        ];

        return new InputSet(values);
    }

    private static InputSet CreateWhisperEncoderInputs()
    {
        IReadOnlyList<NamedOnnxValue> values =
        [
            NamedOnnxValue.CreateFromTensor("input_features", new DenseTensor<float>(new float[80 * 3000], [1, 80, 3000]))
        ];

        return new InputSet(values);
    }

    private static InputSet CreateWhisperDecoderInputs(Tensor<float> encoderHiddenStates)
    {
        IReadOnlyList<NamedOnnxValue> values =
        [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(new long[] { 50258 }, [1, 1])),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates)
        ];

        return new InputSet(values);
    }

    private static InputSet CreateNemotronEncoderInputs(IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        int[] channelDims = ResolveMetadataDims(inputMetadata, "cache_last_channel", [24, 1, 56, 1024]);
        int[] timeDims = ResolveMetadataDims(inputMetadata, "cache_last_time", [24, 1, 1024, 8]);
        int channelCount = channelDims.Aggregate(1, static (product, dimension) => checked(product * dimension));
        int timeCount = timeDims.Aggregate(1, static (product, dimension) => checked(product * dimension));
        // Bundled Nemotron export expects time-major [B,T,mel]; transpose from C# mel-major.
        var values = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("processed_signal", new DenseTensor<float>(new float[65 * 128], [1, 65, 128])),
            NamedOnnxValue.CreateFromTensor("processed_signal_length", new DenseTensor<long>(new long[] { 65 }, [1])),
            NamedOnnxValue.CreateFromTensor("cache_last_channel", new DenseTensor<float>(new float[channelCount], channelDims)),
            NamedOnnxValue.CreateFromTensor("cache_last_time", new DenseTensor<float>(new float[timeCount], timeDims)),
            NamedOnnxValue.CreateFromTensor("cache_last_channel_len", new DenseTensor<long>(new long[] { 0 }, [1]))
        };

        if (inputMetadata.ContainsKey("prompt_index"))
        {
            values.Add(NamedOnnxValue.CreateFromTensor("prompt_index", new DenseTensor<long>(new long[] { 101 }, [1])));
        }

        return new InputSet(values);
    }

    private static InputSet CreateNemotronDecoderInputs(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        Tensor<float> encoded)
    {
        // Encoder output is time-major [batch, time, hidden]; hidden is Dimensions[2].
        int hiddenSize = encoded.Dimensions.Length >= 3 ? encoded.Dimensions[2] : 1024;
        int[] stateDims = ResolveMetadataDims(inputMetadata, "input_states_1", [2, 1, 640]);
        int stateCount = stateDims.Aggregate(1, static (product, dimension) => checked(product * dimension));
        var frame = new DenseTensor<float>(new float[hiddenSize], [1, hiddenSize, 1]);
        for (int hiddenIndex = 0; hiddenIndex < hiddenSize && encoded.Dimensions.Length >= 3; hiddenIndex++)
        {
            frame[0, hiddenIndex, 0] = encoded[0, 0, hiddenIndex];
        }

        return new InputSet(
        [
            NamedOnnxValue.CreateFromTensor("encoder_outputs", frame),
            CreateNemotronDecoderTokenInput(inputMetadata, "targets", 0, [1, 1]),
            CreateNemotronDecoderTokenInput(inputMetadata, "target_length", 1, [1]),
            NamedOnnxValue.CreateFromTensor("input_states_1", new DenseTensor<float>(new float[stateCount], stateDims)),
            NamedOnnxValue.CreateFromTensor("input_states_2", new DenseTensor<float>(new float[stateCount], stateDims))
        ]);
    }

    private static NamedOnnxValue CreateNemotronDecoderTokenInput(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        string inputName,
        long value,
        int[] dimensions)
    {
        TensorElementType elementType = inputMetadata.TryGetValue(inputName, out NodeMetadata? metadata)
            ? metadata.ElementDataType
            : TensorElementType.Int64;   // Nemotron decoder graph exports targets/target_length as int64 (per export + review P2); smoke must not default to int32 or provider verification fails with type mismatch.

        return elementType switch
        {
            TensorElementType.Int32 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<int>(new[] { checked((int)value) }, dimensions)),
            TensorElementType.Int64 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<long>(new[] { value }, dimensions)),
            _ => throw new NotSupportedException(
                $"Nemotron decoder input '{inputName}' uses unsupported token tensor element type '{elementType}'.")
        };
    }

    private static int[] ResolveMetadataDims(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        string inputName,
        int[] fallback)
    {
        if (!inputMetadata.TryGetValue(inputName, out NodeMetadata? metadata) ||
            metadata.Dimensions.Length != fallback.Length)
        {
            return fallback;
        }

        var dims = new int[fallback.Length];
        for (int index = 0; index < dims.Length; index++)
        {
            dims[index] = metadata.Dimensions[index] > 0 ? metadata.Dimensions[index] : fallback[index];
        }

        return dims;
    }

    private static InputSet CreateSeparationInputs(InferenceSession session)
    {
        (string inputName, NodeMetadata metadata) = session.InputMetadata.First();
        int[] dimensions = ResolveSeparationSmokeInputDimensions(metadata.Dimensions);
        int elementCount = dimensions.Aggregate(1, static (product, dimension) => checked(product * dimension));
        return new InputSet(
        [
            NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(new float[elementCount], dimensions))
        ]);
    }

    internal static int[] ResolveSeparationSmokeInputDimensionsForTesting(IReadOnlyList<int> modelDimensions) =>
        ResolveSeparationSmokeInputDimensions(modelDimensions);

    private static int[] ResolveSeparationSmokeInputDimensions(IReadOnlyList<int> modelDimensions)
    {
        if (modelDimensions.Count == 4) return [2, 1, 512, 1024];

        int[] dimensions = modelDimensions.Count switch
        {
            2 => [2, 44100],
            3 => [1, 2, 44100],
            _ => modelDimensions
                .Select(static dimension => dimension > 0 ? dimension : 1)
                .ToArray()
        };
        int sampleDimensionIndex = dimensions.Length - 1;
        dimensions[sampleDimensionIndex] = Math.Max(dimensions[sampleDimensionIndex], 44100);
        return dimensions;
    }

    private static int ResolvePositiveDimension(int dimension, int fallback) =>
        dimension > 0 ? dimension : fallback;

    private static InputSet CreateDiarizationInputs(InferenceSession session)
    {
        IReadOnlyDictionary<string, NodeMetadata> inputs = session.InputMetadata;
        IReadOnlyDictionary<string, Type> inputElementTypes = inputs.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value.ElementType,
            StringComparer.Ordinal);
        (string waveformName, string? lengthName) = ResolveDiarizationInputNames(inputElementTypes);

        int[] waveformDims = ResolveDiarizationWaveformShape(inputs[waveformName].Dimensions);
        var values = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(waveformName, new DenseTensor<float>(new float[16000], waveformDims))
        };

        if (!string.IsNullOrWhiteSpace(lengthName))
        {
            values.Add(NamedOnnxValue.CreateFromTensor(lengthName, new DenseTensor<long>(new long[] { 16000 }, [1])));
        }

        return new InputSet(values);
    }

    private static InputSet CreateTtsInputs(IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        foreach ((string inputName, NodeMetadata metadata) in inputMetadata)
        {
            int[] dims = metadata.Dimensions.Select(d => d > 0 ? d : 1).ToArray();
            NamedOnnxValue? value = CreateTtsInputValue(inputName, metadata.ElementType, dims);
            if (value is not null)
            {
                values.Add(value);
            }
        }

        return new InputSet(values);
    }

    private static string ResolveTtsProbeModelPath(
        string modelId,
        string modelAlias,
        string modelRootPath,
        string entryPath,
        string variant)
    {
        if (!IsChatterboxTtsModel(modelId, modelAlias))
        {
            return entryPath;
        }

        string rootPath = !string.IsNullOrWhiteSpace(modelRootPath)
            ? modelRootPath
            : Path.GetDirectoryName(entryPath)
                ?? throw new InvalidOperationException("Cannot resolve Chatterbox TTS smoke-test root path.");
        string onnxDirectory = string.Equals(Path.GetFileName(rootPath), "onnx", StringComparison.OrdinalIgnoreCase)
            ? rootPath
            : Path.Combine(rootPath, "onnx");

        if (!string.IsNullOrWhiteSpace(variant) &&
            !variant.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            string variantDecoderPath = Path.Combine(onnxDirectory, $"conditional_decoder_{variant}.onnx");
            if (File.Exists(variantDecoderPath))
            {
                return variantDecoderPath;
            }
        }

        string defaultDecoderPath = Path.Combine(onnxDirectory, "conditional_decoder.onnx");
        if (File.Exists(defaultDecoderPath))
        {
            return defaultDecoderPath;
        }

        return Path.Combine(onnxDirectory, "conditional_decoder.onnx");
    }

    private static bool IsChatterboxTtsModel(string modelId, string modelAlias) =>
        modelId.Contains("chatterbox", StringComparison.OrdinalIgnoreCase) ||
        modelAlias.Contains("chatterbox", StringComparison.OrdinalIgnoreCase);

    private static (string WaveformName, string? LengthName) ResolveDiarizationInputNames(
        IReadOnlyDictionary<string, Type> inputElementTypes)
    {
        string waveformName;
        if (inputElementTypes.ContainsKey("waveform"))
        {
            waveformName = "waveform";
        }
        else if (inputElementTypes.ContainsKey("audio_signal"))
        {
            waveformName = "audio_signal";
        }
        else
        {
            string[] floatInputs = inputElementTypes
                .Where(static candidate => candidate.Value == typeof(float))
                .Select(static candidate => candidate.Key)
                .ToArray();
            waveformName = floatInputs.Length switch
            {
                1 => floatInputs[0],
                0 => throw new InvalidOperationException("Smoke test could not locate a float diarization waveform input."),
                _ => throw new InvalidOperationException($"Smoke test found {floatInputs.Length} float diarization inputs; expected exactly one waveform input.")
            };
        }

        string? lengthName;
        if (inputElementTypes.ContainsKey("length"))
        {
            lengthName = "length";
        }
        else if (inputElementTypes.ContainsKey("audio_signal_length"))
        {
            lengthName = "audio_signal_length";
        }
        else
        {
            lengthName = inputElementTypes
                .FirstOrDefault(static candidate =>
                    candidate.Value == typeof(long) &&
                    candidate.Key.Contains("length", StringComparison.OrdinalIgnoreCase))
                .Key;
            if (string.IsNullOrWhiteSpace(lengthName))
            {
                lengthName = null;
            }
        }

        return (waveformName, lengthName);
    }

    private static int[] ResolveDiarizationWaveformShape(IReadOnlyList<int> modelDimensions) =>
        modelDimensions.Count switch
        {
            1 => [16000],
            2 => [1, 16000],
            _ => throw new InvalidOperationException("Smoke test diarization waveform input must be rank 1 or 2.")
        };

    private static NamedOnnxValue? CreateTtsInputValue(string inputName, Type elementType, int[] dims)
    {
        int elementCount = dims.Aggregate(1, static (a, b) => checked(a * b));

        if (elementType == typeof(long))
        {
            return NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<long>(new long[elementCount], dims));
        }

        if (elementType == typeof(float))
        {
            return NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(new float[elementCount], dims));
        }

        if (elementType == typeof(Half))
        {
            return NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<Half>(new Half[elementCount], dims));
        }

        if (elementType == typeof(Float16))
        {
            return NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<Float16>(new Float16[elementCount], dims));
        }

        // Resilient branch (zero-fill/skip) for unknown element types in TTS probe.
        return null;
    }

    private static async Task SmokeTestTranslationAsync(
        string entryPath,
        string modelAlias,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        string encoderModelPath = ResolveTranslationEncoderPath(entryPath);
        string decoderModelPath = ResolveOpusDecoderPath(encoderModelPath, modelAlias);
        using OnnxExecutionSessionFactory.OpusSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreateOpusAsync(encoderModelPath, decoderModelPath, provider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var encoderInputs = new InputSet([
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(new long[] { 0L }, [1, 1])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(new long[] { 1L }, [1, 1]))
        ]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults = sessionLease.EncoderSession.Run(encoderInputs.Values);
        Tensor<float> encoderHiddenStates = encoderResults
            .Single(static r => r.Name == "last_hidden_state")
            .AsTensor<float>();

        using var decoderInputs = CreateTranslationDecoderInputs(
            sessionLease.DecoderSession.InputMetadata,
            encoderHiddenStates);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.DecoderSession.Run(decoderInputs.Values);
    }

    private static void EnsureSelectedProviderMatchesRequested(
        ExecutionProviderKind requestedProvider,
        string selectedProvider)
    {
        string requestedProviderLabel = FormatProviderLabel(requestedProvider);
        if (string.Equals(selectedProvider, requestedProviderLabel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Smoke test requested provider '{requestedProviderLabel}' but session creation selected effective provider '{selectedProvider}'.");
    }

    private static string FormatProviderLabel(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.TensorRTRtx => "tensorrt-rtx",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRt => "tensorrt",
            ExecutionProviderKind.Migraphx => "migraphx",
            ExecutionProviderKind.Dnnl => "dnnl",
            ExecutionProviderKind.Qnn => "qnn",
            ExecutionProviderKind.OpenVinoCatalog => "openvino-catalog",
            ExecutionProviderKind.VitisAi => "vitisai",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported execution provider kind.")
        };

    private static InputSet CreateTranslationDecoderInputs(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        Tensor<float> encoderHiddenStates)
    {
        var attentionMask = new DenseTensor<long>(new long[] { 1L }, [1, 1]);
        var inputIds = new DenseTensor<long>(new long[] { 0L }, [1, 1]);

        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        foreach ((string inputName, NodeMetadata metadata) in inputMetadata)
        {
            values.Add(inputName switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                "encoder_hidden_states" => NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates),
                "attention_mask" => NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                "encoder_attention_mask" => NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMask),
                "use_cache_branch" => NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { false }, new[] { 1 })),
                _ when inputName.StartsWith("past_key_values.", StringComparison.Ordinal) =>
                    NamedOnnxValue.CreateFromTensor(inputName, CreateEmptyPastTensor(metadata)),
                _ => throw new NotSupportedException($"Translation decoder input '{inputName}' is not supported in smoke test.")
            });
        }

        return new InputSet(values);
    }

    private static DenseTensor<float> CreateEmptyPastTensor(NodeMetadata metadata)
    {
        int[] sourceDims = metadata.Dimensions;
        int[] dims = new int[sourceDims.Length];
        for (int i = 0; i < sourceDims.Length; i++)
        {
            dims[i] = sourceDims[i] > 0 ? sourceDims[i] : 1;
        }

        dims[0] = 1;                       // batch = 1
        if (dims.Length > 2) dims[2] = 0; // sequence = 0 (empty KV cache)
        return new DenseTensor<float>(Array.Empty<float>(), dims);
    }

    private static string ResolveTranslationEncoderPath(string entryPath)
    {
        string fileName = Path.GetFileName(entryPath);
        if (fileName.StartsWith("decoder_", StringComparison.OrdinalIgnoreCase))
        {
            string directory = Path.GetDirectoryName(entryPath)!;
            string[] candidates = ["encoder_model.onnx", "encoder_model_quantized.onnx", "encoder_model_fp16.onnx", "encoder_model_int8.onnx"];
            foreach (string candidate in candidates)
            {
                string candidatePath = Path.Combine(directory, candidate);
                if (File.Exists(candidatePath))
                {
                    return Path.GetFullPath(candidatePath);
                }
            }

            // A decoder file was given but no encoder exists alongside it. Returning the
            // decoder path as the 'encoder' would produce a confusing ONNX load error later.
            // Fail fast with a clear message naming the expected files and directory.
            throw new FileNotFoundException(
                $"Translation encoder model not found in '{directory}'. " +
                $"Expected one of: {string.Join(", ", candidates)}. " +
                "Ensure the encoder model file is present alongside the decoder model.",
                Path.Combine(directory, candidates[0]));
        }

        return entryPath;
    }

    private static string ResolveOpusDecoderPath(string encoderModelPath, string modelAlias)
    {
        // Match the decoder preference order used by the active engine family so that the
        // smoke test exercises the same files that translation will actually load at runtime.
        // MADLAD prefers int8 → default → merged; OpusMT and unknowns prefer merged → default → int8.
        bool isMadlad = modelAlias.Contains("madlad", StringComparison.OrdinalIgnoreCase);
        string[] candidates = isMadlad
            ? ["decoder_model_quantized.onnx", "decoder_model_int8.onnx", "decoder_model.onnx", "decoder_model_merged.onnx"]
            : ["decoder_model_merged.onnx", "decoder_model.onnx", "decoder_model_int8.onnx"];

        string directory = Path.GetDirectoryName(encoderModelPath)!;
        foreach (string candidate in candidates)
        {
            string candidatePath = Path.Combine(directory, candidate);
            if (File.Exists(candidatePath))
            {
                return Path.GetFullPath(candidatePath);
            }
        }

        string fallback = Path.Combine(directory, "decoder_model.onnx");
        throw new FileNotFoundException("Opus decoder model was not found next to the encoder model.", fallback);
    }

    private static string ResolveWhisperDecoderPath(string encoderModelPath)
    {
        string fileName = Path.GetFileName(encoderModelPath);
        string decoderFileName = fileName.Replace("encoder_model", "decoder_model", StringComparison.OrdinalIgnoreCase);
        string candidatePath = Path.Combine(Path.GetDirectoryName(encoderModelPath)!, decoderFileName);
        if (File.Exists(candidatePath))
        {
            return Path.GetFullPath(candidatePath);
        }

        candidatePath = Path.Combine(Path.GetDirectoryName(encoderModelPath)!, "decoder_model.onnx");
        if (File.Exists(candidatePath))
        {
            return Path.GetFullPath(candidatePath);
        }

        throw new FileNotFoundException("Whisper decoder model was not found next to the encoder model.", candidatePath);
    }

    private static string ResolveNemotronDecoderJointPath(string encoderModelPath)
    {
        string candidatePath = Path.Combine(Path.GetDirectoryName(encoderModelPath)!, "decoder_joint.onnx");
        if (File.Exists(candidatePath))
        {
            return Path.GetFullPath(candidatePath);
        }

        throw new FileNotFoundException("Nemotron ASR decoder_joint.onnx was not found next to encoder.onnx.", candidatePath);
    }

    private sealed class InputSet(IReadOnlyList<NamedOnnxValue> values) : IDisposable
    {
        public IReadOnlyList<NamedOnnxValue> Values { get; } = values;

        public void Dispose()
        {
            foreach (IDisposable value in Values.OfType<IDisposable>())
            {
                value.Dispose();
            }
        }
    }
}
