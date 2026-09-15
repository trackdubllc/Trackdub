using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Composition.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Tts;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.Pipeline;

public sealed class PipelineReadinessServiceTests
{
    [Fact]
    public async Task EvaluateAsync_normalizes_source_language_for_translation_planning()
    {
        string? capturedSourceLanguage = null;
        string? capturedTargetLanguage = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedSourceLanguage = req.SourceLanguage;
                capturedTargetLanguage = req.TargetLanguage;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TranslationModelAlias: "opus-mt-en-es");

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation],
            selections,
            state: null,
            sourceLanguageCode: "pt-BR",
            targetLanguageCode: "es-MX");

        Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, report.Stages[0].Status);
        Assert.Equal("pt", capturedSourceLanguage);
        Assert.Equal("es", capturedTargetLanguage);
    }

    [Fact]
    public async Task EvaluateAsync_uses_planner_for_lip_sync_instead_of_hardcoded_skip()
    {
        int planCalls = 0;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                Assert.Equal(RuntimeStage.LipSync, req.Stage);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSync],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>()),
            state: null);

        Assert.Equal(1, planCalls);
        StageReadiness lipSync = Assert.Single(report.Stages);
        Assert.Equal(StageNames.LipSync, lipSync.StageName);
        Assert.Equal(ReadinessState.Ready, lipSync.Status);
    }

    [Fact]
    public async Task EvaluateAsync_passes_lip_sync_alias_from_selections_to_planner()
    {
        string? capturedAlias = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedAlias = req.PreferredModelAlias;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSync],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
                LipSyncModelAlias: "latentsync-1.6"),
            state: null);

        Assert.Equal("latentsync-1.6", capturedAlias);
        StageReadiness lipSync = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, lipSync.Status);
    }

    [Fact]
    public async Task EvaluateAsync_passes_lip_synthesis_alias_from_selections_to_planner()
    {
        string? capturedAlias = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedAlias = req.PreferredModelAlias;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.LipSynthesis],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
                LipSynthesisModelAlias: "latentsync-1.6"),
            state: null);

        Assert.Equal("latentsync-1.6", capturedAlias);
        StageReadiness lipSynthesis = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Ready, lipSynthesis.Status);
    }

    [Fact]
    public async Task EvaluateAsync_validate_runtime_false_skips_provider_smoke_test()
    {
        var captured = new List<bool>();
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                captured.Add(req.SkipProviderSmokeTest);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        await service.EvaluateAsync(
            [RuntimeStage.Vad], selections, state: null, validateRuntime: false);
        await service.EvaluateAsync(
            [RuntimeStage.Asr], selections, state: null, validateRuntime: true);

        Assert.Equal([true, false], captured);
    }

    [Fact]
    public async Task EvaluateAsync_caches_per_validate_runtime_mode()
    {
        int planCalls = 0;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        // Same stage evaluated in both modes must not share a cache entry.
        await service.EvaluateAsync([RuntimeStage.Vad], selections, state: null, validateRuntime: false);
        await service.EvaluateAsync([RuntimeStage.Vad], selections, state: null, validateRuntime: true);
        await service.EvaluateAsync([RuntimeStage.Vad], selections, state: null, validateRuntime: false);
        await service.EvaluateAsync([RuntimeStage.Vad], selections, state: null, validateRuntime: true);

        Assert.Equal(2, planCalls);
    }

    [Fact]
    public async Task EvaluateAsync_reports_speech_enhancement_under_canonical_stage_name()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
                new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.SpeechEnhancement],
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>()),
            state: null);

        StageReadiness stage = Assert.Single(report.Stages);
        Assert.Equal(StageNames.SpeechEnhancement, stage.StageName);
        Assert.Equal(ReadinessState.Ready, stage.Status);
    }

    [Fact]
    public async Task EvaluateAsync_replans_when_execution_provider_override_changes()
    {
        int planCalls = 0;
        var capturedProviders = new List<ExecutionProviderKind?>();
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                capturedProviders.Add(req.PreferredExecutionProvider);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var cpuSelections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>
            {
                ["Translation"] = ExecutionProviderKind.Cpu,
            });
        var dmlSelections = cpuSelections with
        {
            HardwareOverrides = new Dictionary<string, ExecutionProviderKind>
            {
                ["Translation"] = ExecutionProviderKind.DirectMl,
            },
        };

        await service.EvaluateAsync([RuntimeStage.Translation], cpuSelections, state: null);
        await service.EvaluateAsync([RuntimeStage.Translation], dmlSelections, state: null);
        // A repeated evaluation under the first override reuses its own cache entry.
        await service.EvaluateAsync([RuntimeStage.Translation], cpuSelections, state: null);

        Assert.Equal(2, planCalls);
        Assert.Equal(
            [ExecutionProviderKind.Cpu, ExecutionProviderKind.DirectMl],
            capturedProviders);
    }

    [Fact]
    public async Task EvaluateAsync_replans_when_model_variant_override_changes()
    {
        int planCalls = 0;
        var capturedVariants = new List<string?>();
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                capturedVariants.Add(req.PreferredModelVariantAlias);
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var fp16Selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TranslationModelAlias: "opus-mt-en-es",
            ModelVariantOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelVariantOverrideKeys.Build(StageNames.Translation, "opus-mt-en-es")] = "fp16",
            });
        var int8Selections = fp16Selections with
        {
            ModelVariantOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelVariantOverrideKeys.Build(StageNames.Translation, "opus-mt-en-es")] = "int8",
            },
        };

        await service.EvaluateAsync([RuntimeStage.Translation], fp16Selections, state: null);
        await service.EvaluateAsync([RuntimeStage.Translation], int8Selections, state: null);
        await service.EvaluateAsync([RuntimeStage.Translation], fp16Selections, state: null);

        Assert.Equal(2, planCalls);
        Assert.Equal(["fp16", "int8"], capturedVariants);
    }

    [Fact]
    public async Task EvaluateAsync_reports_unverified_when_smoke_test_skipped_for_non_cpu_provider()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req => new StageRuntimePlan
            {
                Stage = req.Stage,
                Status = StageRuntimePlanStatus.Ready,
                ExecutionProvider = ExecutionProviderKind.DirectMl,
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation], selections, state: null, validateRuntime: false);

        StageReadiness stage = Assert.Single(report.Stages);
        Assert.Equal(ReadinessState.Unverified, stage.Status);
        Assert.False(stage.Status.IsBlocking());
    }

    [Fact]
    public async Task EvaluateAsync_reports_ready_when_smoke_test_skipped_for_cpu_plan()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req => new StageRuntimePlan
            {
                Stage = req.Stage,
                Status = StageRuntimePlanStatus.Ready,
                ExecutionProvider = ExecutionProviderKind.Cpu,
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation], selections, state: null, validateRuntime: false);

        // CPU plans are never smoke-tested, so metadata checks are their full check.
        Assert.Equal(ReadinessState.Ready, Assert.Single(report.Stages).Status);
    }

    [Fact]
    public async Task EvaluateAsync_reports_ready_when_plan_verified_despite_skipped_smoke_test()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req => new StageRuntimePlan
            {
                Stage = req.Stage,
                Status = StageRuntimePlanStatus.Verified,
                ExecutionProvider = ExecutionProviderKind.DirectMl,
            }
        };

        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), new FakeConsentService());
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

        PipelineReadinessReport report = await service.EvaluateAsync(
            [RuntimeStage.Translation], selections, state: null, validateRuntime: false);

        Assert.Equal(ReadinessState.Ready, Assert.Single(report.Stages).Status);
    }

    [Fact]
    public async Task EvaluateAsync_applies_tts_consent_overlay_on_cache_hit()
    {
        int planCalls = 0;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                planCalls++;
                return new StageRuntimePlan { Stage = req.Stage, Status = StageRuntimePlanStatus.Ready };
            }
        };

        var consent = new FakeConsentService();
        var service = new PipelineReadinessService(planner, new NullCloudApiKeyProvider(), consent);
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());
        TranscriptProjectState cloneState = CreateStateWithVoiceCloneAssignment();

        PipelineReadinessReport blocked = await service.EvaluateAsync(
            [RuntimeStage.Tts], selections, cloneState);
        Assert.Equal(ReadinessState.ConsentRequired, Assert.Single(blocked.Stages).Status);

        // Consent granted mid-session must clear the overlay even though the
        // underlying plan is served from cache (no invalidation, no replan).
        consent.GrantVoiceCloningConsent();
        PipelineReadinessReport allowed = await service.EvaluateAsync(
            [RuntimeStage.Tts], selections, cloneState);
        Assert.Equal(ReadinessState.Ready, Assert.Single(allowed.Stages).Status);
        Assert.Equal(1, planCalls);
    }

    private static TranscriptProjectState CreateStateWithVoiceCloneAssignment()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "test", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            projectId,
            "media/source.mp4",
            "source.mp4",
            "abc123",
            1024L,
            now,
            "mp4",
            DurationSeconds: 30.0,
            HasAudio: true,
            HasVideo: true,
            now);
        var openResult = new OpenProjectResult(
            project,
            mediaAsset,
            SourceReference: null,
            SourceMediaStatus.Available,
            SourceStatusMessage: null,
            Artifacts: [],
            TranscriptLanguage: "en");

        VoiceAssignment cloneAssignment = VoiceAssignment.Create(
            projectId,
            Guid.NewGuid(),
            "chatterbox-clone",
            referenceClipArtifactId: Guid.NewGuid());

        return new TranscriptProjectState(
            openResult,
            CurrentTranscriptRevision: null,
            TranscriptSegments: [],
            Speakers: [],
            SpeakerTurns: [],
            CurrentTranslationRevision: null,
            TranslatedSegments: [],
            IsTranslationStale: false,
            TranscriptLanguage: "en",
            StageRuns: [],
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: null,
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [cloneAssignment],
            TtsTakes: [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }

    private sealed class NullCloudApiKeyProvider : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }
}
