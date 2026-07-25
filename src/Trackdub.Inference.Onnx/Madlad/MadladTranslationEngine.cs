using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Onnx.Translation;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Madlad;

public sealed class MadladTranslationEngine(IRuntimePlanner runtimePlanner,
    BenchmarkModelPathResolver modelPathResolver,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : ITranslationEngineAdapter, IStageRuntimeExecutionReporter
{
    public const string EngineFamilyName = "madlad";

    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly BenchmarkModelPathResolver modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public string EngineFamily => EngineFamilyName;

    public async Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Segments);

        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.Translation,
                PreferredModelAlias: request.PreferredModelAlias,
                SourceLanguage: request.SourceLanguage,
                TargetLanguage: request.TargetLanguage,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: request.PreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);
        EnsurePlanReady(plan, RuntimeStage.Translation);

        string encoderModelPath = ResolveEncoderModelPath(plan, request.ResolvedModelEntryPath);
        string decoderModelPath = ResolveDecoderModelPath(plan, encoderModelPath);
        string modelRootPath = ResolveModelRootPath(encoderModelPath);
        MadladTokenizerDecoder tokenizer = await MadladTokenizerDecoder.LoadAsync(modelRootPath).ConfigureAwait(false);
        string targetLanguageTag = ResolveTargetLanguageTag(request.TargetLanguage);

        if (request.Segments.Count == 0)
        {
            LastExecutionSummary = CreatePlannedOnlySummary(plan, "Translation skipped because the transcript did not contain any segments.");
            return [];
        }

        using OnnxExecutionSessionFactory.OpusSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledOpusAsync("madlad", encoderModelPath, decoderModelPath, plan.ExecutionProvider!.Value, cancellationToken)
            .ConfigureAwait(false);

        var translatedSegments = new List<TranslatedTextSegment>(request.Segments.Count);
        foreach (TranslationInputSegment segment in request.Segments.OrderBy(static segment => segment.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string translatedText = await TranslateSegmentAsync(
                sessionLease,
                tokenizer,
                targetLanguageTag,
                segment.Text,
                cancellationToken).ConfigureAwait(false);

            translatedSegments.Add(new TranslatedTextSegment(
                segment.Index,
                segment.StartSeconds,
                segment.EndSeconds,
                translatedText));
        }

        LastExecutionSummary = CreateExecutionSummary(plan, sessionLease);
        return translatedSegments;
    }

    private static async Task<string> TranslateSegmentAsync(
        OnnxExecutionSessionFactory.OpusSessionLease sessionLease,
        MadladTokenizerDecoder tokenizer,
        string targetLanguageTag,
        string text,
        CancellationToken cancellationToken)
    {
        long[] inputIds = tokenizer.EncodeSourceText(text, targetLanguageTag);
        long[] attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();

        using var encoderInputs = CreateEncoderInputs(
            sessionLease.EncoderSession.InputMetadata,
            inputIds,
            attentionMask);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults =
            sessionLease.EncoderSession.RunWithRetry(encoderInputs.Values);
        Tensor<float> encoderHiddenStates = encoderResults
            .Single(static result => result.Name == "last_hidden_state")
            .AsTensor<float>();

        List<long> generatedTokens = await GreedyDecodeAsync(
            sessionLease.DecoderSession,
            tokenizer,
            encoderHiddenStates,
            attentionMask,
            cancellationToken).ConfigureAwait(false);
        string translatedText = tokenizer.DecodeTargetText(generatedTokens);

        return string.IsNullOrWhiteSpace(translatedText)
            ? string.Empty
            : translatedText;
    }

    private static Task<List<long>> GreedyDecodeAsync(
        InferenceSession decoderSession,
        MadladTokenizerDecoder tokenizer,
        Tensor<float> encoderHiddenStates,
        IReadOnlyList<long> attentionMask,
        CancellationToken cancellationToken)
    {
        var generatedTokens = new List<long> { tokenizer.DecoderStartTokenId };
        int maxSteps = Math.Max(8, tokenizer.MaxGenerationLength);

        for (int step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var decoderInputs = CreateDecoderInputs(
                decoderSession.InputMetadata,
                encoderHiddenStates,
                attentionMask,
                generatedTokens);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoderResults = decoderSession.RunWithRetry(decoderInputs.Values);
            Tensor<float> logits = decoderResults
                .Single(static result => result.Name == "logits")
                .AsTensor<float>();

            int sequenceLength = logits.Dimensions[1];
            int vocabularySize = logits.Dimensions[2];
            int nextToken = SelectNextToken(logits, sequenceLength - 1, vocabularySize, tokenizer.PadTokenId);
            if (nextToken == tokenizer.EndOfSentenceTokenId || nextToken < 0)
            {
                break;
            }

            generatedTokens.Add(nextToken);
        }

        return Task.FromResult(generatedTokens.Skip(1).ToList());
    }

    private static int SelectNextToken(
        Tensor<float> logits,
        int timeIndex,
        int vocabularySize,
        int padTokenId)
    {
        int bestToken = -1;
        float bestValue = float.NegativeInfinity;
        for (int tokenIndex = 0; tokenIndex < vocabularySize; tokenIndex++)
        {
            if (tokenIndex == padTokenId)
            {
                continue;
            }

            float value = logits[0, timeIndex, tokenIndex];
            if (value > bestValue)
            {
                bestValue = value;
                bestToken = tokenIndex;
            }
        }

        return bestToken;
    }

    private static InputSet CreateEncoderInputs(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        IReadOnlyList<long> inputIds,
        IReadOnlyList<long> attentionMask)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        foreach ((string inputName, _) in inputMetadata)
        {
            values.Add(inputName switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(
                    "input_ids",
                    new DenseTensor<long>(inputIds.ToArray(), [1, inputIds.Count])),
                "attention_mask" => NamedOnnxValue.CreateFromTensor(
                    "attention_mask",
                    new DenseTensor<long>(attentionMask.ToArray(), [1, attentionMask.Count])),
                _ => throw new NotSupportedException($"MADLAD encoder input '{inputName}' is not supported.")
            });
        }

        return new InputSet(values);
    }

    private static InputSet CreateDecoderInputs(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        Tensor<float> encoderHiddenStates,
        IReadOnlyList<long> attentionMask,
        IReadOnlyList<long> generatedTokens)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        DenseTensor<long> inputIdsTensor = new(generatedTokens.ToArray(), [1, generatedTokens.Count]);
        DenseTensor<long> attentionMaskTensor = new(attentionMask.ToArray(), [1, attentionMask.Count]);

        foreach ((string inputName, _) in inputMetadata)
        {
            values.Add(inputName switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                "encoder_hidden_states" => NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates),
                "attention_mask" => NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
                "encoder_attention_mask" => NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMaskTensor),
                "use_cache_branch" => NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { false }, new[] { 1 })),
                _ when inputName.StartsWith("past_key_values.", StringComparison.Ordinal) =>
                    NamedOnnxValue.CreateFromTensor(inputName, CreateEmptyPastTensor(inputMetadata[inputName])),
                _ => throw new NotSupportedException($"MADLAD decoder input '{inputName}' is not supported.")
            });
        }

        return new InputSet(values);
    }

    private static DenseTensor<float> CreateEmptyPastTensor(NodeMetadata metadata)
    {
        int[] sourceDims = metadata.Dimensions;
        int[] dims = new int[sourceDims.Length];
        for (int i = 0; i < sourceDims.Length; i++)
            dims[i] = sourceDims[i] > 0 ? sourceDims[i] : 1;
        dims[0] = 1;                       // batch = 1
        if (dims.Length > 2) dims[2] = 0; // sequence = 0 (empty cache)
        return new(Array.Empty<float>(), dims);
    }

    private static void EnsurePlanReady(StageRuntimePlan plan, RuntimeStage stage)
    {
        if (plan.IsRunnable() &&
            plan.ExecutionProvider is not null &&
            !string.IsNullOrWhiteSpace(plan.ModelAlias))
        {
            return;
        }

        throw new InvalidOperationException(
            plan.Fallback?.Detail ??
            $"Runtime planner did not produce a ready {stage} plan.");
    }

    private string ResolveEncoderModelPath(StageRuntimePlan plan, string? resolvedModelEntryPath)
    {
        if (!string.IsNullOrWhiteSpace(plan.ModelEntryPath))
        {
            return Path.GetFullPath(plan.ModelEntryPath);
        }

        if (!string.IsNullOrWhiteSpace(resolvedModelEntryPath))
        {
            return Path.GetFullPath(resolvedModelEntryPath);
        }

        if (!string.IsNullOrWhiteSpace(plan.Variant))
        {
            BenchmarkModelCandidate variantCandidate = modelPathResolver.ResolveSingle(plan.ModelAlias!, plan.Variant);
            string fileName = Path.GetFileName(variantCandidate.ModelPath);
            if (fileName.StartsWith("encoder_model", StringComparison.OrdinalIgnoreCase))
            {
                return variantCandidate.ModelPath;
            }
        }

        BenchmarkModelCandidate candidate = modelPathResolver.ResolveSingle(plan.ModelAlias!);
        return candidate.ModelPath;
    }

    private string ResolveDecoderModelPath(StageRuntimePlan plan, string encoderModelPath)
    {
        string modelRootPath = ResolveModelRootPath(encoderModelPath);
        foreach (string fileName in new[] { "decoder_model_quantized.onnx", "decoder_model_int8.onnx", "decoder_model.onnx", "decoder_model_merged.onnx" })
        {
            string candidatePath = Path.Combine(modelRootPath, fileName);
            if (File.Exists(candidatePath))
            {
                return Path.GetFullPath(candidatePath);
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.Variant))
        {
            BenchmarkModelCandidate candidate = modelPathResolver.ResolveSingle(plan.ModelAlias!, plan.Variant);
            string fileName = Path.GetFileName(candidate.ModelPath);
            if (fileName.StartsWith("decoder_model", StringComparison.OrdinalIgnoreCase))
            {
                return candidate.ModelPath;
            }
        }

        foreach (string fileName in new[] { "decoder_model_quantized.onnx", "decoder_model_int8.onnx", "decoder_model.onnx", "decoder_model_merged.onnx" })
        {
            string candidatePath = Path.Combine(modelRootPath, fileName);
            if (File.Exists(candidatePath))
            {
                return Path.GetFullPath(candidatePath);
            }
        }

        throw new FileNotFoundException("The MADLAD decoder model was not found next to the encoder model.", encoderModelPath);
    }

    private static string ResolveModelRootPath(string encoderModelPath)
    {
        string? onnxDirectory = Path.GetDirectoryName(encoderModelPath);
        return onnxDirectory is null
            ? throw new InvalidOperationException("The MADLAD model root path could not be resolved.")
            : onnxDirectory;
    }

    private static string ResolveTargetLanguageTag(string targetLanguage)
    {
        if (!TranslationLanguageCoverageMatrix.TryGetLanguage(targetLanguage, out TranslationLanguageDefinition? definition))
        {
            throw new InvalidOperationException($"MADLAD does not know the target language tag for '{targetLanguage}'.");
        }

        return $"<2{definition!.MadladTag}>";
    }

    private static StageRuntimeExecutionSummary CreateExecutionSummary(
        StageRuntimePlan plan,
        OnnxExecutionSessionFactory.OpusSessionLease sessionLease) =>
        new(
            sessionLease.RequestedProvider,
            sessionLease.SelectedProvider,
            plan.ModelId,
            plan.ModelAlias,
            plan.Variant,
            sessionLease.BootstrapDetail);

    private static StageRuntimeExecutionSummary CreatePlannedOnlySummary(
        StageRuntimePlan plan,
        string bootstrapDetail) =>
        new(
            "auto",
            plan.ExecutionProvider is ExecutionProviderKind.DirectMl ? "dml" : "cpu",
            plan.ModelId,
            plan.ModelAlias,
            plan.Variant,
            bootstrapDetail);

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
