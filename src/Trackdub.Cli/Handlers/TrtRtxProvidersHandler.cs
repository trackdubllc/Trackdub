using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Trackdub.Application.Runtime;
using Trackdub.Composition.StarterPacks;
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

    public static async Task<int> SmokeAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        TextWriter progressOutput,
        CancellationToken cancellationToken)
    {
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        ITensorRtRtxRuntimeReadinessService readinessService =
            factory.GetRequiredService<ITensorRtRtxRuntimeReadinessService>();

        TensorRtRtxRuntimeReadinessSnapshot snapshot = await readinessService
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        if (!snapshot.IsReady)
        {
            var blocked = new TrtRtxSmokeOutput
            {
                Ready = false,
                Blocker = snapshot.Blocker.ToString(),
                Detail = snapshot.Detail,
                Attempted = 0,
                Passed = 0,
                Failed = 0,
                Skipped = TrtRtxSmokeCatalog.StarterPackTurboGpu.Count,
                Targets = [],
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(blocked, SmokeJsonOptions)).ConfigureAwait(false);
            await progressOutput.WriteLineAsync(
                    snapshot.Detail ?? "TensorRT RTX EP ABI plugin is not ready. Run trackdub providers trt-rtx status.")
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        TrtRtxStarterPackSmokeReport report;
        try
        {
            report = await TrtRtxStarterPackSmokeRunner
                .RunAsync(storagePaths.ModelCacheDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failed = new TrtRtxSmokeOutput
            {
                Ready = true,
                Detail = ex.Message,
                Attempted = 0,
                Passed = 0,
                Failed = 1,
                Skipped = 0,
                Targets = [],
            };

            await output.WriteLineAsync(JsonSerializer.Serialize(failed, SmokeJsonOptions)).ConfigureAwait(false);
            await progressOutput.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        var payload = new TrtRtxSmokeOutput
        {
            Ready = true,
            Scope = TrtRtxSmokeCatalog.ScopeName,
            Attempted = report.Attempted,
            Passed = report.Passed,
            Failed = report.Failed,
            Skipped = report.Skipped,
            Targets = report.Targets
                .Select(target => new TrtRtxSmokeTargetOutput
                {
                    Label = target.Label,
                    ModelReference = target.ModelReference,
                    Status = target.Status.ToString().ToLowerInvariant(),
                    Detail = target.Detail,
                })
                .ToArray(),
        };

        await output.WriteLineAsync(JsonSerializer.Serialize(payload, SmokeJsonOptions)).ConfigureAwait(false);

        foreach (TrtRtxStarterPackSmokeTargetResult target in report.Targets)
        {
            string line = target.Status switch
            {
                TrtRtxStarterPackSmokeTargetStatus.Passed =>
                    $"PASS {target.Label} ({target.ModelReference})",
                TrtRtxStarterPackSmokeTargetStatus.Skipped =>
                    $"SKIP {target.Label}: {target.Detail ?? "model not cached locally"}",
                TrtRtxStarterPackSmokeTargetStatus.Failed =>
                    $"FAIL {target.Label}: {target.Detail ?? "smoke test failed"}",
                _ => $"{target.Label}: {target.Status}",
            };

            await progressOutput.WriteLineAsync(line).ConfigureAwait(false);
        }

        await progressOutput.WriteLineAsync(
                $"TRT RTX smoke summary: passed={report.Passed}, failed={report.Failed}, skipped={report.Skipped}.")
            .ConfigureAwait(false);

        if (!report.HasAttempts)
        {
            await progressOutput.WriteLineAsync(
                    "No TRT RTX smoke targets were cached locally. Download starter-pack models first.")
                .ConfigureAwait(false);
            return Program.ExitPipelineFailure;
        }

        return report.HasFailures ? Program.ExitPipelineFailure : Program.ExitSuccess;
    }

    private static readonly JsonSerializerOptions SmokeJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

    private sealed class TrtRtxSmokeOutput
    {
        public bool Ready { get; init; }
        public string? Scope { get; init; }
        public string? Blocker { get; init; }
        public string? Detail { get; init; }
        public int Attempted { get; init; }
        public int Passed { get; init; }
        public int Failed { get; init; }
        public int Skipped { get; init; }
        public IReadOnlyList<TrtRtxSmokeTargetOutput> Targets { get; init; } = [];
    }

    private sealed class TrtRtxSmokeTargetOutput
    {
        public string Label { get; init; } = string.Empty;
        public string ModelReference { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? Detail { get; init; }
    }
}
