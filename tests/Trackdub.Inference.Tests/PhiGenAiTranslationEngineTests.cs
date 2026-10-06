using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Phi;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Real-model parity suite for <see cref="PhiGenAiTranslationEngine"/>. Before this file the
/// engine had no engine-level tests at all — only routing alias coverage in
/// <c>TranslationLanguageRouterTests</c> and manifest rows — so these are the first tests that
/// drive the real ORT GenAI decoder through the translation contract.
/// </summary>
public sealed class PhiGenAiTranslationEngineTests
{
    /// <summary>Model root the bundled manifest declares for the Phi-3.5 CPU int4 variant.</summary>
    private const string BundledModelRootRelativePath =
        "phi-3.5-mini-genai/cpu_and_mobile/cpu-int4-awq-block-128-acc-level-4";

    private const string GenAiConfigFileName = "genai_config.json";
    private const string OnnxGraphFileName =
        "phi-3.5-mini-instruct-cpu-int4-awq-block-128-acc-level-4.onnx";
    private const string OnnxGraphDataFileName =
        "phi-3.5-mini-instruct-cpu-int4-awq-block-128-acc-level-4.onnx.data";
    private const string TokenizerFileName = "tokenizer.json";
    private const string SpecialTokensMapFileName = "special_tokens_map.json";
    private const string TokenizerConfigFileName = "tokenizer_config.json";

    /// <summary>
    /// Fixed en→fr fixture used by both tests. The sentences are short and the bundled
    /// genai_config.json decodes greedily (top_k = 1), so the fixture is deterministic.
    /// </summary>
    private static readonly TranslationInputSegment[] FixtureSegments =
    [
        new(0, 0.0, 1.0, "Hello, how are you today?"),
        new(1, 1.0, 2.0, "The weather is beautiful.")
    ];

    /// <summary>
    /// French markers for the fixed fixture sentences. The bundled model decodes greedily
    /// (top_k = 1), but phrasing still varies with normalization and grammar choices, so the
    /// tests assert an objective French signal instead of one brittle exact string. Every
    /// marker only occurs in French, so an English echo or paraphrase fails the check.
    /// </summary>
    private static readonly string[] FrenchMarkers =
    [
        "Bonjour",
        "Salut",
        "comment allez-vous",
        "ça va",
        "aujourd",
        "Le temps",
        "la météo",
        "il fait beau"
    ];

    /// <summary>
    /// Staged once per test class from the bundled model root with <c>config.json</c> removed.
    /// ORT GenAI only needs <c>genai_config.json</c>, but a cache-shaped root proves the engine
    /// does not depend on the Hugging Face config the desktop model cache may not carry.
    /// </summary>
    private static readonly Lazy<string> ConfiglessModelRoot = new(StageConfiglessModelRoot);

