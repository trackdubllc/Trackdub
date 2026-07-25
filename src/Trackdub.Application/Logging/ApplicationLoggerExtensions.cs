using System.Text.RegularExpressions;
using Trackdub.Contracts;

namespace Trackdub.Application.Logging;

/// <summary>
/// Structured logging helpers for <see cref="IApplicationLogger"/> call sites.
/// Keeps message templates constant while appending formatted values at write time.
/// </summary>
public static partial class ApplicationLoggerExtensions
{
    [GeneratedRegex(@"\{[^}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    public static void LogDebug(this IApplicationLogger? logger, string messageTemplate, params object?[] args)
    {
        logger?.LogDebug(Format(messageTemplate, args));
    }

    public static void LogInformation(this IApplicationLogger? logger, string messageTemplate, params object?[] args)
    {
        logger?.LogInformation(Format(messageTemplate, args));
    }

    public static void LogWarning(this IApplicationLogger? logger, string messageTemplate, params object?[] args)
    {
        logger?.LogWarning(Format(messageTemplate, args));
    }

    public static void LogWarning(
        this IApplicationLogger? logger,
        Exception exception,
        string messageTemplate,
        params object?[] args)
    {
        logger?.LogWarning(Format(messageTemplate, args), exception);
    }

    public static void LogError(this IApplicationLogger? logger, string messageTemplate, params object?[] args)
    {
        logger?.LogError(Format(messageTemplate, args));
    }

    public static void LogError(
        this IApplicationLogger? logger,
        Exception exception,
        string messageTemplate,
        params object?[] args)
    {
        logger?.LogError(Format(messageTemplate, args), exception);
    }

    private static string Format(string messageTemplate, ReadOnlySpan<object?> args)
    {
        if (args.IsEmpty)
        {
            return messageTemplate;
        }

        object?[] values = args.ToArray();
        int index = 0;
        return PlaceholderRegex().Replace(messageTemplate, _ =>
        {
            if (index >= values.Length)
            {
                return "?";
            }

            object? value = values[index++];
            return value?.ToString() ?? "null";
        });
    }
}
