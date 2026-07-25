using Trackdub.Contracts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts.Stages;

public sealed class TextRefinementGenerationStage(
    TextRefinementStageHandler textRefinementStageHandler,
    IProjectStageRunStore stageRunStore,
    PipelineDegradationWriter? degradationWriter = null)
    : ITranscriptGenerationStage
{
    private readonly TextRefinementStageHandler textRefinementStageHandler =
        textRefinementStageHandler ?? throw new ArgumentNullException(nameof(textRefinementStageHandler));
    private readonly IProjectStageRunStore stageRunStore =
        stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));

    public string StageName => StageNames.TextRefinementAsr;

    public async Task<TranscriptGenerationContext> ExecuteAsync(
        TranscriptGenerationContext context,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        if (context.AsrResult is null)
        {
            throw new InvalidOperationException("ASR result is missing.");
        }

        if (!context.ModelPreferences.EnableAsrTextRefinement)
        {
            return await SkipAsync(
                    context,
                    "TEXT_REFINEMENT_DISABLED",
                    "ASR text refinement is disabled.",
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);
        }

        if (context.AsrResult.Segments.Count == 0)
        {
            return await SkipAsync(
                    context,
                    "TEXT_REFINEMENT_NO_SEGMENTS",
                    "ASR produced no segments; text refinement skipped.",
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);
        }

        PipelineProgressReporter.Phase(
            progress,
            StageName,
            "Polishing transcript",
            $"Running ASR text refinement on {context.AsrResult.Segments.Count} segment(s).");

        try
        {
            TextRefinementStageResult refinementResult = await textRefinementStageHandler.HandleAsync(
                new TextRefinementStageRequest(
                    context.Project.Id,
                    context.AsrResult.Segments
                        .OrderBy(segment => segment.Index)
                        .Select(segment => new TextRefinementInputSegment(
                            segment.Index,
                            segment.StartSeconds,
                            segment.EndSeconds,
                            segment.Text))
                        .ToArray(),
                    TextRefinementScope.Asr,
                    context.SourceLanguage,
                    PreferredModelAlias: context.ModelPreferences.TextRefinementModelAlias,
                    RequirePreferredModelAlias: context.ModelPreferences.RequireTextRefinementModelAlias,
                    PreferredExecutionProvider: context.ModelPreferences.GetPreferredExecutionProvider(RuntimeStage.TextRefinement),
                    RequirePreferredExecutionProvider: context.ModelPreferences.RequiresPreferredExecutionProvider(RuntimeStage.TextRefinement),
                    PreferredModelVariantAlias: context.ModelPreferences.GetPreferredModelVariantAlias(RuntimeStage.TextRefinement)),
                cancellationToken).ConfigureAwait(false);

            PipelineProgressReporter.Phase(
                progress,
                StageName,
                "Polish complete",
                $"Refined {refinementResult.Segments.Count} segment(s).");

            return context with { TextRefinementResult = refinementResult };
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FileNotFoundException)
        {
            return await SkipWithDegradationAsync(
                    context,
                    "TEXT_REFINEMENT_UNAVAILABLE",
                    ex.Message,
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);
        }
    }

    private async Task<TranscriptGenerationContext> SkipAsync(
        TranscriptGenerationContext context,
        string reasonCode,
        string reason,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress)
    {
        PipelineProgressReporter.Phase(progress, StageName, "Skipping polish", reason);
        StageRunRecord skippedStageRun = await StageRunHelper
            .StartAsync(stageRunStore, context.Project.Id, StageNames.TextRefinementAsr, cancellationToken)
            .ConfigureAwait(false);
        skippedStageRun = await StageRunHelper
            .SkipAsync(stageRunStore, skippedStageRun, runtimeReporter: null, reasonCode, cancellationToken)
            .ConfigureAwait(false);

        return context with
        {
            TextRefinementResult = new TextRefinementStageResult(
                skippedStageRun,
                TextRefinementScope.Asr,
                [])
        };
    }

    private async Task<TranscriptGenerationContext> SkipWithDegradationAsync(
        TranscriptGenerationContext context,
        string degradationCode,
        string detail,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress)
    {
        TranscriptGenerationContext skipped = await SkipAsync(
                context,
                degradationCode,
                detail,
                cancellationToken,
                progress)
            .ConfigureAwait(false);

        if (degradationWriter is not null && skipped.TextRefinementResult is not null)
        {
            try
            {
                await degradationWriter.WriteAsync(
                    new PipelineDegradationRecord(
                        StageNames.TextRefinementAsr,
                        degradationCode,
                        "ASR text refinement was unavailable; raw ASR transcript preserved.",
                        Detail: detail,
                        SelectedFallback: "raw-asr",
                        RecommendedAction: "Download the text-refinement model or disable ASR polish.",
                        DateTimeOffset.UtcNow,
                        skipped.TextRefinementResult.StageRun.Id),
                    context.Project.Id,
                    context.MediaAsset.Id,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Best-effort degradation write.
            }
        }

        return skipped;
    }
}
