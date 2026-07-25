using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Trackdub.Cli;

internal sealed class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger();
    public void Dispose() { }

    private sealed class StderrLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string level = logLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => "NONE"
            };

            string timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            Console.Error.Write(timestamp);
            Console.Error.Write(" [");
            Console.Error.Write(level);
            Console.Error.Write("] ");
            Console.Error.WriteLine(formatter(state, exception));

            if (exception is not null)
            {
                Console.Error.WriteLine(exception.ToString());
            }
        }
    }
}
