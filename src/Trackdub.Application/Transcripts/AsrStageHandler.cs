using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed record AsrStageRequest(
    Guid ProjectId,
    string AudioPath,
    IReadOnlyList<SpeechRegion> Regions,
    string? PreferredModelAlias = null,
    bool RequirePreferredModelAlias = false,
    string? SourceLanguage = null,
    ExecutionProviderKind? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null);

public sealed record AsrStageResult(
    StageRunRecord StageRun,
    IReadOnlyList<RecognizedTranscriptSegment> Segments,
    DeviceDegradationReport? DeviceDegradation = null);

public sealed class AsrStageHandler(
    IAudioTranscriptionEngine transcriptionEngine,
    IProjectStageRunStore stageRunStore,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IApplicationLogger? logger = null)
{
    private readonly IAudioTranscriptionEngine transcriptionEngine = transcriptionEngine ?? throw new ArgumentNullException(nameof(transcriptionEngine));
    private readonly IProjectStageRunStore stageRunStore = stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));

    public async Task<AsrStageResult> HandleAsync(
        AsrStageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        (StageRunRecord stageRun, IReadOnlyList<RecognizedTranscriptSegment> segments) = await StageRunHelper.RunStageAsync(
                stageRunStore,
                request.ProjectId,
                StageNames.Asr,
                transcriptionEngine,
                async (_, ct) =>
                {
                    return await transcriptionEngine
                        .TranscribeAsync(
                            new AudioTranscriptionRequest(
                                request.AudioPath,
                                request.Regions,
                                new InferenceRequestOptions(
                                    request.PreferredModelAlias,
                                    request.RequirePreferredModelAlias,
                                    request.PreferredExecutionProvider?.ToString(),
                                    request.RequirePreferredExecutionProvider,
                                    request.PreferredModelVariantAlias),
                                request.SourceLanguage),
                            ct)
                        .ConfigureAwait(false);
                },
                "ASR canceled.",
                cancellationToken,
                runtimePlanningPreferences,
                logger)
            .ConfigureAwait(false);

        DeviceDegradationReport? deviceDegradation =
            (transcriptionEngine as IDeviceDegradationReporter)?.LastDeviceDegradation;
        return new AsrStageResult(stageRun, segments, deviceDegradation);
    }
}
