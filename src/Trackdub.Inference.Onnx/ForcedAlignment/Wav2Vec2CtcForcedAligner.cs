// Layer note: the brief requested IApplicationLogger? logger in the constructor.
// IApplicationLogger is defined in Trackdub.Application, which is outside the allowed
// dependency graph for Trackdub.Inference.Onnx (Inference.Onnx → Inference, Contracts, Domain only).
// The parameter has been OMITTED to avoid the layer violation. The orchestrator should
// provide a cross-cutting logger contract in Trackdub.Contracts or Trackdub.Inference if
// logging from this layer is needed (Task 7 / future brief).

using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Audio;

namespace Trackdub.Inference.Onnx.ForcedAlignment;

/// <summary>
/// Forced aligner backed by wav2vec2-lv60 fine-tuned for espeak phoneme output.
/// Accepts either the bundled default FP16 ONNX (<c>onnx/model_fp16.onnx</c>) or the
/// performance INT8 variant (<c>onnx/model_int8.onnx</c>), plus <c>vocab.json</c>.
/// Prefers INT8 when both are present. When an <see cref="IGraphemeToPhoneme"/> is supplied
/// and the request carries a language code, transcripts are phonemized before CTC decode.
/// </summary>
public sealed class Wav2Vec2CtcForcedAligner : IForcedAlignerAdapter, IDisposable
{
    private const string OnnxSubdir = "onnx";
    private const string Int8OnnxFileName = "model_int8.onnx";
    private const string Fp16OnnxFileName = "model_fp16.onnx";
    private const string VocabRelPath = "vocab.json";
    private const int TargetSampleRate = 16_000;

    // wav2vec2 outputs one frame per 20 ms (320-sample stride at 16 kHz)
    private const double FrameDurationSeconds = 0.020;

    private static readonly IReadOnlyList<WordTiming> NoWords = [];
    private static readonly IReadOnlyList<PhonemeTiming> NoPhonemes = [];
    private static readonly AlignmentConfidence ZeroConfidence = new(0d, null, null);

    // Fallback Latin-character → espeak IPA map used only when no phonemizer is injected
    // or phonemization fails. Prefer IGraphemeToPhoneme (EspeakNgPhonemizer) for real speech.
    private static readonly IReadOnlyDictionary<char, string> LatinToIpaMap =
        new Dictionary<char, string>
        {
            ['a'] = "æ",
            ['c'] = "k",
            ['e'] = "ɛ",
            ['g'] = "ɡ",  // IPA ɡ (U+0261), distinct from ASCII g
            ['i'] = "ɪ",
            ['j'] = "dʒ",
            ['o'] = "ɒ",
            ['q'] = "k",
            ['r'] = "ɹ",
            ['u'] = "ʊ",
            ['x'] = "z",   // simplified; 'ks' requires two tokens
            ['y'] = "j",
        };

    private readonly string _modelRootPath;
    private readonly string _vocabPath;
    private readonly IGraphemeToPhoneme? _phonemizer;
    private readonly object _loadLock = new();

    private Wav2Vec2PhonemeVocab? _vocab;
    private InferenceSession? _session;
    private bool _loadFailed;
    private int _disposed;

    public Wav2Vec2CtcForcedAligner(string modelRootPath, IGraphemeToPhoneme? phonemizer = null)
    {
        ArgumentNullException.ThrowIfNull(modelRootPath);
        _modelRootPath = modelRootPath;
        _vocabPath = modelRootPath.Length > 0 ? Path.Combine(modelRootPath, VocabRelPath) : string.Empty;
        _phonemizer = phonemizer;
    }

    public string ProviderId => "onnx-ctc-phoneme-aligner";
    public string ModelId => "wav2vec2-lv60-espeak-cv-ft-onnx";

    /// <summary>CTC decode over the espeak-ipa vocabulary yields phoneme-level timings.</summary>
    public bool SupportsPhonemeTimings => true;

    public bool IsAvailable
    {
        get
        {
            string onnxPath = ResolveOnnxPath(_modelRootPath);
            return !string.IsNullOrEmpty(onnxPath)
                && File.Exists(onnxPath)
                && File.Exists(_vocabPath);
        }
    }

