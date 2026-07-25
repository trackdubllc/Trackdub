using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts.Stages;

public sealed class AsrGenerationStage(
    AsrStageHandler asrStageHandler,
    IArtifactStore artifactStore,
    IProjectStageRunStore stageRunStore,
    PipelineDegradationWriter? degradationWriter = null)
    : ITranscriptGenerationStage
{
    private readonly AsrStageHandler asrStageHandler = asrStageHandler ?? throw new ArgumentNullException(nameof(asrStageHandler));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IProjectStageRunStore stageRunStore = stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));

    public string StageName => StageNames.Asr;

    public async Task<TranscriptGenerationContext> ExecuteAsync(
        TranscriptGenerationContext context,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        if (context.RegionPlan is null)
        {
            throw new InvalidOperationException("Region plan is missing.");
        }

        TranscriptRegionPlan regionPlan = context.RegionPlan;
        if (regionPlan.Regions.Count == 0)
        {
            double durationSeconds = ResolveShortAudioDurationSeconds(context);
            if (durationSeconds > 0d && durationSeconds < TranscriptPipelineConstants.ShortAudioFallbackMaximumSeconds)
            {
                var fallbackRegion = new SpeechRegion(0, 0d, durationSeconds);
                regionPlan = new TranscriptRegionPlan([fallbackRegion], regionPlan.SpeakerIdsBySegmentIndex);
                PipelineProgressReporter.Phase(
                    progress,
                    StageName,
                    "Applying short audio fallback",
                    $"VAD detected no regions in {durationSeconds:F1}s audio; ASR will process the full audio.");

                if (degradationWriter is not null)
                {
                    try
                    {
                        await degradationWriter.WriteAsync(
                            new PipelineDegradationRecord(
                                StageNames.Asr,
                                "VAD_NO_REGIONS_SHORT_AUDIO_FALLBACK",
                                "VAD detected no speech regions in short audio; ASR will process the full audio.",
                                Detail: $"Duration: {durationSeconds:F3}s",
                                SelectedFallback: "full-audio-asr",
                                RecommendedAction: "Review the transcript before export.",
                                DateTimeOffset.UtcNow,
                                StageRunId: null),
                            context.Project.Id,
                            context.MediaAsset.Id,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // Degradation write is best-effort; failure must not abort the fallback ASR run.
                    }
                }
            }
        }

        context = context with { RegionPlan = regionPlan };

        // Guard: VAD found no speech regions. Skip ASR entirely and record the
        // degradation so the stage state stays distinct (Skipped) from a
        // genuine successful empty-segment run.
        if (regionPlan.Regions.Count == 0)
        {
            PipelineProgressReporter.Phase(progress, StageName, "Skipping ASR", "VAD detected no speech regions.");
            StageRunRecord skippedStageRun = await StageRunHelper
                .StartAsync(stageRunStore, context.Project.Id, StageNames.Asr, cancellationToken)
                .ConfigureAwait(false);
            skippedStageRun = await StageRunHelper
                .SkipAsync(stageRunStore, skippedStageRun, runtimeReporter: null, "VAD_NO_REGIONS", cancellationToken)
                .ConfigureAwait(false);

            if (degradationWriter is not null)
            {
                try
                {
                    await degradationWriter.WriteAsync(
                        new PipelineDegradationRecord(
                            StageNames.Asr,
                            "VAD_NO_REGIONS",
                            "VAD detected no speech regions; transcript will be empty.",
                            Detail: null,
                            SelectedFallback: null,
                            RecommendedAction: "Inspect the audio for excessive silence or noise.",
                            DateTimeOffset.UtcNow,
                            skippedStageRun.Id),
                        context.Project.Id,
                        context.MediaAsset.Id,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Degradation write is best-effort; failure must not abort the empty-transcript fallback.
                }
            }

            return context with { AsrResult = new AsrStageResult(skippedStageRun, []) };
        }

        PipelineProgressReporter.Phase(
            progress,
            StageName,
            "Recognizing speech",
            $"Running ASR on {regionPlan.Regions.Count} region(s).");
        AsrStageResult asrResult = await asrStageHandler.HandleAsync(
            new AsrStageRequest(
                context.Project.Id,
                artifactStore.GetPath(context.AudioRoutingPlan.AsrAudioArtifact.RelativePath),
                regionPlan.Regions,
                context.ModelPreferences.AsrModelAlias,
                context.ModelPreferences.RequireAsrModelAlias,
                context.SourceLanguage,
                context.ModelPreferences.GetPreferredExecutionProvider(RuntimeStage.Asr),
                context.ModelPreferences.RequiresPreferredExecutionProvider(RuntimeStage.Asr),
                context.ModelPreferences.GetPreferredModelVariantAlias(RuntimeStage.Asr)),
            cancellationToken).ConfigureAwait(false);

        PipelineProgressReporter.Phase(
            progress,
            StageName,
            "Recognition complete",
            $"Recognized {asrResult.Segments.Count} segment(s).");

        if (asrResult.DeviceDegradation is DeviceDegradationReport degradation && degradationWriter is not null)
        {
            try
            {
                PipelineDegradationRecord record = degradation.Kind == DeviceDegradationKind.MemoryExhausted
                    ? DeviceFailureDegradationFactory.CreateOomRecord(
                        StageNames.Asr,
                        degradation.FailedDeviceIndex,
                        degradation.FailedAdapterDescription,
                        degradation.ErrorDetail,
                        asrResult.StageRun.Id)
                    : DeviceFailureDegradationFactory.CreateDeviceFailureRecord(
                        StageNames.Asr,
                        degradation.FailedDeviceIndex,
                        degradation.FailedAdapterDescription,
                        degradation.ErrorDetail,
                        asrResult.StageRun.Id);
                await degradationWriter.WriteAsync(
                    record,
                    context.Project.Id,
                    context.MediaAsset.Id,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A fallback device already produced a completed ASR result; a failed degradation
                // record write must not abort the successful stage.
            }
        }

        // Distinct from VAD_NO_REGIONS above: VAD found speech, but the transcription engine
        // still produced no segments (e.g. all regions were noise/unintelligible). Record it
        // so this low-confidence outcome doesn't look identical to a genuine empty-audio skip.
        if (asrResult.Segments.Count == 0 && degradationWriter is not null)
        {
            try
            {
                await degradationWriter.WriteAsync(
                    new PipelineDegradationRecord(
                        StageNames.Asr,
                        "ASR_EMPTY_RESULT",
                        "ASR ran on detected speech regions but produced no transcript segments.",
                        Detail: null,
                        SelectedFallback: null,
                        RecommendedAction: "Inspect the audio quality or try a different ASR model.",
                        DateTimeOffset.UtcNow,
                        asrResult.StageRun.Id),
                    context.Project.Id,
                    context.MediaAsset.Id,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Degradation write is best-effort; failure must not abort the pipeline.
            }
        }

        return context with { AsrResult = asrResult };
    }

    private static double ResolveShortAudioDurationSeconds(TranscriptGenerationContext context) =>
        context.AudioRoutingPlan.AsrAudioArtifact.DurationSeconds
        ?? context.NormalizedAudioArtifact.DurationSeconds
        ?? context.MediaAsset.DurationSeconds;
}
