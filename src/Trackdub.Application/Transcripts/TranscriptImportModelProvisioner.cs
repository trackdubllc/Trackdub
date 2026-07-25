namespace Trackdub.Application.Transcripts;

/// <summary>
/// Ensures VAD/ASR (and optional separation) models are provisioned before transcription runs.
/// </summary>
public sealed class TranscriptImportModelProvisioner(RuntimeModelSetupCoordinator coordinator)
{
    private readonly RuntimeModelSetupCoordinator coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async Task EnsureImportModelsAsync(
        TranscriptWorkspace workspace,
        InferenceModelPreferences? modelPreferences,
        bool enableStemSeparation,
        CancellationToken cancellationToken,
        RuntimeModelSetupCallbacks? callbacks = null,
        string? sourceLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        RuntimeModelSelections selections =
            RuntimeModelRequestFactory.CreateSelectionsFromPreferences(modelPreferences);
        RuntimeModelSetupCallbacks effectiveCallbacks =
            callbacks ?? HeadlessRuntimeModelSetup.CreateCallbacks(cancellationToken);

        RuntimeModelSetupResult result = await coordinator
            .EnsureImportModelsAvailableAsync(
                workspace,
                selections,
                enableStemSeparation,
                effectiveCallbacks,
                allowOptionalStageSkip: enableStemSeparation,
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
            callbacks ?? HeadlessRuntimeModelSetup.CreateCallbacks(cancellationToken);

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
