using System.Buffers.Binary;
using System.Reflection;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Madlad;
using Trackdub.Inference.Onnx.OpusMt;
using Trackdub.Inference.Onnx.SileroVad;
using Trackdub.Inference.Onnx.NemotronAsr;
using Trackdub.Inference.Onnx.Qwen3Asr;
using Trackdub.Inference.Onnx.Whisper;
using Trackdub.Inference.Pipelines.Transcript;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

public sealed class OnnxTranscriptEnginesTests
{
    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_BuildTranscriptionRegions_MergesShortNearbyVadRegions()
    {
        IReadOnlyList<SpeechRegion> regions = WhisperOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
            [
                new SpeechRegion(0, 4.03, 4.38),
                new SpeechRegion(1, 4.74, 5.15),
                new SpeechRegion(2, 5.47, 6.01),
                new SpeechRegion(3, 6.59, 6.88),
                new SpeechRegion(4, 8.00, 8.61),
                new SpeechRegion(5, 8.74, 10.30),
                new SpeechRegion(6, 10.47, 10.72),
                new SpeechRegion(7, 12.13, 12.99),
                new SpeechRegion(8, 14.34, 16.06),
                new SpeechRegion(9, 16.26, 16.57)
            ],
            durationSeconds: 20.0);

        SpeechRegion region = Assert.Single(regions);
        Assert.Equal(0, region.Index);
        Assert.Equal(3.53, region.StartSeconds, precision: 2);
        Assert.Equal(17.07, region.EndSeconds, precision: 2);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_BuildTranscriptionRegions_ExpandsIsolatedShortRegions()
    {
        IReadOnlyList<SpeechRegion> regions = WhisperOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
            [new SpeechRegion(8, 10.0, 10.4)],
            durationSeconds: 30.0);

        SpeechRegion region = Assert.Single(regions);
        Assert.Equal(0, region.Index);
        Assert.Equal(3.0, region.EndSeconds - region.StartSeconds, precision: 2);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_BuildTranscriptionRegions_AvoidsOverlappingExpandedRegions()
    {
        IReadOnlyList<SpeechRegion> regions = WhisperOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
            [
                new SpeechRegion(0, 0.0, 0.3),
                new SpeechRegion(1, 2.0, 2.3)
            ],
            durationSeconds: 10.0);

