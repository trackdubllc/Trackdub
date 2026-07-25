using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Logging;

/// <summary>
/// Default logger implementation using System.Diagnostics.
/// </summary>
public sealed class DebugApplicationLogger : IApplicationLogger
{
    public void LogDebug(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[DEBUG] {message}");
    }

    public void LogInformation(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[INFO] {message}");
    }

    public void LogWarning(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            System.Diagnostics.Debug.WriteLine($"[WARN] {message}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[WARN] {message}: {exception}");
        }
    }

    public void LogError(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            System.Diagnostics.Debug.WriteLine($"[ERROR] {message}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[ERROR] {message}: {exception}");
        }
    }
}
