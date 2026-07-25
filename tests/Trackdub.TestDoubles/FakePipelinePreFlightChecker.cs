using Trackdub.Application.Pipeline;

namespace Trackdub.TestDoubles;

/// <summary>
/// A no-op pre-flight checker for use in tests. Always reports models as available.
/// Can be configured to throw <see cref="Exception"/> for specific stage names to test
/// preflight-failure paths.
/// </summary>
public sealed class FakePipelinePreFlightChecker : IPipelinePreFlightChecker
{
    private readonly Dictionary<string, Exception> _blockedStages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total number of times <see cref="EnsureModelsAvailableAsync"/> was called.</summary>
    public int CallCount { get; private set; }

    /// <summary>Stage names passed to <see cref="EnsureModelsAvailableAsync"/> in order.</summary>
    public IReadOnlyList<string> CheckedStageNames => _checkedStageNames;
    private readonly List<string> _checkedStageNames = [];

    /// <summary>
    /// Configure the checker to throw <paramref name="exception"/> when the given
    /// <paramref name="stageName"/> is checked.
    /// </summary>
    public void BlockStage(string stageName, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        ArgumentNullException.ThrowIfNull(exception);
        _blockedStages[stageName] = exception;
    }

    public Task EnsureModelsAvailableAsync(
        string stageName,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        _checkedStageNames.Add(stageName);

        if (_blockedStages.TryGetValue(stageName, out Exception? ex))
        {
            throw ex;
        }

        return Task.CompletedTask;
    }
}
