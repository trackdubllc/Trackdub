using System.CommandLine;

using Trackdub.Cli.Commands;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Cli;

internal static class Program
{
    /// <summary>Exit code for successful execution.</summary>
    internal const int ExitSuccess = 0;

    /// <summary>Exit code for argument or validation errors.</summary>
    internal const int ExitArgumentError = 1;

    /// <summary>Exit code for pipeline execution failures.</summary>
    internal const int ExitPipelineFailure = 2;

    /// <summary>Exit code when the user or host cancels an in-flight operation (130 = 128 + SIGINT).</summary>
    internal const int ExitCancelled = 130;

    static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 && UiCommand.IsInteractiveTerminal())
            {
                return await UiCommand.RunFromMainAsync(modelDirectory: null, CancellationToken.None).ConfigureAwait(false);
            }

            var rootCommand = BuildRootCommand();
            var parseResult = rootCommand.Parse(args);

            if (IsHelpRequest(args))
            {
                using var output = new StringWriter();
                var configuration = new InvocationConfiguration
                {
                    Output = output
                };

                int exitCode = await parseResult.InvokeAsync(configuration).ConfigureAwait(false);
                await Console.Out.WriteAsync(NormalizeHelpExecutableName(output.ToString())).ConfigureAwait(false);
                return exitCode;
            }

            return await parseResult.InvokeAsync();
        }
        catch (OperationCanceledException)
        {
            return ExitCancelled;
        }
    }

    internal static RootCommand BuildRootCommand(Func<bool>? isSetupInteractive = null)
    {
        var rootCommand = new RootCommand("Trackdub CLI - headless dubbing pipeline");

        // Global options
        var modelDirectoryOption = new Option<string?>("--model-directory")
        {
            Description = "Path to the model directory containing downloaded models",
            Recursive = true
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable verbose logging to stderr",
            Recursive = true,
            DefaultValueFactory = _ => false
        };

        var progressOption = new Option<string>("--progress")
        {
            Description = "Progress output format (json or text). Text uses live Spectre progress bars when stderr is an interactive terminal.",
            Recursive = true,
            DefaultValueFactory = _ => "text"
        };
        progressOption.AcceptOnlyFromAmong("json", "text");

        var skipPreflightOption = new Option<bool>("--skip-preflight")
        {
            Description = "Skip pre-flight model and runtime validation",
            Recursive = true,
            DefaultValueFactory = _ => false
        };

        var executionProviderOption = new Option<string>("--execution-provider")
        {
            Description = "Preferred ONNX Runtime execution provider for inference (auto, cpu, directml, cuda). "
                + "Auto lets the runtime planner choose the best available provider.",
            Recursive = true,
            DefaultValueFactory = _ => "auto"
        };
        executionProviderOption.AcceptOnlyFromAmong("auto", "cpu", "directml", "cuda");

        var devicePolicyOption = new Option<string>("--device-policy")
        {
            Description = "Windows ML execution-provider device policy (advanced, Windows-only): "
                + $"{WindowsMlExecutionDevicePolicySettings.FormatSupportedKeys()}. "
                + "Explicit (default) keeps Trackdub's own catalog device selection; other values "
                + "delegate device choice to ONNX Runtime's SetEpSelectionPolicy. Ignored on non-Windows platforms.",
            Recursive = true,
            DefaultValueFactory = _ => WindowsMlExecutionDevicePolicySettings.ExplicitKey
        };
        devicePolicyOption.AcceptOnlyFromAmong(
            WindowsMlExecutionDevicePolicySettings.AllPolicies.Select(
                WindowsMlExecutionDevicePolicySettings.ToKey).ToArray());

        rootCommand.Add(modelDirectoryOption);
        rootCommand.Add(verboseOption);
        rootCommand.Add(progressOption);
        rootCommand.Add(skipPreflightOption);
        rootCommand.Add(executionProviderOption);
        rootCommand.Add(devicePolicyOption);

        // Subcommands
        rootCommand.Add(DubCommand.Create(isSetupInteractive));
        rootCommand.Add(RunCommand.Create(isSetupInteractive));
        rootCommand.Add(RunStageCommand.Create());
        rootCommand.Add(CheckCommand.Create());
        rootCommand.Add(DoctorCommand.Create());
        rootCommand.Add(ConfigCommand.Create());
        rootCommand.Add(ProjectCommand.Create());
        rootCommand.Add(ModelsCommand.Create());
        rootCommand.Add(ProvidersCommand.Create());
        rootCommand.Add(CacheCommand.Create());
        rootCommand.Add(UiCommand.Create(isSetupInteractive));
        rootCommand.Add(CompleteCommand.Create(rootCommand));
        rootCommand.Add(CompletionCommand.Create());

        return rootCommand;
    }

    internal static string NormalizeHelpExecutableName(string helpText) =>
        helpText
            .Replace("Usage:\r\n  Trackdub.Cli", "Usage:\r\n  trackdub", StringComparison.Ordinal)
            .Replace("Usage:\n  Trackdub.Cli", "Usage:\n  trackdub", StringComparison.Ordinal);

    private static bool IsHelpRequest(IEnumerable<string> args) =>
        args.Any(static arg => arg is "--help" or "-h" or "-?");
}
