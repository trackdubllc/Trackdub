using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed class TextRefinementStageHandler(
    ITextRefinementEngine textRefinementEngine,
    IProjectStageRunStore stageRunStore,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IApplicationLogger? logger = null)
{
    private readonly ITextRefinementEngine textRefinementEngine =
        textRefinementEngine ?? throw new ArgumentNullException(nameof(textRefinementEngine));
    private readonly IProjectStageRunStore stageRunStore =
        stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));

    public async Task<TextRefinementStageResult> HandleAsync(
        TextRefinementStageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string stageName = request.Scope switch
        {
            TextRefinementScope.Asr => StageNames.TextRefinementAsr,
            TextRefinementScope.Translation => StageNames.TextRefinementTranslation,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Scope, "Unsupported text refinement scope.")
        };

        (StageRunRecord stageRun, IReadOnlyList<RefinedTextSegment> segments) = await StageRunHelper.RunStageAsync(
                stageRunStore,
                request.ProjectId,
                stageName,
                textRefinementEngine,
                async (_, ct) =>
                {
                    return await textRefinementEngine
                        .RefineAsync(
                            new TextRefinementRequest(
                                request.Segments,
                                request.Scope,
                                request.SourceLanguage,
                                request.TargetLanguage,
                                request.PreferredModelAlias,
                                request.PreferredExecutionProvider?.ToString(),
                                request.RequirePreferredModelAlias,
                                request.RequirePreferredExecutionProvider,
                                request.PreferredModelVariantAlias),
                            ct)
                        .ConfigureAwait(false);
                },
                "Text refinement canceled.",
                cancellationToken,
                runtimePlanningPreferences,
                logger)
            .ConfigureAwait(false);

        return new TextRefinementStageResult(stageRun, request.Scope, segments);
    }
}
