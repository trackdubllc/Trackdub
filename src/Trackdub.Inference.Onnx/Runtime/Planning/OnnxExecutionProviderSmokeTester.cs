using Microsoft.ML.OnnxRuntimeGenAI;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Inference.Onnx.Runtime;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.Qwen3Asr;
using Trackdub.Inference.Onnx.NemotronAsr;
using Trackdub.Inference.Onnx.ParakeetTdt;
using Trackdub.Inference.Onnx.SortFormer;
using Trackdub.Inference.Onnx.Whisper;
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
            // Fatal-CTP guards first: these families hard-crash the host under TRT, and the
            // refusal must happen before any bootstrap/native touch (a crash cannot be caught).
            if (request.Stage is RuntimeStage.TextRefinement
                || UsesOrtGenAiModelLoad(request.EngineFamily)
                || UsesOrtGenAiTranslationSmoke(request.EngineFamily))
            {
                ThrowIfGenAiTensorRtProvider(request.ExecutionProvider);
            }

            ThrowIfFatalTensorRtFamily(request.EngineFamily, request.ExecutionProvider);

            // Register/validate the requested EP before any probe session. When the bootstrapper
            // cannot keep the requested provider selected (e.g. TRT RTX plugin missing and
            // falling back to DirectML), the pair is unproven — fail fast with that detail
            // instead of creating a session that silently lands on a different provider.
            ExecutionProviderBootstrapResult bootstrap = await OnnxExecutionSessionFactory
                .BootstrapForSmokeAsync(request.ExecutionProvider, cancellationToken)
                .ConfigureAwait(false);
            if (!bootstrap.IsRequestFulfilled)
            {
                return new ExecutionProviderSmokeTestResult(
                    false,
                    $"Smoke bootstrap could not select {request.ExecutionProvider} "
                    + $"(selected {bootstrap.SelectedProvider}). {bootstrap.Detail}");
            }

            switch (request.Stage)
            {
                case RuntimeStage.Vad:
                    await SmokeTestVadAsync(request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.Asr:
                    await SmokeTestAsrAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.Separation:
                    await SmokeTestSeparationAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.Translation:
                    await SmokeTestTranslationAsync(request, cancellationToken).ConfigureAwait(false);
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
                        request.EntryPath,
                        request.ExecutionProvider,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeStage.SpeechEnhancement:
                case RuntimeStage.OverlapRescue:
                case RuntimeStage.LipSync:
                case RuntimeStage.LipSynthesis:
                    await SmokeTestGenericSessionAsync(request, cancellationToken)
                        .ConfigureAwait(false);
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
        string entryPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        ThrowIfGenAiTensorRtProvider(provider);

        string genAiRoot = RequireGenAiConfigRoot(
            modelRootPath,
            entryPath,
            "Text refinement smoke test requires genai_config.json in the model root.");

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Model model = CreateGenAiSmokeModel(genAiRoot, provider);
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
        config.AppendProvider(GenAiExecutionProviderNames.Resolve(provider));
        return new Model(config);
    }

    private static async Task SmokeTestVadAsync(
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        // Pooled with the same engine family SileroVadSpeechRegionDetector uses, so the
        // smoke run leaves a warm session for the VAD stage instead of a throwaway one.
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledSingleAsync("silero-vad", modelPath, provider, cancellationToken, allowTrtInitFallback: false)
            .ConfigureAwait(false);
        try
        {
            EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"{ex.Message} Session bootstrap: {sessionLease.BootstrapDetail}",
                ex);
        }

        using var input = CreateVadInputs();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.Session.Run(input.Values);
    }

    private static async Task SmokeTestWhisperAsync(
        string encoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        string decoderModelPath = ResolveWhisperDecoderPath(encoderModelPath);
        string? onnxDirectory = Path.GetDirectoryName(encoderModelPath);
        string modelRootPath = Path.GetDirectoryName(onnxDirectory ?? string.Empty)
            ?? throw new InvalidOperationException("Whisper smoke test could not resolve model root.");
        // Pooled with the same family/TRT profiles WhisperOnnxAudioTranscriptionEngine uses.
        using OnnxExecutionSessionFactory.WhisperSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledWhisperAsync(
                WhisperOnnxAudioTranscriptionEngine.EngineFamilyName,
                encoderModelPath,
                decoderModelPath,
                provider,
                cancellationToken,
                additionalTrtEncoderOptions: WhisperOnnxAudioTranscriptionEngine.BuildTrtEncoderOptions(
                    WhisperOnnxAudioTranscriptionEngine.ReadMelBins(modelRootPath)))
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var encoderInputs = CreateWhisperEncoderInputs(sessionLease.EncoderSession.InputMetadata);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults = sessionLease.EncoderSession.Run(encoderInputs.Values);
        Tensor<float> hiddenStates = ResolveWhisperEncoderHiddenStates(encoderResults);

        using var decoderInputs = CreateWhisperDecoderInputs(hiddenStates);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.DecoderSession.Run(decoderInputs.Values);
    }

    private static Tensor<float> ResolveWhisperEncoderHiddenStates(
        IEnumerable<DisposableNamedOnnxValue> encoderResults)
    {
        DisposableNamedOnnxValue[] materialised = encoderResults as DisposableNamedOnnxValue[]
            ?? encoderResults.ToArray();
        foreach (DisposableNamedOnnxValue result in materialised)
        {
            if (result.Name.Contains("hidden", StringComparison.OrdinalIgnoreCase)
                || result.Name.Equals("last_hidden_state", StringComparison.OrdinalIgnoreCase))
            {
                return result.AsTensor<float>();
            }
        }

        string available = string.Join(", ", materialised.Select(static result => result.Name));
        throw new InvalidOperationException(
            "Whisper encoder smoke test did not produce a hidden-state output. "
            + $"Available outputs: {(string.IsNullOrWhiteSpace(available) ? "(none)" : available)}.");
    }

    private static async Task SmokeTestAsrAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken)
    {
        string engineFamily = request.EngineFamily?.Trim() ?? string.Empty;
        if (engineFamily.Equals("qwen3-asr", StringComparison.OrdinalIgnoreCase))
        {
            // Pool key: production Qwen3 omits modelId/variant; keep smoke identical so the
            // session proven here is the one the ASR stage reuses.
            await SmokeTestQwen3AsrAsync(request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (engineFamily.Equals("nemotron-asr", StringComparison.OrdinalIgnoreCase))
        {
            await SmokeTestNemotronAsrAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (engineFamily.Equals(ParakeetTdtOnnxAudioTranscriptionEngine.EngineFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            await SmokeTestParakeetTdtAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (engineFamily.Equals("whisper-onnx", StringComparison.OrdinalIgnoreCase))
        {
            await SmokeTestWhisperAsync(request.EntryPath, request.ExecutionProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (UsesOrtGenAiModelLoad(engineFamily))
        {
            await SmokeTestGenAiLoadAsync(
                    request.ModelRootPath,
                    request.EntryPath,
                    request.ExecutionProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await SmokeTestGenericSessionAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static bool UsesOrtGenAiModelLoad(string? engineFamily) =>
        engineFamily is not null
        && engineFamily.Equals("whisper-genai", StringComparison.OrdinalIgnoreCase);

    internal static bool UsesOrtGenAiTranslationSmoke(string? engineFamily) =>
        engineFamily is not null
        && (engineFamily.Equals("phi-genai", StringComparison.OrdinalIgnoreCase)
            || engineFamily.Equals("qwen-instruct", StringComparison.OrdinalIgnoreCase));

    private static async Task SmokeTestGenAiLoadAsync(
        string modelRootPath,
        string entryPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        ThrowIfGenAiTensorRtProvider(provider);

        string genAiRoot = RequireGenAiConfigRoot(
            modelRootPath,
            entryPath,
            "GenAI smoke test requires genai_config.json in the model root.");

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (CreateGenAiSmokeModel(genAiRoot, provider))
            {
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    // ORT GenAI's NvTensorRtRtx device terminates the host process (native stack overflow) on
    // bundled GenAI models such as qwen-instruct (Qwen2.5-1.5B). A fatal crash cannot be caught
    // and reported as a smoke failure, so the attempt must be refused before touching native code.
    private static void ThrowIfGenAiTensorRtProvider(ExecutionProviderKind provider)
    {
        if (provider is ExecutionProviderKind.TensorRTRtx or ExecutionProviderKind.TensorRt)
        {
            throw new NotSupportedException(
                "ORT GenAI NvTensorRtRtx is excluded for GenAI model loads: it terminates the host "
                + "process (native stack overflow) on bundled GenAI models. Use dml or cpu.");
        }
    }

    // Encoder-decoder InferenceSession construction for these families terminates the host
    // process (stack overflow) under TensorRT providers; the reason their stage allow-list
    // overrides exist. The smoke sweep bypasses stage allow-lists, so refuse the attempt
    // before session creation; a fatal crash cannot be caught and reported.
    private static void ThrowIfFatalTensorRtFamily(string? engineFamily, ExecutionProviderKind provider)
    {
        if (provider is ExecutionProviderKind.TensorRTRtx or ExecutionProviderKind.TensorRt
            && engineFamily is not null
            && (engineFamily.Equals("opus-mt", StringComparison.OrdinalIgnoreCase)
                || engineFamily.Equals("madlad", StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException(
                $"Engine family '{engineFamily}' is excluded from TensorRT providers: "
                + "encoder-decoder InferenceSession construction terminates the host process "
                + "(native stack overflow). Use dml or cpu.");
        }
    }

    private static string RequireGenAiConfigRoot(
        string modelRootPath,
        string entryPath,
        string missingConfigMessage)
    {
        string genAiRoot = PlannedRuntimeModelResolver.ResolveGenAiModelRoot(modelRootPath, entryPath);
        string configPath = Path.Join(genAiRoot, "genai_config.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"{missingConfigMessage} Missing: {configPath}", configPath);
        }

        return genAiRoot;
    }

    private static async Task SmokeTestGenericSessionAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken)
    {
        string poolFamily = ResolveGenericPoolFamily(request);
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledSingleAsync(
                poolFamily,
                request.EntryPath,
                request.ExecutionProvider,
                cancellationToken,
                allowTrtInitFallback: false)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(request.ExecutionProvider, sessionLease.SelectedProvider);

        using var inputs = CreateMetadataDrivenInputs(sessionLease.Session.InputMetadata);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.Session.Run(inputs.Values);
    }

    /// <summary>
    /// Resolves the pool family for generic single-graph smoke targets so the key matches
    /// the production engine's CreatePooledSingleAsync family (DeepFilterNet, SepFormer, LatentSync).
    /// </summary>
    private static string ResolveGenericPoolFamily(ExecutionProviderSmokeTestRequest request)
    {
        string name = $"{request.EngineFamily} {request.ModelId} {Path.GetFileNameWithoutExtension(request.EntryPath)}";
        if (name.Contains("deepfilternet", StringComparison.OrdinalIgnoreCase) ||
            request.Stage is RuntimeStage.SpeechEnhancement)
        {
            if (name.Contains("erb", StringComparison.OrdinalIgnoreCase))
            {
                return "deepfilternet3-erb-dec";
            }

            if (name.Contains("df_dec", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("df-dec", StringComparison.OrdinalIgnoreCase))
            {
                return "deepfilternet3-df-dec";
            }

            if (name.Contains("enc", StringComparison.OrdinalIgnoreCase))
            {
                return "deepfilternet3-enc";
            }
        }

        if (request.Stage is RuntimeStage.OverlapRescue)
        {
            return name.Contains("osd", StringComparison.OrdinalIgnoreCase)
                ? "sepformer-osd"
                : "sepformer";
        }

        if (request.Stage is RuntimeStage.LipSync or RuntimeStage.LipSynthesis)
        {
            return string.IsNullOrWhiteSpace(request.EngineFamily) ? "latentsync" : request.EngineFamily;
        }

        return string.IsNullOrWhiteSpace(request.EngineFamily) ? request.Stage.ToString().ToLowerInvariant() : request.EngineFamily;
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
            .CreatePooledQwen3AsrAsync(
                "qwen3-asr",
                encoderModelPath,
                decoderInitPath,
                decoderStepPath,
                provider,
                cancellationToken,
                additionalTrtEncoderOptions: Qwen3AsrOnnxAudioTranscriptionEngine.TrtEncoderOptions)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var encoderInputs = new InputSet([
            NamedOnnxValue.CreateFromTensor("mel", new DenseTensor<float>(new float[128], [1, 128, 1]))
        ]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults =
            sessionLease.EncoderSession.Run(encoderInputs.Values);
        Tensor<float> audioFeatures = encoderResults.First().AsTensor<float>();
        Qwen3AsrGreedyDecoder.RunSmokeInitAndStep(sessionLease, audioFeatures);
    }

    private static async Task SmokeTestNemotronAsrAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken)
    {
        string encoderModelPath = request.EntryPath;
        string decoderJointPath = ResolveNemotronDecoderJointPath(encoderModelPath);
        // modelId/variant must match NemotronAsrOnnxAudioTranscriptionEngine or the stage
        // misses this pooled session and pays cold load again.
        using OnnxExecutionSessionFactory.NemotronAsrSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledNemotronAsrAsync(
                "nemotron-asr",
                encoderModelPath,
                decoderJointPath,
                request.ExecutionProvider,
                cancellationToken,
                modelId: request.ModelId,
                variant: request.Variant,
                additionalTrtEncoderOptions: NemotronAsrEncoderTrtProfiles.BuildOptions(encoderModelPath))
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(request.ExecutionProvider, sessionLease.SelectedProvider);

        using var encoderInputs = CreateNemotronEncoderInputs(sessionLease.EncoderSession.InputMetadata);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults =
            sessionLease.EncoderSession.Run(encoderInputs.Values);
        Tensor<float> encoded = encoderResults.Single(static result => result.Name == "encoded").AsTensor<float>();
        using var decoderInputs = CreateNemotronDecoderInputs(sessionLease.DecoderJointSession.InputMetadata, encoded);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.DecoderJointSession.Run(decoderInputs.Values);
    }

    private static async Task SmokeTestParakeetTdtAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken)
    {
        string encoderModelPath = request.EntryPath;
        string root = Path.GetDirectoryName(encoderModelPath)
            ?? throw new InvalidOperationException("Parakeet-TDT smoke test could not resolve model root.");
        // Preprocessor is a separate pooled CPU graph in production; warm the same key so the
        // stage does not rebuild nemo128.onnx after a successful smoke.
        using OnnxExecutionSessionFactory.SingleSessionLease preprocessor = await OnnxExecutionSessionFactory
            .CreatePooledSingleAsync(
                "parakeet-tdt-preprocessor",
                Path.Combine(root, "nemo128.onnx"),
                ExecutionProviderKind.Cpu,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(ExecutionProviderKind.Cpu, preprocessor.SelectedProvider);

        using OnnxExecutionSessionFactory.NemotronAsrSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledNemotronAsrAsync(
                ParakeetTdtOnnxAudioTranscriptionEngine.EngineFamilyName,
                encoderModelPath,
                Path.Combine(root, "decoder_joint-model.onnx"),
                request.ExecutionProvider,
                cancellationToken,
                modelId: request.ModelId,
                variant: request.Variant,
                additionalTrtEncoderOptions: ParakeetTdtOnnxAudioTranscriptionEngine.TrtEncoderOptions)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(request.ExecutionProvider, sessionLease.SelectedProvider);

        const int melFrames = 100;
        using var encoderInputs = new InputSet([
            NamedOnnxValue.CreateFromTensor("audio_signal", new DenseTensor<float>(new float[128 * melFrames], [1, 128, melFrames])),
            NamedOnnxValue.CreateFromTensor("length", new DenseTensor<long>(new long[] { melFrames }, [1])),
        ]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults =
            sessionLease.EncoderSession.Run(encoderInputs.Values);
        Tensor<float> encoded = encoderResults.Single(static result => result.Name == "outputs").AsTensor<float>();
        using var decoderInputs = CreateNemotronDecoderInputs(sessionLease.DecoderJointSession.InputMetadata, encoded);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.DecoderJointSession.Run(decoderInputs.Values);
    }

    private static async Task SmokeTestSeparationAsync(
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken)
    {
        string runnableModelPath = request.EntryPath;
        string poolFamily = ResolveSeparationPoolFamily(request.EngineFamily, request.ModelId, runnableModelPath);
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledSingleAsync(poolFamily, runnableModelPath, request.ExecutionProvider, cancellationToken, allowTrtInitFallback: false)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(request.ExecutionProvider, sessionLease.SelectedProvider);

        using var inputs = CreateSeparationInputs(sessionLease.Session);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.Session.Run(inputs.Values);
    }

    /// <summary>
    /// Maps a separation smoke target onto the pool family strings used by the production
    /// separators (Spleeter/SepFormer) so smoke and stage runs share one warm session.
    /// </summary>
    private static string ResolveSeparationPoolFamily(string? engineFamily, string modelId, string modelPath)
    {
        string name = $"{modelId} {Path.GetFileNameWithoutExtension(modelPath)} {engineFamily}";
        if (name.Contains("vocals", StringComparison.OrdinalIgnoreCase))
        {
            return "spleeter-vocals";
        }

        if (name.Contains("accompaniment", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("spleeter-acc", StringComparison.OrdinalIgnoreCase) ||
            (name.Contains("acc", StringComparison.OrdinalIgnoreCase) && name.Contains("spleeter", StringComparison.OrdinalIgnoreCase)))
        {
            return "spleeter-acc";
        }

        if (name.Contains("osd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("overlap", StringComparison.OrdinalIgnoreCase))
        {
            return "sepformer-osd";
        }

        if (name.Contains("sepformer", StringComparison.OrdinalIgnoreCase))
        {
            return "sepformer";
        }

        return string.IsNullOrWhiteSpace(engineFamily) ? "separation" : engineFamily;
    }

    private static async Task SmokeTestDiarizationAsync(
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        // Same family + TRT profiles as SortFormerDiarizationEngine so the smoke run warms
        // the session the diarization stage will lease.
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledSingleAsync(
                SortFormerDiarizationEngine.EngineFamilyName,
                modelPath,
                provider,
                cancellationToken,
                additionalTrtOptions: SortFormerDiarizationEngine.TrtOptions,
                allowTrtInitFallback: false)
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
        string poolFamily = ResolveTtsPoolFamily(modelId, modelAlias);
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledSingleAsync(poolFamily, modelPath, provider, cancellationToken, allowTrtInitFallback: false)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(provider, sessionLease.SelectedProvider);

        using var inputs = CreateTtsInputs(sessionLease.Session.InputMetadata);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _ = sessionLease.Session.Run(inputs.Values);

        if (IsChatterboxTtsModel(modelId, modelAlias))
        {
            await WarmChatterboxSidecarsAsync(modelRootPath, entryPath, variant, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveTtsPoolFamily(string modelId, string modelAlias)
    {
        if (IsChatterboxTtsModel(modelId, modelAlias))
        {
            return "chatterbox";
        }

        if (modelId.Contains("cosyvoice", StringComparison.OrdinalIgnoreCase) ||
            modelAlias.Contains("cosyvoice", StringComparison.OrdinalIgnoreCase))
        {
            return "cosyvoice";
        }

        return "kokoro";
    }

    /// <summary>
    /// Pre-warms the three Chatterbox CPU sidecar graphs so the TTS stage does not pay
    /// their cold load after the smoke test already warmed the conditional decoder.
    /// Provider selection mirrors ChatterboxVoiceCloneTtsEngine (sidecars on CPU).
    /// </summary>
    private static async Task WarmChatterboxSidecarsAsync(
        string modelRootPath,
        string entryPath,
        string variant,
        CancellationToken cancellationToken)
    {
        string rootPath = !string.IsNullOrWhiteSpace(modelRootPath)
            ? modelRootPath
            : Path.GetDirectoryName(entryPath)
                ?? throw new InvalidOperationException("Cannot resolve Chatterbox TTS warmup root path.");
        string onnxDirectory = string.Equals(Path.GetFileName(rootPath), "onnx", StringComparison.OrdinalIgnoreCase)
            ? rootPath
            : Path.Combine(rootPath, "onnx");

        foreach (string graphName in new[] { "speech_encoder", "embed_tokens", "language_model" })
        {
            string graphPath = ResolveChatterboxGraphPath(onnxDirectory, graphName, variant);
            if (!File.Exists(graphPath))
            {
                continue;
            }

            await OnnxExecutionSessionFactory.WarmPooledSingleAsync(
                "chatterbox",
                graphPath,
                ExecutionProviderKind.Cpu,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveChatterboxGraphPath(string onnxDirectory, string graphName, string? variant)
    {
        if (!string.IsNullOrWhiteSpace(variant) &&
            !variant.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            string variantPath = Path.Combine(onnxDirectory, $"{graphName}_{variant}.onnx");
            if (File.Exists(variantPath))
            {
                return variantPath;
            }
        }

        return Path.Combine(onnxDirectory, $"{graphName}.onnx");
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

    private static InputSet CreateWhisperEncoderInputs(IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        foreach ((string inputName, NodeMetadata metadata) in inputMetadata)
        {
            values.Add(CreateSmokeTensorInput(inputName, metadata));
        }

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
        // Bundled Nemotron export expects mel-major [B,mel,T], same layout the engine feeds.
        var values = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("processed_signal", new DenseTensor<float>(new float[128 * 65], [1, 128, 65])),
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
        // The encoder output may be time-major [B,T,H] or hidden-major [B,H,T] depending on
        // the provider's graph; resolve the layout the same way the engine does. The decoder's
        // encoder_outputs metadata declares hidden at index 1 ([batch, hidden, time]).
        int hiddenDim = inputMetadata.TryGetValue("encoder_outputs", out NodeMetadata? encoderOutputsMetadata)
            && encoderOutputsMetadata.Dimensions.Length == 3
            && encoderOutputsMetadata.Dimensions[1] > 0
                ? encoderOutputsMetadata.Dimensions[1]
                : 1024;
        NemotronAsrEncodedTensorLayout.EncodedLayout layout = NemotronAsrEncodedTensorLayout.Resolve(
            encoded.Dimensions,
            encodedLength: int.MaxValue,
            hiddenDim);
        DenseTensor<float> frame = NemotronAsrEncodedTensorLayout.SliceFrame(encoded, layout, frameIndex: 0);
        int[] stateDims = ResolveMetadataDims(inputMetadata, "input_states_1", [2, 1, 640]);
        int stateCount = stateDims.Aggregate(1, static (product, dimension) => checked(product * dimension));

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
        if (TryCreateWaveformDiarizationInputs(inputs, out InputSet? waveformInputs))
        {
            return waveformInputs!;
        }

        return CreateMetadataDrivenInputs(inputs);
    }

    private static bool TryCreateWaveformDiarizationInputs(
        IReadOnlyDictionary<string, NodeMetadata> inputs,
        out InputSet? inputSet)
    {
        inputSet = null;
        IReadOnlyDictionary<string, Type> inputElementTypes = inputs.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value.ElementType,
            StringComparer.Ordinal);

        try
        {
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

            inputSet = new InputSet(values);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static InputSet CreateMetadataDrivenInputs(IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        foreach ((string inputName, NodeMetadata metadata) in inputMetadata)
        {
            values.Add(CreateSmokeTensorInput(inputName, metadata));
        }

        return new InputSet(values);
    }

    private static NamedOnnxValue CreateSmokeTensorInput(string inputName, NodeMetadata metadata)
    {
        if (!metadata.IsTensor)
        {
            throw new NotSupportedException($"Smoke test input '{inputName}' is not a tensor input.");
        }

        int[] dimensions = metadata.Dimensions.Select(static dimension => dimension > 0 ? dimension : 1).ToArray();
        int elementCount = dimensions.Aggregate(1, static (product, dimension) => checked(product * dimension));

        return metadata.ElementDataType switch
        {
            TensorElementType.Float => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<float>(new float[elementCount], dimensions)),
            TensorElementType.Float16 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<Float16>(new Float16[elementCount], dimensions)),
            TensorElementType.Int32 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<int>(new int[elementCount], dimensions)),
            TensorElementType.Int64 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<long>(new long[elementCount], dimensions)),
            TensorElementType.Bool => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<bool>(new bool[elementCount], dimensions)),
            _ => throw new NotSupportedException(
                $"Smoke test input '{inputName}' uses unsupported tensor element type '{metadata.ElementDataType}'.")
        };
    }

    private static InputSet CreateTtsInputs(IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        if (LooksLikeChatterboxConditionalDecoder(inputMetadata))
        {
            return CreateChatterboxDecoderInputs(inputMetadata);
        }

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

    private static bool LooksLikeChatterboxConditionalDecoder(IReadOnlyDictionary<string, NodeMetadata> inputMetadata) =>
        inputMetadata.ContainsKey("speech_tokens")
        && inputMetadata.ContainsKey("speaker_embeddings")
        && inputMetadata.ContainsKey("speaker_features");

    // ISTFT in conditional_decoder uses n_fft=960 / hop=480. One speech token yields 480
    // samples and fails overlap-add (480 by 960). Keep a short but valid token sequence.
    private const int ChatterboxDecoderSmokeSpeechTokenCount = 8;
    private const int ChatterboxDecoderSmokeFeatureFrames = 8;

    private static InputSet CreateChatterboxDecoderInputs(IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        foreach ((string inputName, NodeMetadata metadata) in inputMetadata)
        {
            int[] dims = ResolveChatterboxDecoderSmokeDimensions(inputName, metadata.Dimensions);
            NamedOnnxValue? value = CreateTtsInputValue(inputName, metadata.ElementType, dims);
            if (value is not null)
            {
                values.Add(value);
            }
        }

        return new InputSet(values);
    }

    internal static int[] ResolveChatterboxDecoderSmokeDimensionsForTesting(string inputName, IReadOnlyList<int> modelDimensions) =>
        ResolveChatterboxDecoderSmokeDimensions(inputName, modelDimensions);

    private static int[] ResolveChatterboxDecoderSmokeDimensions(string inputName, IReadOnlyList<int> modelDimensions)
    {
        if (inputName.Equals("speech_tokens", StringComparison.Ordinal)
            && modelDimensions.Count == 2)
        {
            return
            [
                modelDimensions[0] > 0 ? modelDimensions[0] : 1,
                ChatterboxDecoderSmokeSpeechTokenCount
            ];
        }

        if (inputName.Equals("speaker_features", StringComparison.Ordinal)
            && modelDimensions.Count == 3)
        {
            return
            [
                modelDimensions[0] > 0 ? modelDimensions[0] : 1,
                modelDimensions[1] > 0 ? modelDimensions[1] : ChatterboxDecoderSmokeFeatureFrames,
                modelDimensions[2] > 0 ? modelDimensions[2] : 80
            ];
        }

        return modelDimensions.Select(static dimension => dimension > 0 ? dimension : 1).ToArray();
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
        ExecutionProviderSmokeTestRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfFatalTensorRtFamily(request.EngineFamily, request.ExecutionProvider);

        if (UsesOrtGenAiTranslationSmoke(request.EngineFamily))
        {
            await SmokeTestTextRefinementGenAiAsync(
                request.ModelRootPath,
                request.EntryPath,
                request.ExecutionProvider,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string encoderModelPath = ResolveTranslationEncoderPath(request.EntryPath);
        string decoderModelPath = ResolveOpusDecoderPath(encoderModelPath, request.ModelAlias);
        using OnnxExecutionSessionFactory.OpusSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreateOpusAsync(encoderModelPath, decoderModelPath, request.ExecutionProvider, cancellationToken)
            .ConfigureAwait(false);
        EnsureSelectedProviderMatchesRequested(request.ExecutionProvider, sessionLease.SelectedProvider);

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
