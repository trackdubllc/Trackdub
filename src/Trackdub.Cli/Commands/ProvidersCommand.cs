using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

/// <summary>
/// <c>providers trt-rtx status</c> and <c>install</c> for headless TensorRT RTX EP ABI plugin management.
/// </summary>
internal static class ProvidersCommand
{
    public static Command Create()
    {
        var providersCommand = new Command("providers", """
            Inspect and install inference execution provider bundles.

            Examples:
              trackdub providers trt-rtx status
              trackdub providers trt-rtx install --accept-license
            """);

        var trtRtxCommand = new Command("trt-rtx", "TensorRT RTX EP ABI plugin (Windows/Linux NVIDIA GPU).");
        trtRtxCommand.Add(CreateStatusCommand());
        trtRtxCommand.Add(CreateInstallCommand());
        providersCommand.Add(trtRtxCommand);

        return providersCommand;
    }

    private static Command CreateStatusCommand()
    {
        var command = new Command("status", """
            Probe TensorRT RTX plugin readiness without downloading.

            Examples:
              trackdub providers trt-rtx status
            """);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await TrtRtxProvidersHandler
                    .StatusAsync(factory, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreateInstallCommand()
    {
        var acceptLicenseOption = new Option<bool>("--accept-license")
        {
            Description =
                "Persist NVIDIA TensorRT RTX license acceptance in studio settings before install (required unless already accepted in Model Manager)",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("install", """
            Download (when allowed), register, and probe the TensorRT RTX EP ABI plugin.

            Examples:
              trackdub providers trt-rtx install --accept-license
            """)
        {
            acceptLicenseOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool acceptLicense = parseResult.GetValue(acceptLicenseOption);

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await TrtRtxProvidersHandler
                    .InstallAsync(factory, acceptLicense, Console.Out, Console.Error, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }
}
