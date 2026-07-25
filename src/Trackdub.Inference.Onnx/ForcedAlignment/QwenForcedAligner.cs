using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Audio;

namespace Trackdub.Inference.Onnx.ForcedAlignment;

/// <summary>
/// Forced-alignment adapter backed by the Qwen3-ForcedAligner ONNX model.
/// Performs word-level alignment at 80 ms resolution (5 000 timestamp classes × 0.08 s/class).
/// Phoneme-level output is not supported by this model; <see cref="ForcedAlignmentResult.Phonemes"/>
/// is always empty.
/// </summary>
public sealed class QwenForcedAligner : IForcedAlignerAdapter, IDisposable
{
    // ── Constants ──────────────────────────────────────────────────────────────

    private const long TimestampTokenId = 151705L;
    private const int ClassCount = 5_000;
    private const string OnnxRelativePath = "onnx/model_q4.onnx";

    private static readonly IReadOnlyDictionary<string, string> SupportedLanguages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["zh"] = "Chinese",
            ["yue"] = "Cantonese",
            ["fr"] = "French",
            ["de"] = "German",
            ["it"] = "Italian",
            ["ja"] = "Japanese",
            ["ko"] = "Korean",
            ["pt"] = "Portuguese",
            ["ru"] = "Russian",
            ["es"] = "Spanish",
        };

    private static readonly string SupportedCodesDisplay =
        string.Join(", ", SupportedLanguages.Keys.OrderBy(static k => k, StringComparer.Ordinal));

    // ── Fields ─────────────────────────────────────────────────────────────────

    private readonly string modelRootPath;
    private readonly ILogger? logger;
    private readonly QwenAudioFeatureExtractor featureExtractor = new();
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private InferenceSession? session;
    private IReadOnlyDictionary<string, long>? vocab;
    private IReadOnlyList<(string First, string Second)>? merges;
    private int disposed;

    // ── Construction ───────────────────────────────────────────────────────────

    public QwenForcedAligner(string modelRootPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(modelRootPath);
        this.modelRootPath = string.IsNullOrWhiteSpace(modelRootPath)
            ? string.Empty
            : Path.GetFullPath(modelRootPath);
        this.logger = logger;
    }

    // ── IForcedAlignerAdapter ──────────────────────────────────────────────────

    public string ProviderId => "onnx-qwen-forced-aligner";

    public string ModelId => "qwen3-forced-aligner-0.6b-q4-onnx";

    /// <summary>
    /// Word-level alignment only (80 ms timestamp classes); phoneme output is never produced.
    /// Phoneme-dependent stages must route to a phoneme-capable adapter instead.
    /// </summary>
    public bool SupportsPhonemeTimings => false;

    /// <summary>
    /// <see langword="true"/> when the ONNX model file and config.json are both present on disk.
    /// Does not verify the checksum or whether the session can load.
    /// </summary>
    public bool IsAvailable =>
        modelRootPath.Length > 0 &&
        File.Exists(Path.Combine(modelRootPath, OnnxRelativePath)) &&
        File.Exists(Path.Combine(modelRootPath, "config.json"));

    // ── IForcedAligner ─────────────────────────────────────────────────────────

    public async Task<ForcedAlignmentResult> AlignAsync(
        ForcedAlignmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed != 0, this);

        // ── 1. Language gate ──────────────────────────────────────────────────
        string? language = request.LanguageCode?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(language) && !SupportedLanguages.ContainsKey(language))
        {
            return SkippedResult(
                request.SegmentId,
                $"Language '{language}' is not supported by Qwen3-ForcedAligner. " +
                $"Supported: {SupportedCodesDisplay}.");
        }

        if (string.IsNullOrWhiteSpace(request.NormalizedTranscript))
        {
            return SkippedResult(request.SegmentId, "Empty transcript — nothing to align.");
        }

        try
        {
            // ── 2. Lazy session / vocab / merges load ─────────────────────────
            await EnsureSessionLoadedAsync(cancellationToken).ConfigureAwait(false);

            // ── 3. Read and resample audio ────────────────────────────────────
            using IAudioSamples rawAudio = await WaveAudioReader
                .ReadMonoPcm16Async(request.AudioPath, cancellationToken)
                .ConfigureAwait(false);

            using IAudioSamples audio = AudioResampler.CreateResampledStream(rawAudio, 16_000);
            int totalSamples = checked((int)audio.SampleFrameCount);
            float[] samples = new float[totalSamples];
            audio.ReadMonoSamples(0, samples);

            // ── 4. Extract mel features ───────────────────────────────────────
            DenseTensor<float> melFeatures = featureExtractor.Extract(samples);
            int numFrames = melFeatures.Dimensions[2];

            // ── 5. Tokenize + inject timestamp slots ──────────────────────────
            (long[] inputIds, long[] attentionMask, int[] timestampPositions) =
                QwenTimestampProcessor.PrepareTokens(
                    request.NormalizedTranscript,
                    TimestampTokenId,
                    vocab!,
                    merges!);

            int seqLen = inputIds.Length;
            string[] words = request.NormalizedTranscript
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // ── 6. Build ONNX inputs ──────────────────────────────────────────
            var inputIdsTensor = new DenseTensor<long>(inputIds, [1, seqLen]);
            var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, seqLen]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
                NamedOnnxValue.CreateFromTensor("input_features", melFeatures),
            };

            // ── 7. ONNX forward pass ──────────────────────────────────────────
            cancellationToken.ThrowIfCancellationRequested();
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                session!.Run(inputs);

            Tensor<float> logitsTensor = outputs
                .Single(static o => string.Equals(o.Name, "logits", StringComparison.Ordinal))
                .AsTensor<float>();

            float[] logitsFlat = logitsTensor.ToArray();
            int inferredSeqLen = logitsTensor.Dimensions[1];
            int inferredClassCount = logitsTensor.Dimensions[2];

            // ── 8. Extract word timings ───────────────────────────────────────
            WordTiming[] wordTimings = QwenTimestampProcessor.ExtractWordTimings(
                logitsFlat,
                inferredSeqLen,
                inferredClassCount,
                timestampPositions,
                words);

            // ── 9. Build confidence ───────────────────────────────────────────
            double overallConf = QwenTimestampProcessor.ComputeMeanTimestampConfidence(
                logitsFlat,
                inferredSeqLen,
                inferredClassCount,
                timestampPositions);

            double wordMean = wordTimings.Length > 0
                ? wordTimings.Average(static w => w.Confidence)
                : 0.0;

            var confidence = new AlignmentConfidence(
                Overall: overallConf,
                WordLevelMean: wordMean,
                PhonemeLevelMean: null);

            ForcedAlignmentStatus status = overallConf >= request.Options.MinOverallConfidence
                ? ForcedAlignmentStatus.Success
                : request.Options.AllowPartial
                    ? ForcedAlignmentStatus.Partial
                    : ForcedAlignmentStatus.Failed;

            return new ForcedAlignmentResult(
                SegmentId: request.SegmentId,
                Status: status,
                Words: wordTimings,
                Phonemes: [],
                Confidence: confidence,
                SkipReason: null,
                ProviderId: ProviderId,
                ModelId: ModelId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Qwen3-ForcedAligner inference failed for segment {SegmentId}.", request.SegmentId);
            return FailedResult(request.SegmentId, ex.Message);
        }
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        sessionLock.Wait(TimeSpan.FromSeconds(5));
        try
        {
            session?.Dispose();
            session = null;
        }
        finally
        {
            sessionLock.Dispose();
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task EnsureSessionLoadedAsync(CancellationToken cancellationToken)
    {
        if (session is not null && vocab is not null && merges is not null)
        {
            return;
        }

        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is null)
            {
                string onnxPath = Path.Combine(modelRootPath, OnnxRelativePath);
                using var options = new SessionOptions();
                session = new InferenceSession(onnxPath, options);
                logger?.LogInformation("Qwen3-ForcedAligner ONNX session loaded from {Path}.", onnxPath);
            }

            if (vocab is null || merges is null)
            {
                (vocab, merges) = await LoadTokenizerAsync(modelRootPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private static async Task<(IReadOnlyDictionary<string, long> Vocab, IReadOnlyList<(string, string)> Merges)>
        LoadTokenizerAsync(string modelRootPath, CancellationToken cancellationToken)
    {
        // vocab.json: { "token_string": token_id, ... }
        string vocabPath = Path.Combine(modelRootPath, "vocab.json");
        string vocabJson = await File.ReadAllTextAsync(vocabPath, cancellationToken).ConfigureAwait(false);

        var rawVocab = new Dictionary<string, long>(StringComparer.Ordinal);
        using (JsonDocument doc = JsonDocument.Parse(vocabJson))
        {
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                rawVocab[prop.Name] = prop.Value.GetInt64();
            }
        }

        // merges.txt: first line may be "#version: 0.2"; subsequent lines are "first second"
        string mergesPath = Path.Combine(modelRootPath, "merges.txt");
        string[] mergeLines = await File.ReadAllLinesAsync(mergesPath, cancellationToken).ConfigureAwait(false);

        var mergeList = new List<(string, string)>(mergeLines.Length);
        foreach (string line in mergeLines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            int spaceIndex = line.IndexOf(' ', StringComparison.Ordinal);
            if (spaceIndex <= 0 || spaceIndex == line.Length - 1)
            {
                continue;
            }

            mergeList.Add((line[..spaceIndex], line[(spaceIndex + 1)..]));
        }

        return (rawVocab, mergeList);
    }

    private static ForcedAlignmentResult SkippedResult(string segmentId, string reason) =>
        new(
            SegmentId: segmentId,
            Status: ForcedAlignmentStatus.Skipped,
            Words: [],
            Phonemes: [],
            Confidence: new AlignmentConfidence(0.0, null, null),
            SkipReason: reason,
            ProviderId: null,
            ModelId: null);

    private static ForcedAlignmentResult FailedResult(string segmentId, string reason) =>
        new(
            SegmentId: segmentId,
            Status: ForcedAlignmentStatus.Failed,
            Words: [],
            Phonemes: [],
            Confidence: new AlignmentConfidence(0.0, null, null),
            SkipReason: reason,
            ProviderId: null,
            ModelId: null);
}
