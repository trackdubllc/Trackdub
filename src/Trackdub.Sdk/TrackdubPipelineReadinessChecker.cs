using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;

namespace Trackdub.Sdk;

/// <summary>
/// Evaluates default pipeline readiness through the same gate as desktop and headless runs.
/// </summary>
public sealed class TrackdubPipelineReadinessChecker
{
    private static readonly RuntimeStage[] s_defaultPipelineStages =
    [
        RuntimeStage.Separation,
        RuntimeStage.Vad,
        RuntimeStage.Asr,
        RuntimeStage.Diarization,
        RuntimeStage.Translation,
        RuntimeStage.Tts,
    ];

    private readonly TrackdubSessionFactory _factory;

    public TrackdubPipelineReadinessChecker(TrackdubSessionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// Evaluates readiness for the standard speech pipeline stages using default model selections.
    /// </summary>
    /// <param name="projectRootPath">
    /// Optional project directory for resume/satisfied detection. When null, evaluates with no project state.
    /// </param>
    public async Task<PipelineReadinessReport> EvaluateDefaultPipelineAsync(
        string? projectRootPath = null,
        CancellationToken cancellationToken = default)
    {
        IPipelineReadinessService readinessService = _factory.GetRequiredService<IPipelineReadinessService>();
        IStudioSettingsService settingsService = _factory.GetRequiredService<IStudioSettingsService>();
        readinessService.InvalidateCache();

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        RuntimeModelSelections selections =
            RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings);

        TranscriptProjectState? state = null;
        if (!string.IsNullOrWhiteSpace(projectRootPath))
        {
            await using TrackdubSession session = _factory.CreateSession(projectRootPath);
            try
            {
                state = await session.Workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // project database absent, corrupt, or schema-mismatched — evaluate with no state
            }
        }

        return await readinessService
            .EvaluateAsync(
                s_defaultPipelineStages,
                selections,
                state,
                cancellationToken,
                state?.TranscriptLanguage ?? settings.DefaultSourceLanguage,
                state?.SelectedTranslationTargetLanguage ?? settings.DefaultTargetLanguage)
            .ConfigureAwait(false);
    }
}
