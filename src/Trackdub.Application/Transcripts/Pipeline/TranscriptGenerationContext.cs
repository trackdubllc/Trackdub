using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Transcript;

namespace Trackdub.Application.Transcripts.Pipeline;

public sealed record TranscriptGenerationContext
{
    public TrackdubProject Project { get; }
    public MediaAsset MediaAsset { get; }
    public ProjectArtifact NormalizedAudioArtifact { get; }
    public TranscriptAudioRoutingPlan AudioRoutingPlan { get; init; }
    public bool EnableSpeakerDiarization { get; }
    public InferenceModelPreferences ModelPreferences { get; }
    public string? SourceLanguage { get; }

    private SpeechRegion[] _speechRegions = [];
    public IReadOnlyList<SpeechRegion> SpeechRegions
    {
        get => _speechRegions;
        init => _speechRegions = value is SpeechRegion[] arr ? [.. arr] : [.. value];
    }

    public Guid? VadStageRunId { get; init; }

    public DiarizationResult? DiarizationResult { get; init; }

    public TranscriptRegionPlan? RegionPlan { get; init; }

    public AsrStageResult? AsrResult { get; init; }

    public TextRefinementStageResult? TextRefinementResult { get; init; }

    public SpeakerAssignmentResult? SpeakerAssignment { get; init; }

    /// <summary>
    /// Optional per-stage progress reporter. Stages emit StageProgressReport events
    /// per segment or region; the pipeline aggregates them into PipelineProgressEvent(Progress).
    /// Null when no progress reporting is wired (e.g. headless auto-runs).
    /// </summary>
    public IProgress<StageProgressReport>? StageProgress { get; init; }

    /// <summary>
    /// The enhanced audio artifact produced by <see cref="Stages.SpeechEnhancementGenerationStage"/>.
    /// When non-null, downstream stages should prefer this over the original VAD source.
    /// </summary>
    public ProjectArtifact? EnhancedAudioArtifact { get; init; }

    /// <summary>
    /// Execution snapshot capturing model aliases, variants, and source language for resume validation.
    /// Populated at pipeline start and used by <see cref="StageArtifactResumeEvaluator"/> to determine
    /// if a stage can be skipped based on matching runtime configuration.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExecutionSnapshot { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Loaded project state for resume evaluation. Null when resume is unavailable.
    /// </summary>
    public TranscriptProjectState? ProjectState { get; init; }

    /// <summary>
    /// Project root path for artifact existence checks during resume evaluation.
    /// </summary>
    public string? ProjectRootPath { get; init; }

    /// <summary>
    /// When true, all stages execute even when valid resumable artifacts exist.
    /// </summary>
    public bool ForceRerun { get; init; }

    public TranscriptGenerationContext(
        TrackdubProject project,
        MediaAsset mediaAsset,
        ProjectArtifact normalizedAudioArtifact,
        TranscriptAudioRoutingPlan audioRoutingPlan,
        bool enableSpeakerDiarization,
        string? sourceLanguage,
        InferenceModelPreferences? modelPreferences = null)
    {
        Project = project;
        MediaAsset = mediaAsset;
        NormalizedAudioArtifact = normalizedAudioArtifact;
        AudioRoutingPlan = audioRoutingPlan;
        EnableSpeakerDiarization = enableSpeakerDiarization;
        SourceLanguage = sourceLanguage;
        ModelPreferences = modelPreferences ?? InferenceModelPreferences.Empty;
    }
}