    [RequiresBundledModelFact(
        BundledModelRootRelativePath + "/" + GenAiConfigFileName,
        BundledModelRootRelativePath + "/" + OnnxGraphFileName,
        BundledModelRootRelativePath + "/" + OnnxGraphDataFileName,
        BundledModelRootRelativePath + "/" + TokenizerFileName,
        BundledModelRootRelativePath + "/" + SpecialTokensMapFileName,
        BundledModelRootRelativePath + "/" + TokenizerConfigFileName)]
    public async Task PhiGenAiTranslationEngine_ConfiglessModelRoot_TranslatesEnglishToFrench()
    {
        string modelRoot = ConfiglessModelRoot.Value;
        Assert.False(
            File.Exists(Path.Join(modelRoot, "config.json")),
            "The staged model root must be config-less to reproduce the cache-shaped layout.");
        Assert.True(File.Exists(Path.Join(modelRoot, "genai_config.json")));

        var logger = new RecordingApplicationLogger();
        PhiGenAiTranslationEngine engine = CreateEngine(modelRoot, logger);

        IReadOnlyList<TranslatedTextSegment> segments = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "fr",
                [FixtureSegments[0]],
                PreferredModelAlias: "phi-3.5-mini-genai",
                ResolvedModelEntryPath: Path.Join(modelRoot, "genai_config.json")),
            CancellationToken.None);

        TranslatedTextSegment segment = Assert.Single(segments);
        Assert.Equal(0, segment.Index);
        AssertFrenchTranslation(segment.Text);
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        Assert.Equal("phi-3.5-mini-genai", engine.LastExecutionSummary.ModelAlias);

        string provenance = Assert.Single(
            logger.InformationMessages,
            message => message.Contains("Phi GenAI model provenance", StringComparison.Ordinal));
        Assert.Contains($"root='{modelRoot}'", provenance, StringComparison.Ordinal);
        Assert.Contains("genai_config.json=present", provenance, StringComparison.Ordinal);
        Assert.Contains("config.json=absent", provenance, StringComparison.Ordinal);
        Assert.Contains("provider=cpu", provenance, StringComparison.Ordinal);
        Assert.Contains("model=microsoft/Phi-3.5-mini-instruct-onnx", provenance, StringComparison.Ordinal);
        Assert.Contains("variant=cpu-int4", provenance, StringComparison.Ordinal);
    }

    [RequiresBundledModelFact(
        BundledModelRootRelativePath + "/" + GenAiConfigFileName,
        BundledModelRootRelativePath + "/" + OnnxGraphFileName,
        BundledModelRootRelativePath + "/" + OnnxGraphDataFileName,
        BundledModelRootRelativePath + "/" + TokenizerFileName,
        BundledModelRootRelativePath + "/" + SpecialTokensMapFileName,
        BundledModelRootRelativePath + "/" + TokenizerConfigFileName)]
    public async Task PhiGenAiTranslationEngine_StreamParity_MatchesBatchPerSegment()
    {
        string modelRoot = ConfiglessModelRoot.Value;
        PhiGenAiTranslationEngine engine = CreateEngine(modelRoot);
        var request = new TranslationRequest(
            "en",
            "fr",
            FixtureSegments,
            PreferredModelAlias: "phi-3.5-mini-genai",
            ResolvedModelEntryPath: Path.Join(modelRoot, "genai_config.json"));

        IReadOnlyList<TranslatedTextSegment> batch =
            await engine.TranslateAsync(request, CancellationToken.None);

        Guid runId = Guid.NewGuid();
        Guid revision = Guid.NewGuid();
        List<PipelineStreamItem<TranslatedTextSegment>> streamed = [];
        await foreach (PipelineStreamItem<TranslatedTextSegment> item in engine.TranslateStreamAsync(
            request, runId, "snap-phi-parity", revision, CancellationToken.None))
        {
            streamed.Add(item);
        }

        Assert.Equal(batch, streamed.Select(static item => item.Payload).ToArray());
        Assert.Equal(FixtureSegments.Length, streamed.Count);
        Assert.Equal([0L, 1L], streamed.Select(static item => item.Identity.Sequence).ToArray());
        Assert.All(streamed, item => AssertFrenchTranslation(item.Payload.Text));
        Assert.All(streamed, item =>
        {
            Assert.Equal(runId, item.Identity.RunId);
            Assert.Equal("snap-phi-parity", item.Identity.SnapshotId);
            Assert.Equal(RuntimeStage.Translation, item.Identity.Stage);
            Assert.Equal(revision, item.Identity.RevisionId);
            Assert.Equal(item.Payload.Index, item.Identity.SegmentIndex);
        });
    }

    private static void AssertFrenchTranslation(string outputText)
    {
        Assert.False(string.IsNullOrWhiteSpace(outputText), "The engine produced an empty translation.");
        Assert.True(
            FrenchMarkers.Any(marker => outputText.Contains(marker, StringComparison.OrdinalIgnoreCase)),
            $"Expected a French translation of the fixture, but generated: '{outputText}'.");
    }

    private static PhiGenAiTranslationEngine CreateEngine(
        string modelRootPath,
        IApplicationLogger? logger = null) =>
        new(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Translation,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "microsoft/Phi-3.5-mini-instruct-onnx",
                ModelAlias = "phi-3.5-mini-genai",
                Variant = "cpu-int4",
                ExecutionProvider = ExecutionProviderKind.Cpu,
                ModelEntryPath = Path.Join(modelRootPath, "genai_config.json"),
                ModelRootPath = modelRootPath
            }),
            BenchmarkModelPathResolver.CreateDefault(),
            runtimePlanningPreferences: null,
            applicationLogger: logger);

    /// <summary>
    /// Copies the bundled model into a staging directory that mirrors a cache-shaped model root:
    /// the genai_config.json, ONNX weights, and tokenizer files only — never the Hugging Face
    /// <c>config.json</c>/<c>generation_config.json</c>. Staged once per test class because the
    /// int4 weights are multi-gigabyte. The directory name is unique per process so concurrent
    /// test hosts (the portable and Windows TFMs, or overlapping test commands) never delete or
    /// overwrite each other's copies, and a self-deregistering process-exit handler cleans it up
    /// best-effort because a pooled GenAI session can still hold the files open.
    /// </summary>
    private static string StageConfiglessModelRoot()
    {
        string bundledRoot = Path.GetFullPath(Path.Join(
            TestRepoRootResolver.FindRepoRoot(), "models", BundledModelRootRelativePath));
        string stagedRoot = Path.Join(
            Path.GetTempPath(), $"trackdub-phi-genai-configless-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagedRoot);
        try
        {
            foreach (string sourcePath in Directory.EnumerateFiles(bundledRoot, "*", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(sourcePath);
                if (fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("generation_config.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destinationPath = Path.Join(stagedRoot, Path.GetRelativePath(bundledRoot, sourcePath));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath);
            }
        }
        catch
        {
            TryDeleteDirectory(stagedRoot);
            throw;
        }

        EventHandler cleanupHandler = null!;
        cleanupHandler = (_, _) =>
        {
            // Deregister so the handler never accumulates, then remove this process's own staging
            // directory (unique per process, so cleanup can never touch another host's copy).
            AppDomain.CurrentDomain.ProcessExit -= cleanupHandler;
            TryDeleteDirectory(stagedRoot);
        };
        AppDomain.CurrentDomain.ProcessExit += cleanupHandler;
        return stagedRoot;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Best-effort cleanup: the directory may already have been removed.
        }
        catch (IOException)
        {
            // Pooled ONNX/GenAI sessions may still hold the staged model files; cleanup is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup: transient file locks or ACLs can block deletion; intentionally ignored.
        }
    }

    private sealed class RecordingApplicationLogger : IApplicationLogger
    {
        public List<string> InformationMessages { get; } = [];

        public void LogDebug(string message)
        {
        }

        public void LogInformation(string message) => InformationMessages.Add(message);

        public void LogWarning(string message, Exception? exception = null)
        {
        }

        public void LogError(string message, Exception? exception = null)
        {
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
