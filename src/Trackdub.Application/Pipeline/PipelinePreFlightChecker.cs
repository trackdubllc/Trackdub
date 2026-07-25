namespace Trackdub.Application.Pipeline;

/// <summary>
/// Pre-flight checks for stage execution.
/// </summary>
public interface IPipelinePreFlightChecker
{
    /// <summary>
    /// Checks that all required models are available. Throws if not.
    /// </summary>
    Task EnsureModelsAvailableAsync(
        string stageName,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null);
}
