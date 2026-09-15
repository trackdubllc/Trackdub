using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Contracts;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

internal static class RunCommand
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

    public static Command Create(Func<bool>? isSetupInteractive = null)
    {
        Func<bool> setupInteractive = isSetupInteractive ?? (() => !Console.IsInputRedirected);

        var runCommand = new Command("run", """
            Unified pipeline execution API.

            Examples:
              trackdub run stage --project ./sample.trackdub --stage vad
              trackdub run pipeline --media ./video.mp4 --target-language es
            """);

        runCommand.Add(CreateStageCommand());
        runCommand.Add(CreatePipelineCommand(setupInteractive));

        return runCommand;
    }

    private static Command CreateStageCommand()
    {
        var projectOption = new Option<string>("--project")
        {
            Description = "Path to an existing .trackdub project directory",
            Required = true,
        };

        var stageOption = new Option<string>("--stage")
        {
            Description = "Canonical stage name to execute",
            Required = true,
        };
        stageOption.AcceptOnlyFromAmong(AcceptedStageNames);

        var modelOption = new Option<string?>("--model")
        {
            Description = "Model alias override for the target stage",
        };

        var command = new Command("stage", """
            Execute a single named pipeline stage against an existing project.

            Examples:
              trackdub run stage --project ./sample.trackdub --stage vad
              trackdub run stage --project ./sample.trackdub --stage asr --model whisper-small
            """)
        {
            projectOption,
            stageOption,
            modelOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            string progressFormat = CliParseHelpers.GetGlobalOptionValue<string>(parseResult, "progress") ?? "text";

            using (factory)
            {
                return await CliProgressRunner.ExecuteAsync(
                    progressFormat,
                    async (progress, ct) => await RunStageHandler.ExecuteAsync(
                        factory,
                        parseResult.GetValue(projectOption)!,
                        parseResult.GetValue(stageOption)!,
                        parseResult.GetValue(modelOption),
                        progress,
                        Console.Out,
                        ct).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreatePipelineCommand(Func<bool> setupInteractive)
    {
        PipelineCommandOptions options = RunPipelineCommandOptions.Create(AcceptedStageNames);

        var command = new Command("pipeline", """
            Execute a full or partial dubbing pipeline.

            Examples:
              trackdub run pipeline --media ./video.mp4 --target-language es
              trackdub run pipeline --media ./video.mp4 --target-language fr --from-stage translation
              trackdub run pipeline --media ./video.mp4 --target-language de --only vad --only asr --force-rerun
              trackdub run pipeline --media ./video.mp4 --target-language en --voice-clone
              trackdub run pipeline --input-dir ./videos --target-language es --continue-on-error
              trackdub run pipeline --preset my-preset --input-glob "**/*.mp4"
            """);

        RunPipelineCommandOptions.AddTo(command, options);
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            RunPipelineCommandExecutor.ExecuteAsync(parseResult, options, setupInteractive, cancellationToken));

        return command;
    }
}
