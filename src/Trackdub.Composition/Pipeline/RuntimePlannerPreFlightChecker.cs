using Trackdub.Contracts.Licensing;
using Trackdub.Application.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Pipeline;

public sealed class RuntimePlannerPreFlightChecker(
    IRuntimePlanner runtimePlanner,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null) : IPipelinePreFlightChecker
{
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));

    public async Task EnsureModelsAvailableAsync(
        string stageName,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null)
    {
        RuntimeStage? stage = MapStageNameToRuntimeStage(stageName);
        if (stage is null)
        {
            return;
        }

        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                stage.Value,
                SourceLanguage: string.Equals(stageName, StageNames.Asr, StringComparison.OrdinalIgnoreCase)
                    ? sourceLanguageCode
                    : null),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);

        if (plan.Status is StageRuntimePlanStatus.Blocked or StageRuntimePlanStatus.DownloadRequired)
        {
            bool canAutoDownload = plan.Status == StageRuntimePlanStatus.DownloadRequired;
            string detail = canAutoDownload
                ? "model is not cached locally"
                : plan.Fallback is { } fallback
                    ? $"{fallback.Code}{(fallback.Detail is not null ? $": {fallback.Detail}" : string.Empty)}"
                    : "no compatible model or execution provider found";

            throw new RequiredModelNotAvailableException(
                modelId: $"{stageName} ({detail})",
                modelPath: plan.ModelId ?? stageName,
                canAutoDownload: canAutoDownload);
        }
    }

    private static RuntimeStage? MapStageNameToRuntimeStage(string stageName) =>
        stageName switch
        {
            StageNames.Vad => RuntimeStage.Vad,
            StageNames.Asr => RuntimeStage.Asr,
            StageNames.Diarization => RuntimeStage.Diarization,
            StageNames.Translation => RuntimeStage.Translation,
            StageNames.Tts => RuntimeStage.Tts,
            StageNames.Separation => RuntimeStage.Separation,
            StageNames.TextRefinementAsr => RuntimeStage.TextRefinement,
            _ => null
        };
}
