using Trackdub.Sdk;

namespace Trackdub.Cli;

internal static class CliModelOverrides
{
    internal static Dictionary<string, string>? Parse(string[] modelOverrides)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in modelOverrides)
        {
            int colonIndex = entry.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= entry.Length - 1)
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.InvalidArgument,
                    $"Invalid --model format: '{entry}'. Expected format: stage:alias (e.g., asr:large-v3)",
                    "--model");
                return null;
            }

            string stage = entry[..colonIndex].Trim();
            string alias = entry[(colonIndex + 1)..].Trim();

            if (string.IsNullOrEmpty(stage) || string.IsNullOrEmpty(alias))
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.InvalidArgument,
                    $"Invalid --model format: '{entry}'. Both stage and alias must be non-empty.",
                    "--model");
                return null;
            }

            result[stage] = alias;
        }

        return result;
    }

    /// <summary>
    /// Accepts a bare alias (<c>madlad400</c>) or the dub-style <c>stage:alias</c> form
    /// for a single <c>run-stage</c> invocation. A stage prefix that does not match
    /// <paramref name="stageName"/> is rejected: passing it through would match no
    /// model and silently fall back to the default route.
    /// </summary>
    /// <returns>
    /// A one-entry preference map keyed by <paramref name="stageName"/>, or <see langword="null"/>
    /// after a validation error has been reported.
    /// </returns>
    internal static Dictionary<string, string>? TryBuildRunStageModelPreferences(string stageName, string modelAlias)
    {
        string normalizedAlias = modelAlias.Trim();
        int colonIndex = normalizedAlias.IndexOf(':');
        if (colonIndex >= 0)
        {
            string prefix = normalizedAlias[..colonIndex].Trim();
            if (!string.Equals(prefix, stageName, StringComparison.OrdinalIgnoreCase))
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.InvalidArgument,
                    $"Invalid --model value: '{modelAlias}'. Expected a bare alias for stage '{stageName}' (e.g., madlad400) or '{stageName}:alias'.",
                    "--model");
                return null;
            }

            normalizedAlias = normalizedAlias[(colonIndex + 1)..].Trim();
        }

        if (string.IsNullOrEmpty(normalizedAlias))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                $"Invalid --model value: '{modelAlias}'. Model alias must be non-empty.",
                "--model");
            return null;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [stageName] = normalizedAlias,
        };
    }

    internal static Dictionary<string, string>? ParseVoiceOverrides(string[] voiceOverrides)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string entry in voiceOverrides)
        {
            int colonIndex = entry.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= entry.Length - 1)
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.InvalidArgument,
                    $"Invalid --voice format: '{entry}'. Expected format: SPEAKER_ID:voice_id (e.g., SPEAKER_00:af_bella)",
                    "--voice");
                return null;
            }

            string speakerId = entry[..colonIndex].Trim();
            string voiceId = entry[(colonIndex + 1)..].Trim();

            if (string.IsNullOrEmpty(speakerId) || string.IsNullOrEmpty(voiceId))
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.InvalidArgument,
                    $"Invalid --voice format: '{entry}'. Both speaker ID and voice ID must be non-empty.",
                    "--voice");
                return null;
            }

            result[speakerId] = voiceId;
        }

        return result;
    }
}