    /// <summary>
    /// Prefer INT8 when present (smaller/faster); otherwise accept the bundled FP16 default.
    /// Manifest default variant is FP16 — hardcoding INT8 alone left IsAvailable false after
    /// a default-variant download.
    /// </summary>
    internal static string ResolveOnnxPath(string modelRootPath)
    {
        if (string.IsNullOrEmpty(modelRootPath))
            return string.Empty;

        string int8 = Path.Combine(modelRootPath, OnnxSubdir, Int8OnnxFileName);
        if (File.Exists(int8))
            return int8;

        string fp16 = Path.Combine(modelRootPath, OnnxSubdir, Fp16OnnxFileName);
        if (File.Exists(fp16))
            return fp16;

        // Neither present yet — keep INT8 as the expected path so IsAvailable flips when
        // Model Manager finishes downloading either variant (INT8 preferred once both exist).
        return int8;
    }

    public async Task<ForcedAlignmentResult> AlignAsync(
        ForcedAlignmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (!IsAvailable)
        {
            return SkippedResult(request.SegmentId,
                "Model files not found. Download the wav2vec2-lv60-espeak-cv-ft model from the Model Manager.");
        }

        try
        {
            return await AlignCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FailedResult(request.SegmentId, ex.Message);
        }
    }

    private async Task<ForcedAlignmentResult> AlignCoreAsync(
        ForcedAlignmentRequest request,
        CancellationToken cancellationToken)
    {
        (Wav2Vec2PhonemeVocab vocab, InferenceSession session) = EnsureLoaded();

        // Build phoneme index sequence from the normalised transcript.
        // Prefer eSpeak phonemization when a language code is available.
        if (!TryBuildPhonemeSequence(
                request.NormalizedTranscript,
                vocab,
                _phonemizer,
                request.LanguageCode,
                out int[] phonemeSequence,
                out string[]? phonemeSymbols,
                out string[]? wordTexts,
                out int[]? phonemeWordMap))
        {
            return SkippedResult(request.SegmentId,
                "Could not map transcript to the wav2vec2 espeak-ipa vocabulary. " +
                "Provide a language code so eSpeak can phonemize, or use Latin-script text.");
        }

        if (phonemeSequence.Length == 0)
        {
            return SkippedResult(request.SegmentId, "Transcript is empty after phoneme mapping.");
        }

        // Load and resample audio to 16 kHz float32 PCM
        using IAudioSamples rawAudio = await WaveAudioReader
            .ReadMonoPcm16Async(request.AudioPath, cancellationToken)
            .ConfigureAwait(false);

        float[] pcm;
        using (IAudioSamples targetAudio = AudioResampler.CreateResampledStream(rawAudio, TargetSampleRate))
        {
            int numSamples = (int)Math.Min(targetAudio.SampleFrameCount, int.MaxValue);
            pcm = new float[numSamples];
            targetAudio.ReadMonoSamples(0, pcm);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Run ONNX forward pass: input_values [1, numSamples] → logits [1, numFrames, vocabSize]
        float[] logSoftmaxFlat = RunOnnxForwardPass(session, pcm, vocab.Count);

        int numFrames = logSoftmaxFlat.Length / vocab.Count;
        if (numFrames == 0)
            return FailedResult(request.SegmentId, "ONNX forward pass returned zero frames.");

        // CTC Viterbi alignment
        (int StartFrame, int EndFrame, float LogProb)[] alignment = CtcViterbiAligner.Align(
            logSoftmaxFlat, numFrames, vocab.Count, phonemeSequence, vocab.BlankIndex);

        if (alignment.Length == 0)
        {
            return SkippedResult(request.SegmentId,
                "Phoneme sequence could not be aligned (sequence may be longer than audio).");
        }

        // Build PhonemeTiming list (skip word-boundary tokens)
        var phonemeTimings = new List<PhonemeTiming>(alignment.Length);
        for (int i = 0; i < alignment.Length; i++)
        {
            string symbol = phonemeSymbols![i];
            if (symbol == vocab.WordBoundarySymbol)
                continue;

            TimeSpan start = TimeSpan.FromSeconds(alignment[i].StartFrame * FrameDurationSeconds);
            TimeSpan end = TimeSpan.FromSeconds((alignment[i].EndFrame + 1) * FrameDurationSeconds);
            double phConf = Math.Exp(alignment[i].LogProb);
            string? wordText = wordTexts != null && phonemeWordMap != null
                ? wordTexts[phonemeWordMap[i]]
                : null;

            phonemeTimings.Add(new PhonemeTiming(
                Symbol: symbol,
                Inventory: "espeak-ipa",
                Start: start,
                End: end,
                Confidence: phConf,
                WordText: wordText));
        }

        // Build WordTiming by grouping phonemes at word-boundary tokens
        var wordTimings = BuildWordTimings(alignment, phonemeSymbols!, phonemeWordMap, wordTexts,
            vocab.WordBoundarySymbol);

        // Overall confidence: geometric mean of per-phoneme log-probs
        double meanLogProb = alignment.Length > 0
            ? alignment.Average(static a => (double)a.LogProb)
            : double.NegativeInfinity;
        double overallConfidence = double.IsNegativeInfinity(meanLogProb)
            ? 0d
            : Math.Clamp(Math.Exp(meanLogProb), 0d, 1d);

        var confidence = new AlignmentConfidence(
            Overall: overallConfidence,
            WordLevelMean: wordTimings.Count > 0
                ? wordTimings.Average(static w => w.Confidence)
                : null,
            PhonemeLevelMean: phonemeTimings.Count > 0
                ? phonemeTimings.Average(static p => p.Confidence)
                : null);

        ForcedAlignmentStatus status =
            overallConfidence >= request.Options.MinOverallConfidence
                ? ForcedAlignmentStatus.Success
                : (request.Options.AllowPartial
                    ? ForcedAlignmentStatus.Partial
                    : ForcedAlignmentStatus.Failed);

        return new ForcedAlignmentResult(
            SegmentId: request.SegmentId,
            Status: status,
            Words: wordTimings,
            Phonemes: phonemeTimings,
            Confidence: confidence,
            SkipReason: null,
            ProviderId: ProviderId,
            ModelId: ModelId);
    }

    // ONNX Run() is not cancellation-aware; callers must throw on the token before and after inference.
    private static float[] RunOnnxForwardPass(InferenceSession session, float[] pcm, int vocabSize)
    {
        var inputTensor = new DenseTensor<float>(pcm, [1, pcm.Length]);
        using var inputs = new InputSet(
        [
            NamedOnnxValue.CreateFromTensor("input_values", inputTensor)
        ]);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            session.Run(inputs.Values);

        DisposableNamedOnnxValue logitsValue = outputs.First(static o => o.Name == "logits");
        Tensor<float> logits = logitsValue.AsTensor<float>();

        int numFrames = logits.Dimensions[1];
        int outputVocabSize = logits.Dimensions[2];
        float[] flat = new float[numFrames * outputVocabSize];

        // Copy and apply numerically stable log-softmax per frame
        for (int t = 0; t < numFrames; t++)
        {
            int frameBase = t * outputVocabSize;

            float maxVal = float.NegativeInfinity;
            for (int c = 0; c < outputVocabSize; c++)
            {
                float v = logits[0, t, c];
                if (v > maxVal) maxVal = v;
            }

            float logSumExp = 0f;
            for (int c = 0; c < outputVocabSize; c++)
                logSumExp += MathF.Exp(logits[0, t, c] - maxVal);
            logSumExp = MathF.Log(logSumExp) + maxVal;

            for (int c = 0; c < outputVocabSize; c++)
                flat[frameBase + c] = logits[0, t, c] - logSumExp;
        }

        return flat;
    }

    /// <summary>
    /// Builds a CTC target sequence. When <paramref name="phonemizer"/> and a language code
    /// are available, phonemizes each word via eSpeak IPA and maps symbols into the vocab.
    /// Falls back to the crude Latin→IPA grapheme map otherwise.
    /// </summary>
    internal static bool TryBuildPhonemeSequence(
        string transcript,
        Wav2Vec2PhonemeVocab vocab,
        IGraphemeToPhoneme? phonemizer,
        string? languageCode,
        out int[] phonemeSequence,
        out string[]? phonemeSymbols,
        out string[]? wordTexts,
        out int[]? phonemeWordMap)
    {
        phonemeSymbols = null;
        wordTexts = null;
        phonemeWordMap = null;

        string[] words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            phonemeSequence = [];
            return false;
        }

        bool usePhonemizer = phonemizer is not null && !string.IsNullOrWhiteSpace(languageCode);

        var indices = new List<int>();
        var symbols = new List<string>();
        var wordTextList = new List<string>();
        var wordMap = new List<int>();

        for (int wordIdx = 0; wordIdx < words.Length; wordIdx++)
        {
            // Add word boundary between words (not before first word)
            if (wordIdx > 0 && vocab.WordBoundaryIndex >= 0)
            {
                indices.Add(vocab.WordBoundaryIndex);
                symbols.Add(vocab.WordBoundarySymbol);
                wordMap.Add(wordIdx - 1); // boundary belongs to previous word
            }

            wordTextList.Add(words[wordIdx]);

            if (usePhonemizer)
            {
                if (!TryAppendPhonemizedWord(
                        words[wordIdx],
                        languageCode!,
                        phonemizer!,
                        vocab,
                        wordIdx,
                        indices,
                        symbols,
                        wordMap))
                {
                    // Phonemizer failed for this word — fall back to grapheme map for the word.
                    if (!TryAppendGraphemeMappedWord(
                            words[wordIdx], vocab, wordIdx, indices, symbols, wordMap))
                    {
                        phonemeSequence = [];
                        return false;
                    }
                }

                continue;
            }

            if (!TryAppendGraphemeMappedWord(
                    words[wordIdx], vocab, wordIdx, indices, symbols, wordMap))
            {
                phonemeSequence = [];
                return false;
            }
        }

        if (indices.Count == 0)
        {
            phonemeSequence = [];
            return false;
        }

        phonemeSequence = [.. indices];
        phonemeSymbols = [.. symbols];
        wordTexts = [.. wordTextList];
        phonemeWordMap = [.. wordMap];
        return true;
    }

