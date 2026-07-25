namespace Trackdub.Contracts;

public interface ICloudDubbingEngine
{
    Task<CloudDubbingResult> DubAsync(CloudDubbingRequest request, CancellationToken cancellationToken);
}

public sealed record CloudDubbingRequest(
    string MediaFilePath,
    string SourceLanguage,
    string TargetLanguage);

public sealed record CloudDubbingResult(
    byte[] AudioBytes,
    string TargetLanguage,
    TimeSpan EstimatedDuration);
