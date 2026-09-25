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
            TensorRtRtxPluginResolution explicitResolution = ValidateCandidate(
                explicitPluginDirectory,
                TensorRtRtxPluginDirectorySource.ExplicitStudioSetting,
                directoryExists,
                fileExists);

            // The bundle installer persists its install directory into this setting, so after a pin
            // bump it still names the previous managed bundle (e.g. Providers/trt-rtx/0.3.0/cu12/...),
            // which lacks the new runtime DLLs. Only a user-chosen directory is authoritative.
            if (explicitResolution.Succeeded ||
                !TensorRtRtxProviderConstants.IsSupersededManagedInstallDirectory(explicitResolution.DirectoryPath, defaultInstallDirectory))
            {
                return explicitResolution;
            }
        }

        string? environmentDirectory = getEnvironmentVariable(TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentDirectory))
        {
            // Same staleness rule: tools/dev/Fetch-TrtRtxEp.ps1 prints the managed install directory
            // for this variable, so a persisted value outlives the pin it was set for.
            TensorRtRtxPluginResolution environmentResolution = ValidateCandidate(
                environmentDirectory,
                TensorRtRtxPluginDirectorySource.EnvironmentVariable,
                directoryExists,
                fileExists);
            if (environmentResolution.Succeeded ||
                !TensorRtRtxProviderConstants.IsSupersededManagedInstallDirectory(environmentResolution.DirectoryPath, defaultInstallDirectory))
            {
                return environmentResolution;
            }
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

        bool directoryPresent;
        string[] missingFiles;
        try
        {
            directoryPresent = directoryExists(normalizedDirectory);

            if (!directoryPresent)
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

            missingFiles = RequiredFileNames
                .Where(fileName =>
                {
                    if (Path.IsPathRooted(fileName))
                    {
                        return true;
                    }

                    string candidatePath = Path.Join(normalizedDirectory, fileName);
                    return !fileExists(candidatePath);
                })
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TensorRtRtxPluginResolution(
                Succeeded: false,
                DirectoryPath: normalizedDirectory,
                ProviderLibraryPath: null,
                Source: source,
                MissingFiles: RequiredFileNames,
                Blocker: TensorRtRtxReadinessBlocker.EpNotPresent,
                Detail: $"TensorRT RTX plugin directory '{normalizedDirectory}' could not be read: {ex.Message}");
        }

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