        Assert.NotEmpty(regions);
        for (int index = 1; index < regions.Count; index++)
        {
            Assert.True(regions[index - 1].EndSeconds <= regions[index].StartSeconds);
        }
    }

    [Fact]
    public async Task ScriptedAudioTranscriptionEngine_EmitsWordConfidenceRows()
    {
        var engine = new ScriptedAudioTranscriptionEngine();

        IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
            "normalized.wav",
            [new SpeechRegion(1, 2.0, 5.0)],
            CancellationToken.None);

        RecognizedTranscriptSegment segment = Assert.Single(segments);
        Assert.NotEmpty(segment.Words);
        Assert.All(segment.Words, word =>
        {
            Assert.InRange(word.Confidence!.Value, 0d, 1d);
            Assert.InRange(word.StartSeconds, 2.0, 5.0);
            Assert.InRange(word.EndSeconds, 2.0, 5.0);
        });
        Assert.Contains(segment.Words, word => Math.Abs(word.Confidence!.Value - 0.68d) < 0.0001d);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_BuildRecognizedWords_PreservesTimingAndConfidence()
    {
        IReadOnlyList<RecognizedTranscriptWord> words = WhisperOnnxAudioTranscriptionEngine.BuildRecognizedWordsForTesting(
            "hello world",
            10.0d,
            12.0d,
            [0.80d, 0.60d]);

        Assert.Collection(
            words,
            first =>
            {
                Assert.Equal(0, first.WordIndex);
                Assert.Equal("hello", first.Text);
                Assert.Equal(10.0d, first.StartSeconds);
                Assert.Equal(11.0d, first.EndSeconds);
                Assert.Equal(0.70d, first.Confidence!.Value, precision: 3);
            },
            second =>
            {
                Assert.Equal(1, second.WordIndex);
                Assert.Equal("world", second.Text);
                Assert.Equal(11.0d, second.StartSeconds);
                Assert.Equal(12.0d, second.EndSeconds);
                Assert.Equal(0.70d, second.Confidence!.Value, precision: 3);
            });
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_RepetitionGuard_TrimsRunawayTail()
    {
        (IReadOnlyList<int> tokens, bool repetitionGuarded) =
            WhisperOnnxAudioTranscriptionEngine.ApplyRepetitionGuardForTesting(
                [9, 8, 7, 1, 2, 3, 1, 2, 3, 1, 2, 3, 1, 2, 3]);

        Assert.True(repetitionGuarded);
        Assert.Equal([9, 8, 7, 1, 2, 3], tokens);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_RepetitionGuard_LeavesNonTailShortRepeats()
    {
        (IReadOnlyList<int> tokens, bool repetitionGuarded) =
            WhisperOnnxAudioTranscriptionEngine.ApplyRepetitionGuardForTesting(
                [9, 8, 7, 1, 2, 3, 1, 2, 3, 1, 2, 3, 42]);

        Assert.False(repetitionGuarded);
        Assert.Equal([9, 8, 7, 1, 2, 3, 1, 2, 3, 1, 2, 3, 42], tokens);
    }

    [Fact]
    public void WhisperOnnxAudioTranscriptionEngine_RepetitionGuard_DetectsLargeNgramLoop()
    {
        // 15-token pattern repeated twice — too large for the original 3..8 ngram guard.
        int[] pattern = [101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115];

        (IReadOnlyList<int> tokens, bool repetitionGuarded) =
            WhisperOnnxAudioTranscriptionEngine.ApplyRepetitionGuardForTesting([.. pattern, .. pattern]);

        Assert.True(repetitionGuarded);
        Assert.Equal(pattern, tokens);
    }

#if WINDOWS
    [Fact]
    public void WhisperGenAiAudioTranscriptionEngine_CleansSpecialTokensAndInfersLanguage()
    {
        const string decoded = "<|startoftranscript|><|en|><|transcribe|><|notimestamps|> hello world <|endoftext|>";

        Assert.Equal("hello world", WhisperGenAiAudioTranscriptionEngine.CleanDecodedText(decoded));
        Assert.Equal("en", WhisperGenAiAudioTranscriptionEngine.TryInferDetectedLanguage(decoded));
    }

    [Fact]
    public void WhisperGenAiAudioTranscriptionEngine_InfersLanguageFromTokenIds()
    {
        // ONNX Runtime GenAI strips special tokens when decoding to text, so the spoken
        // language must be read from the raw token-id sequence. <|startoftranscript|> (50258)
        // is not a language token and must be skipped so the first real language token wins.
        var languageTokensById = new Dictionary<int, string>
        {
            [50259] = "en",
            [50262] = "es",
        };

        Assert.Equal(
            "es",
            WhisperGenAiAudioTranscriptionEngine.InferLanguageFromTokenIds(
                [50258, 50262, 50359, 1000, 2000],
                languageTokensById));
        Assert.Null(
            WhisperGenAiAudioTranscriptionEngine.InferLanguageFromTokenIds(
                [50258, 50359, 1000],
                languageTokensById));
    }

    [Fact]
    public void WhisperGenAiAudioTranscriptionEngine_UsesRequiredAudioProcessorPrompt()
    {
        Assert.Equal(
            "<|startoftranscript|><|transcribe|><|notimestamps|>",
            WhisperGenAiAudioTranscriptionEngine.GetAudioProcessorPromptForTesting());
        Assert.Equal(
            "<|startoftranscript|>",
            WhisperGenAiAudioTranscriptionEngine.GetLanguageDetectionPromptForTesting());
    }

    [Theory]
    [InlineData("es", "<|startoftranscript|><|es|><|transcribe|><|notimestamps|>")]
    [InlineData("ES", "<|startoftranscript|><|es|><|transcribe|><|notimestamps|>")]
    [InlineData(null, "<|startoftranscript|><|transcribe|><|notimestamps|>")]
    [InlineData("", "<|startoftranscript|><|transcribe|><|notimestamps|>")]
    public void WhisperGenAiAudioTranscriptionEngine_BuildsSourceLanguageTranscriptionPrompt(
        string? detectedLanguage,
        string expectedPrompt)
    {
        Assert.Equal(
            expectedPrompt,
            WhisperGenAiAudioTranscriptionEngine.BuildTranscriptionPromptForTesting(detectedLanguage));
    }

    [RequiresBundledModelFact("whisper-tiny-genai/genai_config.json", "whisper-tiny-genai/audio_processor_config.json", "whisper-tiny-genai/encoder.onnx", "whisper-tiny-genai/decoder.onnx")]
    public async Task WhisperGenAiAudioTranscriptionEngine_RunsBundledModelOnSilence()
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var engine = new WhisperGenAiAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "openai/whisper-tiny",
                    ModelAlias = "whisper-tiny-genai",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [ new SpeechRegion(0, 0.0, 0.8) ],
                CancellationToken.None);

            RecognizedTranscriptSegment segment = Assert.Single(segments);
            Assert.Equal(0, segment.Index);
            Assert.Equal(0.0, segment.StartSeconds);
            Assert.Equal(0.8, segment.EndSeconds);
            Assert.NotNull(engine.LastExecutionSummary);
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
            Assert.Equal("whisper-tiny-genai", engine.LastExecutionSummary.ModelAlias);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [RequiresBundledModelFact("whisper-tiny-genai/tokenizer.json")]
    public async Task WhisperGenAiAudioTranscriptionEngine_LoadsLanguageTokenIdsFromBundledTokenizer()
    {
        string modelRoot = Path.Combine(TestRepoRootResolver.FindRepoRoot(), "models", "whisper-tiny-genai");

        IReadOnlyDictionary<int, string> languageTokensById =
            await WhisperGenAiAudioTranscriptionEngine.LoadLanguageTokenIdsAsync(modelRoot);

        // Real-model guard for token-id-based language detection: if this id<->code mapping ever
        // drifts (different model export, vocab change), auto-detect would silently mislabel the
        // language. These ids are the canonical whisper language tokens (<|en|>=50259, <|es|>=50262).
        Assert.Equal("en", languageTokensById[50259]);
        Assert.Equal("es", languageTokensById[50262]);
        Assert.True(
            languageTokensById.Count >= 90,
            $"Expected ~99 whisper language tokens, found {languageTokensById.Count}.");
    }
#endif

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task SileroVadSpeechRegionDetector_RunsBundledModelOnSilence()
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var detector = new SileroVadSpeechRegionDetector(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Vad,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "onnx-community/silero-vad",
                    ModelAlias = "silero-vad",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<SpeechRegion> regions = await detector.DetectAsync(wavePath, 1.0, CancellationToken.None);

            Assert.NotNull(regions);
            Assert.NotNull(detector.LastExecutionSummary);
            Assert.Equal("cpu", detector.LastExecutionSummary!.SelectedProvider);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [Fact]
    public void Qwen3AsrFeatureLengths_MatchesReferenceFormula()
    {
        Assert.Equal(1, Qwen3AsrFeatureLengths.GetEncoderOutputLength(1));
        Assert.Equal(13, Qwen3AsrFeatureLengths.GetEncoderOutputLength(100));
        Assert.Equal(26, Qwen3AsrFeatureLengths.GetEncoderOutputLength(200));
    }

    [Fact]
    public void Qwen3AsrOutputParser_ParsesLanguageTaggedOutput()
    {
        // Real Qwen3-ASR auto-detect output: the language metadata line plus the
        // "<asr_text>" marker (no pipes — see Qwen3AsrPromptTokens.AsrTextTag).
        (string language, string text) = Qwen3AsrOutputParser.Parse(
            "language English<asr_text>Hello world");

        Assert.Equal("English", language);
        Assert.Equal("Hello world", text);
        Assert.Equal("en", Qwen3AsrLanguageCodes.TryGetIsoCode(language));
    }

    [Fact]
    public void Qwen3AsrOutputParser_DoesNotLeakLanguageMetadataIntoText()
    {
        // Regression guard for the "<asr_text>" tag-format bug: the metadata prefix and
        // marker must never appear in the transcript text shown to the user.
        (_, string text) = Qwen3AsrOutputParser.Parse(
            "language Chinese<asr_text>对。");

        Assert.DoesNotContain("language", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asr_text", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("对。", text);
    }

    [Fact]
    public void Qwen3AsrOutputParser_StripsTrailingEosTagOnForcedLanguagePath()
    {
        // Forced-language path returns the decode verbatim; the greedy decoder appends the
        // terminal EOS token which tokenizer.Decode renders as literal "<|im_end|>".
        (string language, string text) = Qwen3AsrOutputParser.Parse(
            "Hello world<|im_end|>", forcedLanguageName: "English");

        Assert.Equal("English", language);
        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void Qwen3AsrOutputParser_StripsControlTokensFromTaggedOutput()
    {
        (string language, string text) = Qwen3AsrOutputParser.Parse(
            "language English<asr_text>Hello world<|endoftext|>");

        Assert.Equal("English", language);
        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void NemotronAsrLanguagePrompts_ResolveSupportedLanguagesAndFallbackToAuto()
    {
        Assert.Equal(0, NemotronAsrLanguagePrompts.ResolvePromptIndex("en-US"));
        Assert.Equal(2, NemotronAsrLanguagePrompts.ResolvePromptIndex("es-ES"));
        Assert.Equal(NemotronAsrLanguagePrompts.AutoPromptIndex, NemotronAsrLanguagePrompts.ResolvePromptIndex("zz-ZZ"));
        Assert.Equal("en", NemotronAsrLanguagePrompts.TryGetIsoCode("en-US"));
        Assert.Null(NemotronAsrLanguagePrompts.TryGetIsoCode("zz-ZZ"));
    }

    [Fact]
    public async Task NemotronAsrLanguagePrompts_LoadsBundlePromptDictionary()
    {
        string configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  "prompt_dictionary": {
                    "auto": 101,
                    "id-ID": 34,
                    "fr-CA": 100
                  }
                }
                """,
                CancellationToken.None);

            NemotronAsrPromptDictionary promptDictionary = await NemotronAsrLanguagePrompts
                .LoadAsync(configPath, CancellationToken.None);

            Assert.Equal(101, promptDictionary.AutoPromptIndex);
            Assert.Equal(34, promptDictionary.ResolvePromptIndex("id"));
            Assert.Equal(100, promptDictionary.ResolvePromptIndex("fr_CA"));
            Assert.Equal(101, promptDictionary.ResolvePromptIndex("zz-ZZ"));
            Assert.Equal("id", promptDictionary.TryGetIsoCode("id"));
            Assert.Null(promptDictionary.TryGetIsoCode("zz-ZZ"));
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task NemotronAsrLanguagePrompts_LoadsAutoPromptIndexFromDictionary()
    {
        string configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  "prompt_dictionary": {
                    "auto": 999,
                    "en-US": 0
                  }
                }
                """,
                CancellationToken.None);

            NemotronAsrPromptDictionary promptDictionary = await NemotronAsrLanguagePrompts
                .LoadAsync(configPath, CancellationToken.None);

            Assert.Equal(999, promptDictionary.AutoPromptIndex);
            Assert.Equal(999, promptDictionary.ResolvePromptIndex(null));
            Assert.Equal(999, promptDictionary.ResolvePromptIndex("zz-ZZ"));
            Assert.Null(promptDictionary.TryGetIsoCode("zz-ZZ"));
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public void NemotronAsrEncoderTrtProfiles_OmitsPromptIndexWhenEncoderDoesNotExposeIt()
    {
        IReadOnlyDictionary<string, string> options = NemotronAsrEncoderTrtProfiles.BuildOptions(hasPromptInput: false);

        Assert.DoesNotContain("prompt_index", options["trt_profile_min_shapes"], StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_index", options["trt_profile_max_shapes"], StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_index", options["trt_profile_opt_shapes"], StringComparison.Ordinal);
    }

    [Fact]
    public void NemotronAsrEncoderTrtProfiles_IncludesPromptIndexWhenEncoderExposesIt()
    {
        IReadOnlyDictionary<string, string> options = NemotronAsrEncoderTrtProfiles.BuildOptions(hasPromptInput: true);

        Assert.Contains("prompt_index:1", options["trt_profile_min_shapes"], StringComparison.Ordinal);
        Assert.Contains("prompt_index:1", options["trt_profile_max_shapes"], StringComparison.Ordinal);
        Assert.Contains("prompt_index:1", options["trt_profile_opt_shapes"], StringComparison.Ordinal);
    }

    [RequiresBundledModelFact(
        "nemotron-3.5-asr-onnx/encoder.onnx")]
    public void NemotronAsrEncoderTrtProfiles_MatchesBundledEncoderPromptInput()
    {
        string encoderPath = BenchmarkModelPathResolver.CreateDefault()
            .ResolveSingle("nemotron-3.5-asr-onnx/encoder.onnx")
            .ModelPath;

        bool hasPromptInput = NemotronAsrEncoderTrtProfiles.EncoderHasPromptInput(encoderPath);
        IReadOnlyDictionary<string, string> options = NemotronAsrEncoderTrtProfiles.BuildOptions(encoderPath);

        if (hasPromptInput)
        {
            Assert.Contains("prompt_index:1", options["trt_profile_opt_shapes"], StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("prompt_index", options["trt_profile_opt_shapes"], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NemotronAsrMelFeatureExtractor_ProducesChunkableMelFrames()
    {
        var extractor = new NemotronAsrMelFeatureExtractor();
        float[,] mel = extractor.Extract(new float[16000]);

        Assert.Equal(NemotronAsrMelFeatureExtractor.MelBins, mel.GetLength(0));
        Assert.True(mel.GetLength(1) > NemotronAsrMelFeatureExtractor.ChunkFrames);

        float[] chunk = extractor.BuildChunk(
            mel,
            frameOffset: 0,
            mainFrameCount: NemotronAsrMelFeatureExtractor.ChunkFrames,
            includePreEncodeCache: false);

        Assert.Equal(
            NemotronAsrMelFeatureExtractor.MelBins * NemotronAsrMelFeatureExtractor.ChunkInputFrames,
            chunk.Length);
    }

    [Fact]
    public void NemotronAsrOnnxAudioTranscriptionEngine_PreservesCallerSpeechRegions()
    {
        IReadOnlyList<SpeechRegion> regions = NemotronAsrOnnxAudioTranscriptionEngine.BuildTranscriptionRegionsForTesting(
        [
            new SpeechRegion(42, 10.0, 10.8),
            new SpeechRegion(7, 1.5, 2.2),
            new SpeechRegion(99, 5.0, 5.0),
        ]);

        Assert.Collection(
            regions,
            first =>
            {
                Assert.Equal(7, first.Index);
                Assert.Equal(1.5, first.StartSeconds);
                Assert.Equal(2.2, first.EndSeconds);
            },
            second =>
            {
                Assert.Equal(42, second.Index);
                Assert.Equal(10.0, second.StartSeconds);
                Assert.Equal(10.8, second.EndSeconds);
            });
    }

    [RequiresBundledModelFact(
        "nemotron-3.5-asr-onnx/config.json",
        "nemotron-3.5-asr-onnx/encoder.onnx",
        "nemotron-3.5-asr-onnx/encoder.onnx.data",
        "nemotron-3.5-asr-onnx/decoder_joint.onnx",
        "nemotron-3.5-asr-onnx/tokenizer.model")]
    public async Task NemotronAsrOnnxAudioTranscriptionEngine_RunsBundledModelOnSilence()
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var engine = new NemotronAsrOnnxAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx",
                    ModelAlias = "nemotron-3.5-asr",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu,
                    EngineFamily = NemotronAsrOnnxAudioTranscriptionEngine.EngineFamilyName,
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [new SpeechRegion(0, 0.0, 0.8)],
                CancellationToken.None);

            _ = segments;
            Assert.NotNull(engine.LastExecutionSummary);
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [RequiresBundledModelFact(
        "qwen3-asr-0.6b-onnx/encoder.onnx",
        "qwen3-asr-0.6b-onnx/decoder_init.onnx",
        "qwen3-asr-0.6b-onnx/decoder_step.onnx",
        "qwen3-asr-0.6b-onnx/embed_tokens.bin",
        "qwen3-asr-0.6b-onnx/tokenizer.json")]
    public async Task Qwen3AsrOnnxAudioTranscriptionEngine_RunsBundledModelOnSilence()
    {
        await RunQwen3AsrSilenceSmokeAsync("qwen3-asr-0.6b", "tonythethompson/qwen3-asr-0.6b-onnx").ConfigureAwait(false);
    }

    [RequiresBundledModelFact(
        "qwen3-asr-1.7b-onnx/encoder.onnx",
        "qwen3-asr-1.7b-onnx/decoder_init.onnx",
        "qwen3-asr-1.7b-onnx/decoder_step.onnx",
        "qwen3-asr-1.7b-onnx/embed_tokens.bin",
        "qwen3-asr-1.7b-onnx/tokenizer.json")]
    public async Task Qwen3AsrOnnxAudioTranscriptionEngine_RunsBundled1Point7BModelOnSilence()
    {
        await RunQwen3AsrSilenceSmokeAsync("qwen3-asr-1.7b", "tonythethompson/qwen3-asr-1.7b-onnx").ConfigureAwait(false);
    }

    private static async Task RunQwen3AsrSilenceSmokeAsync(string modelAlias, string modelId)
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var engine = new Qwen3AsrOnnxAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = modelId,
                    ModelAlias = modelAlias,
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu,
                    EngineFamily = Qwen3AsrOnnxAudioTranscriptionEngine.EngineFamilyName,
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [new SpeechRegion(0, 0.0, 0.8)],
                CancellationToken.None);

            _ = segments;
            Assert.NotNull(engine.LastExecutionSummary);
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [RequiresBundledModelFact("whisper-tiny-onnx/onnx/encoder_model.onnx", "whisper-tiny-onnx/onnx/decoder_model.onnx", "whisper-tiny-onnx/vocab.json", "whisper-tiny-onnx/config.json")]
    public async Task WhisperOnnxAudioTranscriptionEngine_RunsBundledModelOnSilence()
    {
        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            var engine = new WhisperOnnxAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "onnx-community/whisper-tiny",
                    ModelAlias = "whisper-tiny-onnx",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu
                }),
                BenchmarkModelPathResolver.CreateDefault());

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [new SpeechRegion(0, 0.0, 0.8)],
                CancellationToken.None);

            RecognizedTranscriptSegment segment = Assert.Single(segments);
            Assert.Equal(0, segment.Index);
            Assert.NotNull(engine.LastExecutionSummary);
            Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        }
        finally
        {
            if (File.Exists(wavePath))
            {
                File.Delete(wavePath);
            }
        }
    }

    [RequiresBundledModelFact("opus/onnx-community-opus-mt-en-es")]
    public async Task OpusMtTranslationEngine_TranslatesBundledEnglishToSpanishSentence()
    {
        var engine = new OpusMtTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/opus-mt-en-es",
                ModelAlias = "opus-en-es",
                Variant = "merged-decoder",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [new TranslationInputSegment(0, 0.0, 1.0, "Hello, I am Brenna Romaniello, your Spanish teacher from Ole Spanish.")],
                PreferredModelAlias: "opus-en-es"),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.Equal(0, segment.Index);
        Assert.Equal("Hola, soy Brenna Romaniello, tu profesora de español de Ole Spanish.", segment.Text);
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        Assert.Equal("opus-en-es", engine.LastExecutionSummary.ModelAlias);
        Assert.Equal("merged-decoder", engine.LastExecutionSummary.ModelVariant);
    }

    [RequiresBundledModelFact("opus/onnx-community-opus-mt-es-en")]
    public async Task OpusMtTranslationEngine_TranslatesBundledSpanishToEnglishSentence()
    {
        var engine = new OpusMtTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/opus-mt-es-en",
                ModelAlias = "opus-es-en",
                Variant = "merged-decoder",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "es",
                "en",
                [new TranslationInputSegment(0, 0.0, 1.0, "Hola, soy Brenna Romaniello, tu profesora de español de Ole Spanish.")],
                PreferredModelAlias: "opus-es-en"),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.Equal(0, segment.Index);
        Assert.Equal("Hi, I'm Brenna Romaniello, your Spanish teacher at Ole Spanish.", segment.Text);
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        Assert.Equal("opus-es-en", engine.LastExecutionSummary.ModelAlias);
        Assert.Equal("merged-decoder", engine.LastExecutionSummary.ModelVariant);
    }

    [FixtureFact("TRACKDUB_OPUS_FIXTURE_ROOT", "encoder_model.onnx")]
    [Trait("Category", "Integration")]
    public async Task OpusMtTranslationEngine_UsesFixtureModelWhenProvided()
    {
        string fixtureRoot = RequireFixtureRoot("TRACKDUB_OPUS_FIXTURE_ROOT");
        string encoderModelPath = RequireFixtureFile(fixtureRoot, "encoder_model.onnx");

        var engine = new OpusMtTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "fixture/opus-mt",
                ModelAlias = "fixture-opus-mt",
                Variant = "merged-decoder",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [new TranslationInputSegment(0, 0.0, 1.0, "Hello world.")],
                PreferredModelAlias: "fixture-opus-mt",
                ResolvedModelEntryPath: encoderModelPath),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.False(string.IsNullOrWhiteSpace(segment.Text));
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
    }

    [FixtureFact("TRACKDUB_MADLAD_FIXTURE_ROOT", "encoder_model_int8.onnx")]
    [Trait("Category", "Integration")]
    public async Task MadladTranslationEngine_UsesFixtureModelWhenProvided()
    {
        string fixtureRoot = RequireFixtureRoot("TRACKDUB_MADLAD_FIXTURE_ROOT");
        string encoderModelPath = RequireFixtureFile(fixtureRoot, "encoder_model_int8.onnx");

        var engine = new MadladTranslationEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "fixture/madlad400",
                ModelAlias = "fixture-madlad400",
                Variant = "int8",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "fr",
                [new TranslationInputSegment(0, 0.0, 1.0, "Hello world.")],
                PreferredModelAlias: "fixture-madlad400",
                ResolvedModelEntryPath: encoderModelPath),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.False(string.IsNullOrWhiteSpace(segment.Text));
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
    }

    [Fact]
    public void MadladTranslationEngine_ResolveEncoderModelPath_prefers_planner_model_entry_path()
    {
        string tempRoot = Directory.CreateTempSubdirectory("trackdub-madlad-plan-entry-").FullName;
        try
        {
            string defaultEncoderPath = Path.Combine(tempRoot, "encoder_model.onnx");
            string selectedRoot = Path.Combine(tempRoot, "optimized", "olive-cpu-fp32");
            string selectedEncoderPath = Path.Combine(selectedRoot, "encoder_model_int8.onnx");
            Directory.CreateDirectory(selectedRoot);
            File.WriteAllBytes(defaultEncoderPath, [1]);
            File.WriteAllBytes(selectedEncoderPath, [2]);
            var engine = new MadladTranslationEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Translation,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "fixture/madlad400",
                    ModelAlias = tempRoot,
                    ExecutionProvider = ExecutionProviderKind.Cpu,
                    ModelEntryPath = selectedEncoderPath
                }),
                new BenchmarkModelPathResolver());
            MethodInfo method = typeof(MadladTranslationEngine).GetMethod(
                "ResolveEncoderModelPath",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            string resolved = (string)method.Invoke(
                engine,
                [
                    new StageRuntimePlan
                    {
                        Stage = RuntimeStage.Translation,
                        Status = StageRuntimePlanStatus.Ready,
                        ModelId = "fixture/madlad400",
                        ModelAlias = tempRoot,
                        ExecutionProvider = ExecutionProviderKind.Cpu,
                        ModelEntryPath = selectedEncoderPath
                    },
                    null
                ])!;

            Assert.Equal(Path.GetFullPath(selectedEncoderPath), resolved);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateSilenceWaveFile(double durationSeconds)
    {
        const int sampleRate = 48000;
        const short channelCount = 1;
        const short bitsPerSample = 16;
        int sampleCount = Math.Max(1, (int)Math.Round(durationSeconds * sampleRate));
        int blockAlign = channelCount * (bitsPerSample / 8);
        int dataLength = sampleCount * blockAlign;
        string path = Path.Combine(Path.GetTempPath(), $"trackdub-onnx-silence-{Guid.NewGuid():N}.wav");

        byte[] buffer = new byte[44 + dataLength];
        WriteAscii(buffer, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), 36 + dataLength);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22, 2), channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34, 2), bitsPerSample);
        WriteAscii(buffer, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40, 4), dataLength);

        File.WriteAllBytes(path, buffer);
        return path;
    }

    private static void WriteAscii(byte[] buffer, int offset, string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            buffer[offset + index] = (byte)text[index];
        }
    }

    private static string RequireFixtureRoot(string environmentVariableName)
    {
        string? fixtureRoot = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(fixtureRoot) || !Directory.Exists(fixtureRoot))
        {
            throw new InvalidOperationException($"Set {environmentVariableName} to a fixture model directory to run this integration test.");
        }

        return fixtureRoot;
    }

    private static string RequireFixtureFile(string fixtureRoot, string relativePath)
    {
        string fullPath = Path.Combine(fixtureRoot, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Fixture file '{relativePath}' was not found under '{fixtureRoot}'.", fullPath);
        }

        return fullPath;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class FixtureFactAttribute : FactAttribute
    {
        public FixtureFactAttribute(string environmentVariableName, string requiredRelativePath)
        {
            string? fixtureRoot = Environment.GetEnvironmentVariable(environmentVariableName);
            if (string.IsNullOrWhiteSpace(fixtureRoot) || !Directory.Exists(fixtureRoot))
            {
                Skip = $"Set {environmentVariableName} to a fixture model directory to run this integration test.";
                return;
            }

            string requiredPath = Path.Combine(fixtureRoot, requiredRelativePath);
            if (!File.Exists(requiredPath))
            {
                Skip = $"Fixture file '{requiredRelativePath}' was not found under '{fixtureRoot}'.";
            }
        }
    }

    private sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan with
            {
                Stage = request.Stage
            });
    }
}
