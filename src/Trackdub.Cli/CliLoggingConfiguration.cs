using Microsoft.Extensions.Logging;

namespace Trackdub.Cli;

/// <summary>
/// Configures application logging for the CLI tool.
/// </summary>
internal static class CliLoggingConfiguration
{
    /// <summary>
    /// Creates the configured logger factory for the CLI.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(bool verbose)
    {
        return new CliLoggerFactory(verbose);
    }

    private sealed class CliLoggerFactory(bool verbose) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
            // No-op for CLI preflight checks.
        }

        public ILogger CreateLogger(string categoryName)
        {
            if (verbose)
            {
                return new StderrLoggerProvider().CreateLogger(categoryName);
            }

            return new NoopLogger();
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoopLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
