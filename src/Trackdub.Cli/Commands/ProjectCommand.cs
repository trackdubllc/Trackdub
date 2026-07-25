using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

internal static class ProjectCommand
{
    public static Command Create()
    {
        var projectCommand = new Command("project", """
            Create and inspect Trackdub project spines without auto-starting transcription.

            Examples:
              trackdub project create --media ./video.mp4
              trackdub project info --project ./video.trackdub
              trackdub project open --project ./video.trackdub
            """);

        projectCommand.Add(CreateCreateCommand());
        projectCommand.Add(CreateIngestCommand());
        projectCommand.Add(CreateOpenCommand());
        projectCommand.Add(CreateInfoCommand());

        return projectCommand;
    }

    private static Command CreateCreateCommand()
    {
        var mediaOption = new Option<string>("--media")
        {
            Description = "Path to the source media file",
            Required = true,
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Project display name. Defaults to the media file stem",
        };

        var outputOption = new Option<string?>("--output")
        {
            Description = "Output directory for the project. Defaults to <media-stem>.trackdub adjacent to source media",
        };

        var command = new Command("create", """
            Register source media and create a project spine only.

            Examples:
              trackdub project create --media ./video.mp4
              trackdub project create --media ./video.mp4 --name Demo --output ./projects/demo.trackdub
            """)
        {
            mediaOption,
            nameOption,
            outputOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await ProjectHandler.CreateAsync(
                    factory,
                    parseResult.GetValue(mediaOption)!,
                    parseResult.GetValue(nameOption),
                    parseResult.GetValue(outputOption),
                    Console.Out,
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreateIngestCommand()
    {
        var mediaOption = new Option<string>("--media")
        {
            Description = "Path to the source media file",
            Required = true,
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Project display name. Defaults to the media file stem",
        };

        var outputOption = new Option<string?>("--output")
        {
            Description = "Output directory for the project. Defaults to <media-stem>.trackdub adjacent to source media",
        };

        var command = new Command("ingest", """
            Alias for project create.

            Examples:
              trackdub project ingest --media ./video.mp4
            """)
        {
            mediaOption,
            nameOption,
            outputOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await ProjectHandler.CreateAsync(
                    factory,
                    parseResult.GetValue(mediaOption)!,
                    parseResult.GetValue(nameOption),
                    parseResult.GetValue(outputOption),
                    Console.Out,
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreateOpenCommand()
    {
        var projectOption = new Option<string>("--project")
        {
            Description = "Path to an existing .trackdub project directory",
            Required = true,
        };

        var command = new Command("open", """
            Validate and summarize an existing project spine.

            Examples:
              trackdub project open --project ./video.trackdub
            """)
        {
            projectOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await ProjectHandler.OpenAsync(
                    factory,
                    parseResult.GetValue(projectOption)!,
                    Console.Out,
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreateInfoCommand()
    {
        var projectOption = new Option<string>("--project")
        {
            Description = "Path to an existing .trackdub project directory",
            Required = true,
        };

        var command = new Command("info", """
            Show project artifacts and stage run history.

            Examples:
              trackdub project info --project ./video.trackdub
            """)
        {
            projectOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await ProjectHandler.InfoAsync(
                    factory,
                    parseResult.GetValue(projectOption)!,
                    Console.Out,
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }
}
