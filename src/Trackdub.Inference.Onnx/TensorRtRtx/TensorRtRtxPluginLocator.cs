using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

internal enum TensorRtRtxPluginDirectorySource
{
    None = 0,
    ExplicitStudioSetting,
    EnvironmentVariable,
    InstalledBundle
}

internal sealed record TensorRtRtxPluginResolution(
    bool Succeeded,
    string? DirectoryPath,
    string? ProviderLibraryPath,
    TensorRtRtxPluginDirectorySource Source,
    IReadOnlyList<string> MissingFiles,
    TensorRtRtxReadinessBlocker Blocker,
    string Detail);

internal static class TensorRtRtxPluginLocator
{
    public static IReadOnlyList<string> RequiredFileNames => TensorRtRtxProviderConstants.RequiredPluginFileNames;

    public static TensorRtRtxPluginResolution Resolve(
        string? explicitPluginDirectory = null,
        string? defaultInstallDirectory = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? fileExists = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        directoryExists ??= Directory.Exists;
        fileExists ??= File.Exists;

        if (!string.IsNullOrWhiteSpace(explicitPluginDirectory))
        {
            return ValidateCandidate(
                explicitPluginDirectory,
                TensorRtRtxPluginDirectorySource.ExplicitStudioSetting,
                directoryExists,
                fileExists);
        }

        string? environmentDirectory = getEnvironmentVariable(TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentDirectory))
        {
            return ValidateCandidate(
                environmentDirectory,
                TensorRtRtxPluginDirectorySource.EnvironmentVariable,
                directoryExists,
                fileExists);
        }

        if (!string.IsNullOrWhiteSpace(defaultInstallDirectory))
        {
            TensorRtRtxPluginResolution installedBundle = ValidateCandidate(
                defaultInstallDirectory,
                TensorRtRtxPluginDirectorySource.InstalledBundle,
                directoryExists,
                fileExists);
            if (installedBundle.Succeeded)
            {
                return installedBundle;
            }
        }

        return new TensorRtRtxPluginResolution(
            Succeeded: false,
            DirectoryPath: defaultInstallDirectory,
            ProviderLibraryPath: null,
            Source: TensorRtRtxPluginDirectorySource.None,
            MissingFiles: RequiredFileNames,
            Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
            Detail: $"TensorRT RTX plugin bundle not installed. Use Model Manager Install or set {TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable}.");
    }

    private static TensorRtRtxPluginResolution ValidateCandidate(
        string pluginDirectory,
        TensorRtRtxPluginDirectorySource source,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists)
    {
        string normalizedDirectory;
        try
        {
            normalizedDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(pluginDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new TensorRtRtxPluginResolution(
                Succeeded: false,
                DirectoryPath: pluginDirectory,
                ProviderLibraryPath: null,
                Source: source,
                MissingFiles: RequiredFileNames,
                Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
                Detail: $"TensorRT RTX plugin directory path is invalid: {ex.Message}");
        }

        if (!directoryExists(normalizedDirectory))
        {
            return new TensorRtRtxPluginResolution(
                Succeeded: false,
                DirectoryPath: normalizedDirectory,
                ProviderLibraryPath: null,
                Source: source,
                MissingFiles: RequiredFileNames,
                Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
                Detail: $"TensorRT RTX plugin directory '{normalizedDirectory}' was not found.");
        }

        string[] missingFiles = RequiredFileNames
            .Where(fileName => !fileExists(Path.Combine(normalizedDirectory, fileName)))
            .ToArray();

        if (missingFiles.Length > 0)
        {
            TensorRtRtxReadinessBlocker blocker = missingFiles.Contains(
                    TensorRtRtxProviderConstants.PluginLibraryFileName,
                    StringComparer.OrdinalIgnoreCase)
                ? TensorRtRtxReadinessBlocker.EpNotPresent
                : TensorRtRtxReadinessBlocker.EpNotReady;

            return new TensorRtRtxPluginResolution(
                Succeeded: false,
                DirectoryPath: normalizedDirectory,
                ProviderLibraryPath: Path.Combine(normalizedDirectory, TensorRtRtxProviderConstants.PluginLibraryFileName),
                Source: source,
                MissingFiles: missingFiles,
                Blocker: blocker,
                Detail: $"TensorRT RTX plugin directory '{normalizedDirectory}' is missing: {string.Join(", ", missingFiles)}.");
        }

        return new TensorRtRtxPluginResolution(
            Succeeded: true,
            DirectoryPath: normalizedDirectory,
            ProviderLibraryPath: Path.Combine(normalizedDirectory, TensorRtRtxProviderConstants.PluginLibraryFileName),
            Source: source,
            MissingFiles: [],
            Blocker: TensorRtRtxReadinessBlocker.None,
            Detail: $"TensorRT RTX plugin bundle located at '{normalizedDirectory}'.");
    }
}
