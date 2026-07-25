namespace Trackdub.Contracts;

/// <summary>
/// Defines a small application logging contract for services that should not depend on UI or infrastructure details.
/// </summary>
public interface IApplicationLogger
{
    void LogDebug(string message);
    void LogInformation(string message);
    void LogWarning(string message, Exception? exception = null);
    void LogError(string message, Exception? exception = null);

    /// <summary>
    /// Writes an error entry in a way that prefers reaching durable storage immediately
    /// (for crash / AppDomain-terminating paths). Default implementation falls back to <see cref="LogError"/>.
    /// </summary>
    void LogErrorSynchronously(string message, Exception? exception = null) =>
        LogError(message, exception);

    /// <summary>
    /// Best-effort flush of any buffered log entries. Default is a no-op.
    /// </summary>
    void Flush() => Flush(TimeSpan.FromSeconds(5));

    /// <summary>
    /// Best-effort flush with an explicit timeout (crash paths should pass a short budget).
    /// </summary>
    void Flush(TimeSpan timeout)
    {
    }
}