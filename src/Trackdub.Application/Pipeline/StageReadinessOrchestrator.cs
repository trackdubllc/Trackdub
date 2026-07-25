using Trackdub.Application.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Maps pipeline stage keys to <see cref="RuntimeModelSetupCoordinator"/> ensure calls.
/// </summary>
public sealed class StageReadinessOrchestrator(RuntimeModelSetupCoordinator coordinator)
    : IStageReadinessOrchestrator
{
    private readonly RuntimeModelSetupCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<RuntimeModelSetupResult> ProvisionStageAsync(
        StageReadinessProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.StageKey switch
        {
            StageNames.Vad or StageNames.Asr =>
                _coordinator.EnsureAsrModelAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    request.Callbacks,
                    cancellationToken,
                    request.SourceLanguageCode),
            StageNames.Diarization =>
                _coordinator.EnsureDiarizationModelAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    request.Callbacks,
                    cancellationToken),
            StageNames.Translation =>
                _coordinator.EnsureTranslationModelAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    request.SourceLanguageCode ?? string.Empty,
                    request.TargetLanguageCode ?? string.Empty,
                    request.Callbacks,
                    cancellationToken),
            StageNames.Tts =>
                _coordinator.EnsureTtsModelAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    request.RequiresVoiceClone,
                    request.Callbacks,
                    cancellationToken),
            StageNames.Separation =>
                _coordinator.EnsureStemRerunModelsAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    [RuntimeStage.Separation],
                    request.Callbacks,
                    cancellationToken,
                    request.SourceLanguageCode),
            StageNames.OverlapRescue =>
                _coordinator.EnsureOverlapRescueModelAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    request.Callbacks,
                    cancellationToken),
            StageNames.TextRefinementAsr =>
                _coordinator.EnsureTextRefinementModelAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    request.Callbacks,
                    cancellationToken),
            StageNames.LipSync =>
                _coordinator.EnsureLipSyncModelAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    request.Callbacks,
                    request.LipSyncModelAlias,
                    cancellationToken),
            StageNames.LipSynthesis =>
                _coordinator.EnsureLipSynthesisModelsAvailableAsync(
                    request.Workspace,
                    request.Selections,
                    request.Callbacks,
                    request.LipSynthesisModelAlias,
                    cancellationToken),
            _ => Task.FromResult(new RuntimeModelSetupResult(IsReady: false, [])),
        };
    }
}
