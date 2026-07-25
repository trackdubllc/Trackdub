namespace Trackdub.Media.Process;

internal static class FfmpegErrorFormatter
{
    internal const int MaxStandardErrorChars = 4096;

    internal static string BuildFailureMessage(string operation, int exitCode, string? standardError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        string stderr = TruncateStandardError(standardError);
        return string.IsNullOrWhiteSpace(stderr)
            ? $"{operation} failed with exit code {exitCode}."
            : $"{operation} failed with exit code {exitCode}: {stderr}";
    }

    internal static string TruncateStandardError(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return string.Empty;
        }

        string trimmed = standardError.Trim();
        if (trimmed.Length <= MaxStandardErrorChars)
        {
            return trimmed;
        }

        return $"[stderr truncated to last {MaxStandardErrorChars} chars]{Environment.NewLine}{trimmed[^MaxStandardErrorChars..]}";
    }
}
