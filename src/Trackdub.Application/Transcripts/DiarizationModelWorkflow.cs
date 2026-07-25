namespace Trackdub.Application.Transcripts;

public sealed class DiarizationModelWorkflow(DiarizationStageHandler? diarizationStageHandler = null)
{
    public RequiredDiarizationModelStatus? GetRequiredDiarizationModelStatus() =>
        diarizationStageHandler?.GetRequiredModelStatus();

    public Task DownloadRequiredDiarizationModelAsync(CancellationToken cancellationToken)
    {
        if (diarizationStageHandler is null)
        {
            throw new InvalidOperationException("Speaker detection model download is not configured.");
        }

        return diarizationStageHandler.DownloadRequiredModelAsync(cancellationToken: cancellationToken);
    }

    public Task ImportDiarizationModelAsync(
        string sourceModelPath,
        CancellationToken cancellationToken)
    {
        if (diarizationStageHandler is null)
        {
            throw new InvalidOperationException("Speaker detection model import is not configured.");
        }

        return diarizationStageHandler.ImportModelAsync(sourceModelPath, cancellationToken);
    }
}