    private static bool TryAppendPhonemizedWord(
        string word,
        string languageCode,
        IGraphemeToPhoneme phonemizer,
        Wav2Vec2PhonemeVocab vocab,
        int wordIdx,
        List<int> indices,
        List<string> symbols,
        List<int> wordMap)
    {
        string ipa;
        try
        {
            ipa = phonemizer.Phonemize(word, languageCode);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ipa))
            return false;

        int appended = 0;
        foreach (string token in TokenizeIpaAgainstVocab(ipa, vocab))
        {
            if (!vocab.TryGetIndex(token, out int idx))
                continue;

            indices.Add(idx);
            symbols.Add(token);
            wordMap.Add(wordIdx);
            appended++;
        }

        return appended > 0 || IsAllSkippableAlignWord(word);
    }

    private static bool TryAppendGraphemeMappedWord(
        string word,
        Wav2Vec2PhonemeVocab vocab,
        int wordIdx,
        List<int> indices,
        List<string> symbols,
        List<int> wordMap)
    {
        int appended = 0;
        foreach (char c in word)
        {
            if (!char.IsAsciiLetter(c))
            {
                // Non-ASCII letter (digit, punctuation, non-Latin) — skip silently
                // for punctuation/digits; fail for non-ASCII letters.
                if (!char.IsAsciiLetterOrDigit(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
                    return false;
                continue;
            }

            char lower = char.ToLowerInvariant(c);

            // Try direct lookup (handles consonants where ASCII char == vocab token)
            if (vocab.TryGetIndex(lower.ToString(), out int directIdx))
            {
                indices.Add(directIdx);
                symbols.Add(lower.ToString());
                wordMap.Add(wordIdx);
                appended++;
                continue;
            }

            // Try IPA mapping
            if (LatinToIpaMap.TryGetValue(lower, out string? ipaSymbol) &&
                vocab.TryGetIndex(ipaSymbol, out int ipaIdx))
            {
                indices.Add(ipaIdx);
                symbols.Add(ipaSymbol);
                wordMap.Add(wordIdx);
                appended++;
                continue;
            }

            // Character maps to nothing in this vocab — cannot align
            return false;
        }

        return appended > 0 || IsAllSkippableAlignWord(word);
    }

    private static bool IsAllSkippableAlignWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return false;

        foreach (char c in word)
        {
            if (char.IsAsciiLetter(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Splits eSpeak IPA into vocab tokens via greedy longest-match against the vocab.
    /// Stress/length markers and whitespace are skipped. Unknown characters are skipped
    /// (not fatal) so partial IPA still yields a usable CTC target.
    /// </summary>
    internal static IEnumerable<string> TokenizeIpaAgainstVocab(string ipa, Wav2Vec2PhonemeVocab vocab)
    {
        ReadOnlySpan<char> span = ipa.AsSpan();
        var tokens = new List<string>();
        int i = 0;
        while (i < span.Length)
        {
            char c = span[i];
            if (char.IsWhiteSpace(c) || c is 'ˈ' or 'ˌ' or 'ː' or '.' or '-' or '\'' or '"')
            {
                i++;
                continue;
            }

            bool matched = false;
            for (int len = Math.Min(3, span.Length - i); len >= 1; len--)
            {
                string candidate = span.Slice(i, len).ToString();
                if (vocab.TryGetIndex(candidate, out _))
                {
                    tokens.Add(candidate);
                    i += len;
                    matched = true;
                    break;
                }
            }

            if (!matched)
                i++; // skip unknown glyph
        }

        return tokens;
    }

    private static IReadOnlyList<WordTiming> BuildWordTimings(
        (int StartFrame, int EndFrame, float LogProb)[] alignment,
        string[] symbols,
        int[]? phonemeWordMap,
        string[]? wordTexts,
        string wordBoundarySymbol)
    {
        if (phonemeWordMap is null || wordTexts is null || wordTexts.Length == 0)
            return NoWords;

        var wordTimings = new List<WordTiming>(wordTexts.Length);
        int wordIdx = -1;
        int wordStart = -1;
        int wordEnd = -1;
        double wordLogProbSum = 0d;
        int wordPhonemeCount = 0;

        void FlushWord()
        {
            if (wordIdx < 0 || wordIdx >= wordTexts.Length || wordStart < 0)
                return;

            TimeSpan start = TimeSpan.FromSeconds(alignment[wordStart].StartFrame * FrameDurationSeconds);
            TimeSpan end = TimeSpan.FromSeconds((alignment[wordEnd].EndFrame + 1) * FrameDurationSeconds);
            double conf = wordPhonemeCount > 0
                ? Math.Clamp(Math.Exp(wordLogProbSum / wordPhonemeCount), 0d, 1d)
                : 0d;
            wordTimings.Add(new WordTiming(wordTexts[wordIdx], start, end, conf));
        }

        for (int i = 0; i < alignment.Length; i++)
        {
            if (symbols[i] == wordBoundarySymbol)
            {
                FlushWord();
                wordIdx++;
                wordStart = -1;
                wordEnd = -1;
                wordLogProbSum = 0d;
                wordPhonemeCount = 0;
                continue;
            }

            int mappedWord = phonemeWordMap[i];
            if (mappedWord != wordIdx)
            {
                FlushWord();
                wordIdx = mappedWord;
                wordStart = -1;
                wordEnd = -1;
                wordLogProbSum = 0d;
                wordPhonemeCount = 0;
            }

            if (wordStart == -1) wordStart = i;
            wordEnd = i;
            wordLogProbSum += alignment[i].LogProb;
            wordPhonemeCount++;
        }

        FlushWord();
        return wordTimings;
    }

    private (Wav2Vec2PhonemeVocab Vocab, InferenceSession Session) EnsureLoaded()
    {
        // Fast path — already loaded
        if (_vocab is not null && _session is not null)
            return (_vocab, _session);

        lock (_loadLock)
        {
            if (_vocab is not null && _session is not null)
                return (_vocab, _session);

            if (_loadFailed)
                throw new InvalidOperationException(
                    $"'{ModelId}' failed to load previously. Check model files at '{_modelRootPath}'.");

            try
            {
                string onnxPath = ResolveOnnxPath(_modelRootPath);
                if (string.IsNullOrEmpty(onnxPath) || !File.Exists(onnxPath))
                {
                    throw new FileNotFoundException(
                        $"ONNX model not found under '{_modelRootPath}'.",
                        onnxPath);
                }

                var vocab = new Wav2Vec2PhonemeVocab(_vocabPath);
                using var opts = new SessionOptions();
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                var session = new InferenceSession(onnxPath, opts);

                _vocab = vocab;
                _session = session;
                return (vocab, session);
            }
            catch
            {
                _loadFailed = true;
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_loadLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private static ForcedAlignmentResult SkippedResult(string segmentId, string reason) =>
        new(segmentId, ForcedAlignmentStatus.Skipped, NoWords, NoPhonemes,
            ZeroConfidence, reason, null, null);

    private static ForcedAlignmentResult FailedResult(string segmentId, string reason) =>
        new(segmentId, ForcedAlignmentStatus.Failed, NoWords, NoPhonemes,
            ZeroConfidence, reason, null, null);

    private sealed class InputSet(IReadOnlyList<NamedOnnxValue> values) : IDisposable
    {
        public IReadOnlyList<NamedOnnxValue> Values { get; } = values;

        public void Dispose()
        {
            foreach (IDisposable value in Values.OfType<IDisposable>())
                value.Dispose();
        }
    }
}
