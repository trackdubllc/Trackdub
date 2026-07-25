using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Cli;

internal static class CliStageFilter
{
    /// <summary>Speech/export pipeline used by unfiltered runs and <c>--from-stage</c> resumes.</summary>
    private static readonly string[] s_pipelineStageOrder =
    [
        StageNames.Separation,
        StageNames.Vad,
        StageNames.Diarization,
        StageNames.Asr,
        StageNames.Translation,
        StageNames.Tts,
        StageNames.Export,
    ];

    /// <summary>Full catalog for <c>--only</c> validation and lip-stage <c>--from-stage</c> resumes.</summary>
    private static readonly string[] s_extendedStageOrder =
    [
        StageNames.Separation,
        StageNames.Vad,
        StageNames.Diarization,
        StageNames.Asr,
        StageNames.Translation,
        StageNames.Tts,
        StageNames.LipSync,
        StageNames.Export,
        StageNames.LipSynthesis,
    ];

    internal static IReadOnlyList<string>? Build(string? fromStage, string[]? onlyStages)
    {
        if (onlyStages is { Length: > 0 })
        {
            foreach (string stage in onlyStages)
            {
                if (IndexOfStage(s_extendedStageOrder, stage) < 0)
                {
                    CliErrorReporter.ReportValidationError(
                        ErrorCode.InvalidArgument,
                        $"Unknown stage '{stage}'.",
                        "--only");
                    return [];
                }
            }

            return onlyStages;
        }

        if (string.IsNullOrWhiteSpace(fromStage))
        {
            return null;
        }

        // Validate against extended order to catch typos and allow lip stages
        if (IndexOfStage(s_extendedStageOrder, fromStage) < 0)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                $"Unknown stage '{fromStage}'.",
                "--from-stage");
            return [];
        }

        // Use pipeline order for normal stages, extended order only when starting from a lip stage
        string[] order = IsLipStage(fromStage) ? s_extendedStageOrder : s_pipelineStageOrder;
        int startIndex = IndexOfStage(order, fromStage);

        return order.Skip(startIndex).ToArray();
    }

    private static bool IsLipStage(string stageName) =>
        string.Equals(stageName, StageNames.LipSync, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stageName, StageNames.LipSynthesis, StringComparison.OrdinalIgnoreCase);

    private static int IndexOfStage(string[] order, string stageName) =>
        Array.FindIndex(
            order,
            candidate => string.Equals(candidate, stageName, StringComparison.OrdinalIgnoreCase));
}
