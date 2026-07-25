using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

/// <summary>
/// Defines the <c>run-stage</c> CLI command that executes a single named pipeline stage
/// against an existing project.
/// </summary>
internal static class RunStageCommand
{
    private static readonly string[] AcceptedStageNames =
    [
        StageNames.Separation,
        StageNames.Vad,
        StageNames.Asr,
        StageNames.Diarization,
        StageNames.Translation,
        StageNames.Tts,
        StageNames.LipSync,
        StageNames.Export,
        StageNames.LipSynthesis,
    ];

    public static Command Create()
    {
        var projectOption = new Option<string>("--project")
        {
            Description = "Path to an existing .trackdub project directory",
            Required = true,
        };

        var stageOption = new Option<string>("--stage")
        {
            Description = "Canonical stage name to execute (e.g., vad, asr, translation, tts, export)",
            Required = true,
        };
        stageOption.AcceptOnlyFromAmong(AcceptedStageNames);

        var modelOption = new Option<string?>("--model")
        {
            Description = "Model alias override for the target stage",
        };

        var command = new Command("run-stage", """
            Execute a single named pipeline stage against an existing project.

            Examples:
              trackdub run-stage --project ./sample.trackdub --stage vad
              trackdub run-stage --project ./sample.trackdub --stage translation --model translation:madlad
            """)
        {
            projectOption,
            stageOption,
            modelOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            string projectPath = parseResult.GetValue(projectOption)!;
            string stageName = parseResult.GetValue(stageOption)!;
            string? modelAlias = parseResult.GetValue(modelOption);

            string progressFormat = CliParseHelpers.GetGlobalOptionValue<string>(parseResult, "progress") ?? "text";

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await CliProgressRunner.ExecuteAsync(
                    progressFormat,
                    async (progress, ct) => await RunStageHandler.ExecuteAsync(
                        factory,
                        projectPath,
                        stageName,
                        modelAlias,
                        progress,
                        Console.Out,
                        ct).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }
}
