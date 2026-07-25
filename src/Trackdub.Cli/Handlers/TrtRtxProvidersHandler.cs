using System.Runtime.InteropServices;
using System.Text.Json;

using Trackdub.Application.Runtime;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Headless TensorRT RTX EP ABI plugin status and install for operators.
/// </summary>
internal static class TrtRtxProvidersHandler
{
    private const string LicenseReference =
        "NVIDIA TensorRT-RTX: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html; CUDA EULA: https://docs.nvidia.com/cuda/eula/index.html";

    public static async Task<int> StatusAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        ITensorRtRtxRuntimeReadinessService readinessService =
            factory.GetRequiredService<ITensorRtRtxRuntimeReadinessService>();

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        TensorRtRtxRuntimeReadinessSnapshot snapshot = await readinessService
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        string? environmentDirectory = Environment.GetEnvironmentVariable(
            TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable);
        string defaultInstallDirectory = TensorRtRtxProviderConstants.GetDefaultInstallDirectory(
            storagePaths.UserDataRoot,
            ResolveNativeRuntimeIdentifier());

        var payload = new TrtRtxStatusOutput
        {
            Ready = snapshot.IsReady,
            SupportedPlatform = snapshot.IsSupportedPlatform,
            ProviderId = snapshot.ProviderId,
            Route = snapshot.RouteDisplay,
            StatusLabel = snapshot.StatusLabel,
            Blocker = snapshot.Blocker.ToString(),
            Detail = snapshot.Detail,
            IsHardwareEligible = snapshot.IsHardwareEligible,
            IsOrtProviderListed = snapshot.IsOrtProviderListed,
            IsRegisteredWithOrt = snapshot.IsRegisteredWithOrt,
            LicenseAccepted = settings.NvidiaTensorRtRtxLicenseAccepted,
            PluginDirectory = ResolveEffectivePluginDirectory(
                settings.TensorRtRtxPluginDirectory,
                environmentDirectory,
                defaultInstallDirectory),
            StudioPluginDirectory = settings.TensorRtRtxPluginDirectory,
            EnvironmentPluginDirectory = environmentDirectory,
            DefaultInstallDirectory = defaultInstallDirectory,
            InstallHint = snapshot.InstallHint,
        };

        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return snapshot.IsReady ? Program.ExitSuccess : Program.ExitPipelineFailure;
    }

    public static async Task<int> InstallAsync(
        TrackdubSessionFactory factory,
        bool acceptLicense,
        TextWriter output,
        TextWriter progressOutput,
        CancellationToken cancellationToken)
    {
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        ITrtRtxEpInstaller installer = factory.GetRequiredService<ITrtRtxEpInstaller>();

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (acceptLicense && !settings.NvidiaTensorRtRtxLicenseAccepted)
        {
            settings = settings with { NvidiaTensorRtRtxLicenseAccepted = true };
            await settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            await progressOutput.WriteLineAsync(
                $"Accepted NVIDIA TensorRT RTX license flag in studio settings. Reference: {LicenseReference}")
                .ConfigureAwait(false);
        }

        if (!settings.NvidiaTensorRtRtxLicenseAccepted)
        {
            var blocked = new TrtRtxInstallOutput
            {
                Succeeded = false,
                FailureDetail =
                    "NVIDIA TensorRT RTX license not accepted. Pass --accept-license after reviewing the license terms, or accept in Model Manager.",
                LicenseReference = LicenseReference,
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(blocked, CliJsonOptions.Default))
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        var progress = new Progress<string>(message =>
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                progressOutput.WriteLine(message);
            }
        });

        TrtRtxEpInstallResult result = await installer
            .EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        ITensorRtRtxRuntimeReadinessService readinessService =
            factory.GetRequiredService<ITensorRtRtxRuntimeReadinessService>();
        TensorRtRtxRuntimeReadinessSnapshot snapshot = await readinessService
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        var payload = new TrtRtxInstallOutput
        {
            Succeeded = result.Succeeded,
            FailureDetail = result.FailureDetail,
            Ready = snapshot.IsReady,
            Blocker = snapshot.Blocker.ToString(),
            Detail = snapshot.Detail,
            IsOrtProviderListed = snapshot.IsOrtProviderListed,
            LicenseReference = LicenseReference,
        };

        await output.WriteLineAsync(JsonSerializer.Serialize(payload, CliJsonOptions.Default))
            .ConfigureAwait(false);

        return result.Succeeded && snapshot.IsReady
            ? Program.ExitSuccess
            : Program.ExitPipelineFailure;
    }

    private static string? ResolveEffectivePluginDirectory(
        string? studioDirectory,
        string? environmentDirectory,
        string defaultInstallDirectory)
    {
        if (!string.IsNullOrWhiteSpace(studioDirectory))
        {
            return studioDirectory;
        }

        if (!string.IsNullOrWhiteSpace(environmentDirectory))
        {
            return environmentDirectory;
        }

        return Directory.Exists(defaultInstallDirectory) ? defaultInstallDirectory : null;
    }

    private static string ResolveNativeRuntimeIdentifier()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64",
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                _ => "osx-x64",
            };
        }

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "linux-arm64",
            _ => "linux-x64",
        };
    }

    private sealed class TrtRtxStatusOutput
    {
        public bool Ready { get; init; }
        public bool SupportedPlatform { get; init; }
        public string? ProviderId { get; init; }
        public string? Route { get; init; }
        public string? StatusLabel { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public bool IsHardwareEligible { get; init; }
        public bool IsOrtProviderListed { get; init; }
        public bool IsRegisteredWithOrt { get; init; }
        public bool LicenseAccepted { get; init; }
        public string? PluginDirectory { get; init; }
        public string? StudioPluginDirectory { get; init; }
        public string? EnvironmentPluginDirectory { get; init; }
        public string? DefaultInstallDirectory { get; init; }
        public string? InstallHint { get; init; }
    }

    private sealed class TrtRtxInstallOutput
    {
        public bool Succeeded { get; init; }
        public bool Ready { get; init; }
        public string? FailureDetail { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public bool IsOrtProviderListed { get; init; }
        public string? LicenseReference { get; init; }
    }
}
