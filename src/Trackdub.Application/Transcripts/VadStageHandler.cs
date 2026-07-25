using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed record VadStageRequest(
    Guid ProjectId,
    string NormalizedAudioPath,
    double DurationSeconds,
    string? PreferredModelAlias = null,
    ExecutionProviderKind? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null);

public sealed record VadStageResult(
    StageRunRecord StageRun,
    IReadOnlyList<SpeechRegion> Regions);

public sealed class VadStageHandler(
    ISpeechRegionDetector speechRegionDetector,
    IProjectStageRunStore stageRunStore,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IApplicationLogger? logger = null)
{
    private readonly ISpeechRegionDetector speechRegionDetector = speechRegionDetector ?? throw new ArgumentNullException(nameof(speechRegionDetector));
    private readonly IProjectStageRunStore stageRunStore = stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));

    public async Task<VadStageResult> HandleAsync(
        VadStageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        (StageRunRecord stageRun, IReadOnlyList<SpeechRegion> regions) = await StageRunHelper.RunStageAsync(
                stageRunStore,
                request.ProjectId,
                StageNames.Vad,
                speechRegionDetector,
                async (_, ct) =>
                {
                    return await speechRegionDetector
                        .DetectAsync(
                            new SpeechRegionDetectionRequest(
                                request.NormalizedAudioPath,
                                request.DurationSeconds,
                                new InferenceRequestOptions(
                                    request.PreferredModelAlias,
                                    PreferredExecutionProvider: request.PreferredExecutionProvider?.ToString(),
                                    RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                                    PreferredModelVariantAlias: request.PreferredModelVariantAlias)),
                            ct)
                        .ConfigureAwait(false);
                },
                "VAD canceled.",
                cancellationToken,
                runtimePlanningPreferences,
                logger)
            .ConfigureAwait(false);

        return new VadStageResult(stageRun, regions);
    }
}
