using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Whisper;

public sealed class WhisperOnnxAudioTranscriptionEngine(IRuntimePlanner runtimePlanner,
    BenchmarkModelPathResolver modelPathResolver,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IPipelineDeviceExclusionProvider? deviceExclusionProvider = null)
    : IAudioTranscriptionEngineAdapter, IStageRuntimeExecutionReporter, IDeviceDegradationReporter
{
    public const string EngineFamilyName = "whisper-onnx";

    private static readonly IReadOnlyDictionary<string, string> TrtEncoderOptions = new Dictionary<string, string>
    {
        ["trt_profile_min_shapes"] = "input_features:1x80x1",
        ["trt_profile_max_shapes"] = "input_features:1x80x3000",
        ["trt_profile_opt_shapes"] = "input_features:1x80x3000"
    };

    private const double MaxChunkDurationSeconds = 28;
    private const double TranscriptionMergeGapSeconds = 1.5;
    private const double TranscriptionContextPaddingSeconds = 0.5;
    private const double MinTranscriptionRegionSeconds = 3.0;
    private const string RepetitionGuardBootstrapDetail = "Decoder repetition guard flagged output for review.";
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly BenchmarkModelPathResolver modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));
    private readonly IPipelineDeviceExclusionProvider? deviceExclusionProvider = deviceExclusionProvider;
    private readonly WhisperFeatureExtractor featureExtractor = new();

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public DeviceDegradationReport? LastDeviceDegradation { get; private set; }

    public string EngineFamily => EngineFamilyName;

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        string normalizedAudioPath,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken) =>
        await TranscribeAsync(
            new AudioTranscriptionRequest(normalizedAudioPath, regions),
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        StageRuntimePlan plan = await PlanStageAsync(request, cancellationToken).ConfigureAwait(false);
        return await TranscribeAsync(request, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StageRuntimePlan> PlanStageAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        InferenceRequestOptions options = request.Options ?? InferenceRequestOptions.Default;
        return await runtimePlanner.PlanAsync(
            await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(new StageRuntimePlanningRequest(
                RuntimeStage.Asr,
                options.NormalizedPreferredModelAlias,
                SourceLanguage: request.SourceLanguage,
                RequirePreferredModelAlias: options.RequirePreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    options.PreferredExecutionProvider,
                    options.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: options.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: options.NormalizedPreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request.Regions);
        EnsurePlanReady(plan, RuntimeStage.Asr);

        IAudioSamples audio = await WaveAudioReader.ReadMonoPcm16Async(request.NormalizedAudioPath, cancellationToken).ConfigureAwait(false);
        // CreateResampledStream returns the source as-is when sample rates match;
        // otherwise it wraps and takes ownership of it. Dispose only the outer reader.
        using IAudioSamples targetAudio = AudioResampler.CreateResampledStream(audio, 16000);
        double durationSeconds = targetAudio.SampleFrameCount / 16000d;

        IReadOnlyList<SpeechRegion> effectiveRegions = request.Regions;
        if (request.Regions.Count == 0)
        {
            if (durationSeconds >= 30.0)
            {
                LastExecutionSummary = CreatePlannedOnlySummary(plan,
                    $"ASR skipped: VAD detected no speech regions in {durationSeconds:F1}s audio.");
                return [];
            }

            // Short audio with no VAD regions — treat full audio as one region
            effectiveRegions = [new SpeechRegion(0, 0.0, durationSeconds)];
        }

        DeviceFallbackSessionCreator.Result<WhisperSessionBundle> acquired = await DeviceFallbackSessionCreator
            .CreateWithDeviceFallbackAsync(
                plan,
                CreateWhisperSessionBundleAsync,
                ct => PlanStageAsync(request, ct),
                deviceExclusionProvider,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        LastDeviceDegradation = acquired.Degradation;
        StageRuntimePlan effectivePlan = acquired.Plan;
        using OnnxExecutionSessionFactory.WhisperSessionLease sessionLease = acquired.Lease.Lease;
        WhisperTokenizerDecoder tokenizer = await WhisperTokenizerDecoder
            .LoadAsync(acquired.Lease.ModelRootPath).ConfigureAwait(false);

        // Merged regions give the language detector more context per probe, but the
        // transcription loop must use the original effectiveRegions so that caller-provided
        // indices (e.g. from RetranscribeSegmentsAsync) are preserved in the output.
        IReadOnlyList<SpeechRegion> languageDetectionRegions = BuildTranscriptionRegions(effectiveRegions, durationSeconds);
        IReadOnlyList<SpeechRegion> transcriptionRegions = effectiveRegions
            .Where(static r => r.EndSeconds > r.StartSeconds)
            .OrderBy(static r => r.Index)
            .ToArray();
        string? requestedSourceLanguage = NormalizeLanguageCode(request.SourceLanguage);
        string? detectedTranscriptLanguage = requestedSourceLanguage is not null &&
                                             tokenizer.TryGetLanguageTokenId(requestedSourceLanguage) is not null
            ? requestedSourceLanguage
            : await DetectTranscriptLanguageAsync(
                sessionLease,
                tokenizer,
                targetAudio,
                languageDetectionRegions,
                cancellationToken).ConfigureAwait(false);
        var segments = new List<RecognizedTranscriptSegment>(transcriptionRegions.Count);
        bool repetitionGuarded = false;

        foreach (SpeechRegion region in transcriptionRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RegionTranscription transcription = await TranscribeRegionAsync(
                sessionLease,
                tokenizer,
                targetAudio,
                region,
                detectedTranscriptLanguage,
                cancellationToken).ConfigureAwait(false);
            repetitionGuarded |= transcription.RepetitionGuarded;

            segments.Add(new RecognizedTranscriptSegment(
                region.Index,
                region.StartSeconds,
                region.EndSeconds,
                transcription.Text,
                transcription.DetectedLanguage ?? detectedTranscriptLanguage,
                transcription.Words));
        }

        LastExecutionSummary = CreateExecutionSummary(effectivePlan, sessionLease, repetitionGuarded);
        return segments;
    }

    private async Task<WhisperSessionBundle> CreateWhisperSessionBundleAsync(
        StageRuntimePlan plan,
        CancellationToken cancellationToken)
    {
        EnsurePlanReady(plan, RuntimeStage.Asr);
        string encoderModelPath = ResolvePlannedModelPath(plan);
        string decoderModelPath = ResolveWhisperDecoderPath(encoderModelPath);
        string modelRootPath = ResolveModelRootPath(encoderModelPath);
        OnnxExecutionSessionFactory.WhisperSessionLease lease = await OnnxExecutionSessionFactory
            .CreatePooledWhisperAsync(
                "whisper-onnx",
                encoderModelPath,
                decoderModelPath,
                plan.ExecutionProvider!.Value,
                cancellationToken,
                additionalTrtEncoderOptions: TrtEncoderOptions)
            .ConfigureAwait(false);
        return new WhisperSessionBundle(lease, encoderModelPath, decoderModelPath, modelRootPath);
    }

    private sealed record WhisperSessionBundle(
        OnnxExecutionSessionFactory.WhisperSessionLease Lease,
        string EncoderModelPath,
        string DecoderModelPath,
        string ModelRootPath);

    private async Task<string?> DetectTranscriptLanguageAsync(
        OnnxExecutionSessionFactory.WhisperSessionLease sessionLease,
        WhisperTokenizerDecoder tokenizer,
        IAudioSamples targetAudio,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken)
    {
        var detectedLanguages = new List<string>(regions.Count);
        foreach (SpeechRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RegionTranscription transcription = await TranscribeRegionAsync(
                sessionLease,
                tokenizer,
                targetAudio,
                region,
                forcedLanguage: null,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(transcription.DetectedLanguage))
            {
                detectedLanguages.Add(transcription.DetectedLanguage);
            }
        }

        return ResolveDetectedLanguage(detectedLanguages);
    }

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        string normalized = languageCode.Trim().ToLowerInvariant().Replace('_', '-');
        int separatorIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            normalized = normalized[..separatorIndex];
        }

        return normalized.Length is >= 2 and <= 8 &&
               normalized.All(static character => character is >= 'a' and <= 'z')
            ? normalized
            : null;
    }

    internal static IReadOnlyList<SpeechRegion> BuildTranscriptionRegionsForTesting(
        IReadOnlyList<SpeechRegion> regions,
        double durationSeconds) =>
        BuildTranscriptionRegions(regions, durationSeconds);

    internal static IReadOnlyList<SpeechRegion> BuildTranscriptionRegions(
        IReadOnlyList<SpeechRegion> regions,
        double durationSeconds)
    {
        if (regions.Count == 0 || durationSeconds <= 0d)
        {
            return [];
        }

        SpeechRegion[] orderedRegions = regions
            .Where(static region => region.EndSeconds > region.StartSeconds)
            .OrderBy(static region => region.StartSeconds)
            .ToArray();
        if (orderedRegions.Length == 0)
        {
            return [];
        }

        var mergedRegions = new List<(double Start, double End)>();
        double currentStart = orderedRegions[0].StartSeconds;
        double currentEnd = orderedRegions[0].EndSeconds;

        foreach (SpeechRegion region in orderedRegions.Skip(1))
        {
            double gapSeconds = region.StartSeconds - currentEnd;
            double mergedDuration = region.EndSeconds - currentStart;
            if (gapSeconds <= TranscriptionMergeGapSeconds &&
                mergedDuration <= MaxChunkDurationSeconds)
            {
                currentEnd = Math.Max(currentEnd, region.EndSeconds);
                continue;
            }

            mergedRegions.Add(ExpandRegion(currentStart, currentEnd, durationSeconds));
            currentStart = region.StartSeconds;
            currentEnd = region.EndSeconds;
        }

        mergedRegions.Add(ExpandRegion(currentStart, currentEnd, durationSeconds));

        return NormalizeExpandedRegions(mergedRegions)
            .Select((region, index) => new SpeechRegion(index, region.Start, region.End))
            .ToArray();
    }

    private static IReadOnlyList<(double Start, double End)> NormalizeExpandedRegions(
        IReadOnlyList<(double Start, double End)> regions)
    {
        var normalized = new List<(double Start, double End)>(regions.Count);
        foreach ((double start, double end) in regions.OrderBy(static region => region.Start))
        {
            if (normalized.Count == 0)
            {
                normalized.Add((start, end));
                continue;
            }

            (double Start, double End) previous = normalized[^1];
            if (start <= previous.End && end - previous.Start <= MaxChunkDurationSeconds)
            {
                normalized[^1] = (previous.Start, Math.Max(previous.End, end));
                continue;
            }

            double nonOverlappingStart = Math.Max(start, previous.End);
            if (end > nonOverlappingStart)
            {
                normalized.Add((nonOverlappingStart, end));
            }
        }

        return normalized;
    }

    private static (double Start, double End) ExpandRegion(
        double startSeconds,
        double endSeconds,
        double durationSeconds)
    {
        double start = Math.Max(0d, startSeconds - TranscriptionContextPaddingSeconds);
        double end = Math.Min(durationSeconds, endSeconds + TranscriptionContextPaddingSeconds);
        double expandedDuration = end - start;
        if (expandedDuration >= MinTranscriptionRegionSeconds || durationSeconds <= expandedDuration)
        {
            return (start, end);
        }

        double extraSeconds = MinTranscriptionRegionSeconds - expandedDuration;
        double leftExtra = Math.Min(start, extraSeconds / 2d);
        start -= leftExtra;
        extraSeconds -= leftExtra;

        double rightExtra = Math.Min(durationSeconds - end, extraSeconds);
        end += rightExtra;
        extraSeconds -= rightExtra;

        start = Math.Max(0d, start - extraSeconds);
        return (start, end);
    }
    private async Task<RegionTranscription> TranscribeRegionAsync(
        OnnxExecutionSessionFactory.WhisperSessionLease sessionLease,
        WhisperTokenizerDecoder tokenizer,
        IAudioSamples targetAudio,
        SpeechRegion region,
        string? forcedLanguage,
        CancellationToken cancellationToken)
    {
        long startSample = Math.Max(0, (long)Math.Floor(region.StartSeconds * 16000d));
        long endSample = Math.Min(targetAudio.SampleFrameCount, (long)Math.Ceiling(region.EndSeconds * 16000d));
        if (endSample <= startSample)
        {
            return new RegionTranscription(string.Empty, DetectedLanguage: null, Words: []);
        }

        float[] regionSamples = new float[checked((int)(endSample - startSample))];
        targetAudio.ReadMonoSamples(startSample, regionSamples);
        List<string> chunkTexts = [];
        List<RecognizedTranscriptWord> chunkWords = [];
        List<string> detectedLanguages = [];
        bool repetitionGuarded = false;
        int maxChunkSamples = (int)(MaxChunkDurationSeconds * 16000d);

        for (int offset = 0; offset < regionSamples.Length; offset += maxChunkSamples)
        {
            int chunkLength = Math.Min(maxChunkSamples, regionSamples.Length - offset);
            double chunkDurationSeconds = chunkLength / 16000d;
            DenseTensor<float> features;
            {
                ReadOnlySpan<float> chunkSpan = new ReadOnlySpan<float>(regionSamples, offset, chunkLength);
                features = featureExtractor.Extract(chunkSpan);
            }

            using var encoderInputs = new InputSet(
            [
                NamedOnnxValue.CreateFromTensor("input_features", features)
            ]);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults =
                sessionLease.EncoderSession.RunWithRetry(encoderInputs.Values);
            Tensor<float> hiddenStates = encoderResults.Single().AsTensor<float>();

            WhisperDecodeResult decodeResult = await GreedyDecodeAsync(
                sessionLease.DecoderSession,
                tokenizer,
                hiddenStates,
                forcedLanguage,
                chunkDurationSeconds,
                cancellationToken).ConfigureAwait(false);
            repetitionGuarded |= decodeResult.RepetitionGuarded;
            string decodedText = tokenizer.DecodeText(decodeResult.OutputTokens);
            if (!string.IsNullOrWhiteSpace(decodedText))
            {
                chunkTexts.Add(decodedText);
                double chunkStartSeconds = region.StartSeconds + (offset / 16000d);
                double chunkEndSeconds = chunkStartSeconds + chunkDurationSeconds;
                chunkWords.AddRange(BuildRecognizedWords(
                    decodedText,
                    chunkStartSeconds,
                    chunkEndSeconds,
                    decodeResult.RepetitionGuarded ? [0d] : decodeResult.TokenConfidences));
            }

            if (!string.IsNullOrWhiteSpace(decodeResult.DetectedLanguage))
            {
                detectedLanguages.Add(decodeResult.DetectedLanguage);
            }
        }

        IReadOnlyList<RecognizedTranscriptWord> regionWords = repetitionGuarded
            ? ReindexWords(chunkWords
                .Select(static word => new RecognizedTranscriptWord(
                    word.WordIndex,
                    word.StartSeconds,
                    word.EndSeconds,
                    word.Text,
                    0d))
                .ToArray())
            : ReindexWords(chunkWords);

        return new RegionTranscription(
            string.Join(" ", chunkTexts).Trim(),
            ResolveDetectedLanguage(detectedLanguages),
            regionWords,
            repetitionGuarded);
    }

    private static Task<WhisperDecodeResult> GreedyDecodeAsync(
        InferenceSession decoderSession,
        WhisperTokenizerDecoder tokenizer,
        Tensor<float> encoderHiddenStates,
        string? forcedLanguage,
        double chunkDurationSeconds,
        CancellationToken cancellationToken)
    {
        int? detectedLanguageToken = tokenizer.TryGetLanguageTokenId(forcedLanguage) ??
                                     DetectLanguageToken(decoderSession, tokenizer, encoderHiddenStates);
        IReadOnlyList<int> promptTokens = tokenizer.BuildTranscriptionPrompt(detectedLanguageToken);
        string? detectedLanguage = tokenizer.TryGetLanguageCode(detectedLanguageToken);
        var generated = promptTokens.Select(static token => (long)token).ToList();
        var outputTokens = new List<int>();
        var tokenConfidences = new List<double?>();
        const int maxDecoderContextTokens = 448;
        int maxOutputTokens = Math.Min(
            CalculateMaxDecodeOutputTokens(chunkDurationSeconds),
            Math.Max(0, maxDecoderContextTokens - generated.Count));
        bool repetitionGuarded = false;

        for (int step = 0; step < maxOutputTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var decoderInputs = CreateDecoderInputs(generated, encoderHiddenStates);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoderResults = decoderSession.RunWithRetry(decoderInputs.Values);
            Tensor<float> logits = decoderResults.Single(static result => result.Name == "logits").AsTensor<float>();

            int sequenceLength = logits.Dimensions[1];
            int vocabularySize = logits.Dimensions[2];
            (int nextToken, double? confidence) = SelectNextToken(logits, sequenceLength - 1, vocabularySize, tokenizer);
            if (nextToken == tokenizer.EndOfTranscriptToken || nextToken < 0)
            {
                break;
            }

            generated.Add(nextToken);
            outputTokens.Add(nextToken);
            tokenConfidences.Add(confidence);
            if (TryTrimRepeatedTail(outputTokens, tokenConfidences))
            {
                repetitionGuarded = true;
                break;
            }
        }

        return Task.FromResult(new WhisperDecodeResult(
            outputTokens,
            detectedLanguage,
            tokenConfidences,
            repetitionGuarded));
    }

    internal static int CalculateMaxDecodeOutputTokensForTesting(double chunkDurationSeconds) =>
        CalculateMaxDecodeOutputTokens(chunkDurationSeconds);

    internal static (IReadOnlyList<int> Tokens, bool RepetitionGuarded) ApplyRepetitionGuardForTesting(
        IReadOnlyList<int> outputTokens)
    {
        var guardedTokens = outputTokens.ToList();
        List<double?> tokenConfidences = Enumerable.Repeat<double?>(1d, guardedTokens.Count).ToList();
        bool repetitionGuarded = TryTrimRepeatedTail(guardedTokens, tokenConfidences);
        return (guardedTokens, repetitionGuarded);
    }

    private static int CalculateMaxDecodeOutputTokens(double chunkDurationSeconds)
    {
        double normalizedDurationSeconds = double.IsFinite(chunkDurationSeconds) && chunkDurationSeconds > 0d
            ? chunkDurationSeconds
            : 0d;
        return Math.Min(448, Math.Max(48, (int)Math.Ceiling(normalizedDurationSeconds * 12d) + 24));
    }

    // Called after each generated token to stop active runaway loops. This is
    // intentionally not a full transcript cleanup pass for earlier repeats.
    private static bool TryTrimRepeatedTail(List<int> outputTokens, List<double?> tokenConfidences)
    {
        for (int ngramLength = 3; ngramLength <= 32; ngramLength++)
        {
            int repeatLimit = ngramLength <= 3 ? 4 : ngramLength <= 8 ? 3 : 2;
            int repeatedTokenCount = ngramLength * repeatLimit;
            if (outputTokens.Count < repeatedTokenCount)
            {
                continue;
            }

            int repeatedStart = outputTokens.Count - repeatedTokenCount;
            bool repeated = true;
            for (int repeatIndex = 1; repeatIndex < repeatLimit && repeated; repeatIndex++)
            {
                int candidateStart = repeatedStart + (ngramLength * repeatIndex);
                for (int offset = 0; offset < ngramLength; offset++)
                {
                    if (outputTokens[repeatedStart + offset] != outputTokens[candidateStart + offset])
                    {
                        repeated = false;
                        break;
                    }
                }
            }

            if (!repeated)
            {
                continue;
            }

            int removeCount = ngramLength * (repeatLimit - 1);
            int removeStart = outputTokens.Count - removeCount;
            outputTokens.RemoveRange(removeStart, removeCount);
            tokenConfidences.RemoveRange(removeStart, removeCount);
            return true;
        }

        return false;
    }

    private static string? ResolveDetectedLanguage(IReadOnlyList<string> detectedLanguages) =>
        detectedLanguages
            .Where(static language => !string.IsNullOrWhiteSpace(language))
            .Select(static language => language.Trim().ToLowerInvariant())
            .GroupBy(static language => language, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => group.Key)
            .FirstOrDefault();

    private static int? DetectLanguageToken(
        InferenceSession decoderSession,
        WhisperTokenizerDecoder tokenizer,
        Tensor<float> encoderHiddenStates)
    {
        if (tokenizer.LanguageTokenIds.Count == 0)
        {
            return null;
        }

        using var decoderInputs = CreateDecoderInputs([tokenizer.DecoderStartToken], encoderHiddenStates);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoderResults = decoderSession.RunWithRetry(decoderInputs.Values);
        Tensor<float> logits = decoderResults.Single(static result => result.Name == "logits").AsTensor<float>();

        int sequenceLength = logits.Dimensions[1];
        int bestToken = SelectBestToken(logits, sequenceLength - 1, tokenizer.LanguageTokenIds);
        return bestToken >= 0 ? bestToken : null;
    }

    private static (int TokenId, double? Confidence) SelectNextToken(
        Tensor<float> logits,
        int timeIndex,
        int vocabularySize,
        WhisperTokenizerDecoder tokenizer)
    {
        int bestToken = -1;
        float bestValue = float.NegativeInfinity;
        for (int tokenIndex = 0; tokenIndex < vocabularySize; tokenIndex++)
        {
            if (tokenIndex >= tokenizer.TimestampBeginToken && tokenIndex != tokenizer.EndOfTranscriptToken)
            {
                continue;
            }

            if (tokenizer.SuppressedTokens.Contains(tokenIndex))
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

        if (bestToken < 0)
        {
            return (bestToken, Confidence: null);
        }

        double denominator = 0d;
        double selectedNumerator = 0d;
        for (int tokenIndex = 0; tokenIndex < vocabularySize; tokenIndex++)
        {
            if (tokenIndex >= tokenizer.TimestampBeginToken && tokenIndex != tokenizer.EndOfTranscriptToken)
            {
                continue;
            }

            if (tokenizer.SuppressedTokens.Contains(tokenIndex))
            {
                continue;
            }

            double numerator = Math.Exp(logits[0, timeIndex, tokenIndex] - bestValue);
            denominator += numerator;
            if (tokenIndex == bestToken)
            {
                selectedNumerator = numerator;
            }
        }

        return (bestToken, denominator > 0d ? selectedNumerator / denominator : null);
    }

    private static int SelectBestToken(
        Tensor<float> logits,
        int timeIndex,
        IEnumerable<int> candidateTokenIds)
    {
        int bestToken = -1;
        float bestValue = float.NegativeInfinity;
        foreach (int tokenId in candidateTokenIds)
        {
            float value = logits[0, timeIndex, tokenId];
            if (value > bestValue)
            {
                bestValue = value;
                bestToken = tokenId;
            }
        }

        return bestToken;
    }

    private static InputSet CreateDecoderInputs(IReadOnlyList<long> generatedTokens, Tensor<float> encoderHiddenStates)
    {
        long[] tokenArray = generatedTokens.ToArray();
        return new InputSet(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(tokenArray, [1, tokenArray.Length])),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates)
        ]);
    }

    private static void EnsurePlanReady(StageRuntimePlan plan, RuntimeStage stage)
    {
        if (plan.IsRunnable() && plan.ExecutionProvider is not null && !string.IsNullOrWhiteSpace(plan.ModelAlias))
        {
            return;
        }

        throw new InvalidOperationException(
            plan.Fallback?.Detail ??
            $"Runtime planner did not produce a ready {stage} plan.");
    }

    private string ResolvePlannedModelPath(StageRuntimePlan plan)
    {
        return PlannedRuntimeModelResolver.ResolveModelPath(plan, modelPathResolver);
    }

    private static string ResolveWhisperDecoderPath(string encoderModelPath)
    {
        string fileName = Path.GetFileName(encoderModelPath);
        string decoderFileName = fileName.Replace("encoder_model", "decoder_model", StringComparison.OrdinalIgnoreCase);
        string decoderModelPath = Path.Combine(Path.GetDirectoryName(encoderModelPath)!, decoderFileName);
        if (File.Exists(decoderModelPath))
        {
            return Path.GetFullPath(decoderModelPath);
        }

        decoderModelPath = Path.Combine(Path.GetDirectoryName(encoderModelPath)!, "decoder_model.onnx");
        if (File.Exists(decoderModelPath))
        {
            return Path.GetFullPath(decoderModelPath);
        }

        throw new FileNotFoundException("Whisper decoder model was not found next to the encoder model.", decoderModelPath);
    }

    private static string ResolveModelRootPath(string encoderModelPath)
    {
        string? onnxDirectory = Path.GetDirectoryName(encoderModelPath);
        string? modelRoot = Path.GetDirectoryName(onnxDirectory);
        return modelRoot ?? throw new InvalidOperationException("Whisper model root path could not be resolved.");
    }

    private static StageRuntimeExecutionSummary CreateExecutionSummary(
        StageRuntimePlan plan,
        OnnxExecutionSessionFactory.WhisperSessionLease sessionLease,
        bool repetitionGuarded)
    {
        string bootstrapDetail = sessionLease.BootstrapDetail ?? string.Empty;
        if (repetitionGuarded)
        {
            bootstrapDetail = string.IsNullOrWhiteSpace(bootstrapDetail)
                ? RepetitionGuardBootstrapDetail
                : $"{bootstrapDetail} {RepetitionGuardBootstrapDetail}";
        }

        return new StageRuntimeExecutionSummary(
            sessionLease.RequestedProvider,
            sessionLease.SelectedProvider,
            plan.ModelId,
            plan.ModelAlias,
            plan.Variant,
            bootstrapDetail);
    }

    private static StageRuntimeExecutionSummary CreatePlannedOnlySummary(StageRuntimePlan plan, string bootstrapDetail) =>
        new(
            "auto",
            plan.ExecutionProvider!.Value.ToString().ToLowerInvariant(),
            plan.ModelId,
            plan.ModelAlias,
            plan.Variant,
            bootstrapDetail);

    internal static IReadOnlyList<RecognizedTranscriptWord> BuildRecognizedWordsForTesting(
        string text,
        double startSeconds,
        double endSeconds,
        IReadOnlyList<double?> tokenConfidences) =>
        BuildRecognizedWords(text, startSeconds, endSeconds, tokenConfidences);

    internal static IReadOnlyList<RecognizedTranscriptWord> BuildRecognizedWords(
        string text,
        double startSeconds,
        double endSeconds,
        IReadOnlyList<double?> tokenConfidences)
    {
        string[] words = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || endSeconds <= startSeconds)
        {
            return [];
        }

        double[] confidences = tokenConfidences
            .OfType<double>()
            .ToArray();
        double? averageConfidence = confidences.Length == 0
            ? null
            : confidences.Average();

        double step = (endSeconds - startSeconds) / words.Length;
        return words
            .Select((word, index) => new RecognizedTranscriptWord(
                index,
                startSeconds + (step * index),
                index == words.Length - 1 ? endSeconds : startSeconds + (step * (index + 1)),
                word,
                averageConfidence))
            .ToArray();
    }

    internal static IReadOnlyList<RecognizedTranscriptWord> ReindexWords(
        IReadOnlyList<RecognizedTranscriptWord> words) =>
        words
            .OrderBy(static word => word.StartSeconds)
            .ThenBy(static word => word.WordIndex)
            .Select((word, index) => new RecognizedTranscriptWord(
                index,
                word.StartSeconds,
                word.EndSeconds,
                word.Text,
                word.Confidence))
            .ToArray();

    private sealed record RegionTranscription(
        string Text,
        string? DetectedLanguage,
        IReadOnlyList<RecognizedTranscriptWord> Words,
        bool RepetitionGuarded = false);

    private sealed record WhisperDecodeResult(
        IReadOnlyList<int> OutputTokens,
        string? DetectedLanguage,
        IReadOnlyList<double?> TokenConfidences,
        bool RepetitionGuarded);

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
