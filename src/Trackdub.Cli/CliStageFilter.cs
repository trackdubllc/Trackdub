using Trackdub.Application.Dubbing;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Cli;

internal static class CliStageFilter
{
    /// <summary>Speech/export pipeline used by unfiltered runs and <c>--from-stage</c> resumes.</summary>
    private static readonly IReadOnlyList<string> s_pipelineStageOrder = DubbingPipelineStages.DefaultStageOrder;

    /// <summary>Full catalog for <c>--only</c> validation and lip-stage <c>--from-stage</c> resumes.</summary>
    private static readonly IReadOnlyList<string> s_extendedStageOrder = DubbingPipelineStages.ExtendedStageOrder;

    internal static IReadOnlyList<string>? Build(
        string? fromStage,
        string[]? onlyStages,
        bool lipSync = false,
        bool repairLips = false)
    {
        if (onlyStages is { Length: > 0 } && (lipSync || repairLips))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                "--lip-sync and --repair-lips cannot be combined with --only; list the desired stages with --only.",
                "--only");
            return [];
        }

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

        if (string.IsNullOrWhiteSpace(fromStage) && !lipSync && !repairLips)
        {
            return null;
        }

        // Validate against extended order to catch typos and allow lip stages
        if (!string.IsNullOrWhiteSpace(fromStage) &&
            IndexOfStage(s_extendedStageOrder, fromStage) < 0)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                $"Unknown stage '{fromStage}'.",
                "--from-stage");
            return [];
        }

        if (lipSync || repairLips)
        {
            int start = string.IsNullOrWhiteSpace(fromStage)
                ? 0
                : IndexOfStage(s_extendedStageOrder, fromStage);
            if ((lipSync && IndexOfStage(s_extendedStageOrder, StageNames.LipSync) < start) ||
                (repairLips && IndexOfStage(s_extendedStageOrder, StageNames.LipSynthesis) < start))
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.InvalidArgument,
                    "Requested lip stage precedes --from-stage.",
                    "--from-stage");
                return [];
            }

            var selected = new HashSet<string>(s_pipelineStageOrder, StringComparer.OrdinalIgnoreCase);
            if (lipSync) selected.Add(StageNames.LipSync);
            if (repairLips) selected.Add(StageNames.LipSynthesis);
            if (!string.IsNullOrWhiteSpace(fromStage)) selected.Add(fromStage);
            // The existing Export stage produces the dubbed mix before repair.
            return s_extendedStageOrder.Skip(start).Where(selected.Contains).ToArray();
        }

        // Lip stages resume through the extended order; normal stages use the
        // pipeline order. Utility stages absent from the pipeline order run
        // themselves, then continue into the remaining pipeline stages at their
        // catalog position.
        if (IsLipStage(fromStage!))
        {
            int lipIndex = IndexOfStage(s_extendedStageOrder, fromStage!);
            return s_extendedStageOrder.Skip(lipIndex).ToArray();
        }

        int pipelineIndex = IndexOfStage(s_pipelineStageOrder, fromStage!);
        if (pipelineIndex >= 0)
        {
            return s_pipelineStageOrder.Skip(pipelineIndex).ToArray();
        }

        int extendedIndex = IndexOfStage(s_extendedStageOrder, fromStage!);
        return new[] { fromStage! }
            .Concat(s_extendedStageOrder
                .Skip(extendedIndex + 1)
                .Where(stage => IndexOfStage(s_pipelineStageOrder, stage) >= 0))
            .ToArray();
    }

    private static bool IsLipStage(string stageName) =>
        string.Equals(stageName, StageNames.LipSync, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stageName, StageNames.LipSynthesis, StringComparison.OrdinalIgnoreCase);

    private static int IndexOfStage(IReadOnlyList<string> order, string stageName)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], stageName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
