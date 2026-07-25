namespace Trackdub.Application.Transcripts.Pipeline;

public interface ITranscriptGenerationStage
{
    string StageName { get; }

    Task<TranscriptGenerationContext> ExecuteAsync(
        TranscriptGenerationContext context,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null);
}
