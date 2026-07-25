using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Sdk;

namespace Trackdub.Cli;

internal static class CliParseHelpers
{
    internal static readonly string[] SupportedExecutionProviderKeys = ["auto", "cpu", "directml", "cuda"];

    internal static string FormatSupportedExecutionProviders() =>
        string.Join(", ", SupportedExecutionProviderKeys);

    private static readonly IReadOnlyDictionary<string, ExecutionProviderPreference> ExecutionProviderKeyMap =
        new Dictionary<string, ExecutionProviderPreference>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = ExecutionProviderPreference.Auto,
            ["cpu"] = ExecutionProviderPreference.Cpu,
            ["directml"] = ExecutionProviderPreference.DirectML,
            ["cuda"] = ExecutionProviderPreference.Cuda,
        };

    internal static T? GetGlobalOptionValue<T>(ParseResult parseResult, string optionName)
    {
        if (FindGlobalOption(parseResult, optionName) is not Option<T> option)
        {
            return default;
        }

        return parseResult.GetValue(option);
    }

    internal static bool TryGetExplicitGlobalOptionValue<T>(
        ParseResult parseResult,
        string optionName,
        out T? value)
    {
        if (FindGlobalOption(parseResult, optionName) is not Option<T> option
            || !HasExplicitOptionToken(parseResult, optionName))
        {
            value = default;
            return false;
        }

        value = parseResult.GetValue(option);
        return true;
    }

    /// <summary>
    /// Resolves the execution provider and Windows ML device policy using the precedence
    /// explicit CLI flags &gt; preset values &gt; application defaults.
    /// When the user explicitly passed <c>--execution-provider</c> / <c>--device-policy</c> those
    /// win; otherwise values stored in the loaded <paramref name="preset"/> are used.
    /// </summary>
    internal static void ResolvePresetExecutionPreferences(
        ParseResult parseResult,
        PipelinePreset? preset,
        out string? executionProvider,
        out string? devicePolicy)
    {
        bool executionProviderExplicit = TryGetExplicitGlobalOptionValue<string>(
                parseResult, "execution-provider", out string? cliExecutionProvider)
            && !string.IsNullOrWhiteSpace(cliExecutionProvider);
        bool devicePolicyExplicit = TryGetExplicitGlobalOptionValue<string>(
                parseResult, "device-policy", out string? cliDevicePolicy)
            && !string.IsNullOrWhiteSpace(cliDevicePolicy);

        executionProvider = executionProviderExplicit ? cliExecutionProvider : preset?.ExecutionProvider;
        devicePolicy = devicePolicyExplicit ? cliDevicePolicy : preset?.DevicePolicy;
    }

    private static Option? FindGlobalOption(ParseResult parseResult, string optionName)
    {
        Command rootCommand = GetRootCommand(parseResult);
        string dashedName = optionName.StartsWith("--", StringComparison.Ordinal)
            ? optionName
            : "--" + optionName;

        return rootCommand.Options.FirstOrDefault(o =>
            o.Name.Equals(dashedName, StringComparison.OrdinalIgnoreCase)
            || o.Name.Equals(optionName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExplicitOptionToken(ParseResult parseResult, string optionName)
    {
        string dashedName = optionName.StartsWith("--", StringComparison.Ordinal)
            ? optionName
            : "--" + optionName;

        return parseResult.Tokens.Any(token => MatchesExplicitOptionToken(token.Value, dashedName, optionName));
    }

    private static bool MatchesExplicitOptionToken(string tokenValue, string dashedName, string optionName)
    {
        if (string.Equals(tokenValue, dashedName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tokenValue, optionName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // System.CommandLine accepts --option=value and --option:value as single tokens.
        return tokenValue.StartsWith(dashedName + "=", StringComparison.OrdinalIgnoreCase)
               || tokenValue.StartsWith(dashedName + ":", StringComparison.OrdinalIgnoreCase);
    }

    internal static TrackdubBuilder ApplyModelDirectory(TrackdubBuilder builder, string? modelDirectory)
    {
        if (modelDirectory is null)
        {
            return builder;
        }

        string resolvedDirectory = Path.GetFullPath(modelDirectory);
        try
        {
            Directory.CreateDirectory(resolvedDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DirectoryNotFoundException(
                $"Model directory could not be created: {resolvedDirectory}",
                ex);
        }

        return builder
            .WithModelDirectory(resolvedDirectory)
            .WithModelCacheDirectory(resolvedDirectory);
    }

    internal static TrackdubSessionFactory? TryBuildFactory(ParseResult parseResult, out int exitCode)
    {
        string? modelDirectory = GetGlobalOptionValue<string?>(parseResult, "model-directory");
        string? executionProvider = GetGlobalOptionValue<string?>(parseResult, "execution-provider");
        string? devicePolicy = GetGlobalOptionValue<string?>(parseResult, "device-policy");
        return TryBuildFactory(modelDirectory, executionProvider, devicePolicy, out exitCode);
    }

    internal static TrackdubSessionFactory? TryBuildFactory(string? modelDirectory, out int exitCode) =>
        TryBuildFactory(modelDirectory, executionProvider: null, devicePolicy: null, out exitCode);

    internal static TrackdubSessionFactory? TryBuildFactory(
        string? modelDirectory,
        string? executionProvider,
        string? devicePolicy,
        out int exitCode)
    {
        exitCode = Program.ExitSuccess;

        if (!TryParseExecutionProvider(executionProvider, out ExecutionProviderPreference providerPreference))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                $"Unknown execution provider: '{executionProvider}'. Expected one of: {FormatSupportedExecutionProviders()}.",
                "--execution-provider");
            exitCode = Program.ExitArgumentError;
            return null;
        }

        if (!TryParseDevicePolicy(devicePolicy, out WindowsMlExecutionDevicePolicy resolvedDevicePolicy))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                $"Unknown device policy: '{devicePolicy}'. Expected one of: {WindowsMlExecutionDevicePolicySettings.FormatSupportedKeys()}.",
                "--device-policy");
            exitCode = Program.ExitArgumentError;
            return null;
        }

        try
        {
            TrackdubSessionFactory factory = ApplyModelDirectory(new TrackdubBuilder(), modelDirectory)
                .WithExecutionProvider(providerPreference)
                .WithWindowsMlExecutionDevicePolicy(resolvedDevicePolicy)
                .Build();
            CliLoggingBootstrap.EnsureReady(factory);
            return factory;
        }
        catch (DirectoryNotFoundException ex)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                ex.Message,
                "--model-directory");
            exitCode = Program.ExitArgumentError;
            return null;
        }
    }

    internal static bool TryParseDevicePolicy(string? value, out WindowsMlExecutionDevicePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            policy = WindowsMlExecutionDevicePolicy.Explicit;
            return true;
        }

        return WindowsMlExecutionDevicePolicySettings.TryParseKey(value, out policy);
    }

    internal static bool TryParseExecutionProvider(string? value, out ExecutionProviderPreference preference)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            preference = ExecutionProviderPreference.Auto;
            return true;
        }

        return ExecutionProviderKeyMap.TryGetValue(value.Trim(), out preference);
    }

    private static Command GetRootCommand(ParseResult parseResult)
    {
        SymbolResult current = parseResult.RootCommandResult;
        while (current.Parent is not null)
        {
            current = current.Parent;
        }

        return ((CommandResult)current).Command;
    }
}
