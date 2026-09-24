using Trackdub.Application.Pipeline;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Ensures VAD/ASR models are provisioned before transcription runs. Separation is not a
/// transcript prerequisite; its model is provisioned by the separation stage itself.
/// </summary>
public sealed class TranscriptImportModelProvisioner(
    RuntimeModelSetupCoordinator coordinator,
    IPipelineModelSetupInteraction? modelSetupInteraction = null)
{
    private readonly RuntimeModelSetupCoordinator coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IPipelineModelSetupInteraction? modelSetupInteraction = modelSetupInteraction;

    public async Task EnsureImportModelsAsync(
        TranscriptWorkspace workspace,
        InferenceModelPreferences? modelPreferences,
        CancellationToken cancellationToken,
        RuntimeModelSetupCallbacks? callbacks = null,
        string? sourceLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        RuntimeModelSelections selections =
            RuntimeModelRequestFactory.CreateSelectionsFromPreferences(modelPreferences);
        RuntimeModelSetupCallbacks effectiveCallbacks =
            callbacks
            ?? modelSetupInteraction?.CreateCallbacks(progress: null, cancellationToken)
            ?? HeadlessRuntimeModelSetup.CreateCallbacks(cancellationToken);

        RuntimeModelSetupResult result = await coordinator
            .EnsureImportModelsAvailableAsync(
                workspace,
                selections,
                enableStemSeparation: false,
                effectiveCallbacks,
                allowOptionalStageSkip: false,
                cancellationToken,
                sourceLanguageCode: sourceLanguage)
            .ConfigureAwait(false);

        if (!result.IsReady)
        {
            throw new InvalidOperationException(
                "Required transcription models were not provisioned. Download or import the missing models, then retry.");
        }
    }

    public async Task EnsureDiarizationModelAsync(
        TranscriptWorkspace workspace,
        InferenceModelPreferences? modelPreferences,
        CancellationToken cancellationToken,
        RuntimeModelSetupCallbacks? callbacks = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        RuntimeModelSelections selections =
            RuntimeModelRequestFactory.CreateSelectionsFromPreferences(modelPreferences);
        RuntimeModelSetupCallbacks effectiveCallbacks =
            callbacks
            ?? modelSetupInteraction?.CreateCallbacks(progress: null, cancellationToken)
            ?? HeadlessRuntimeModelSetup.CreateCallbacks(cancellationToken);

        RuntimeModelSetupResult result = await coordinator
            .EnsureDiarizationModelAvailableAsync(workspace, selections, effectiveCallbacks, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsReady)
        {
            throw new InvalidOperationException(
                "Required diarization model was not provisioned. Download or import the missing model, then retry.");
        }
    }
}
