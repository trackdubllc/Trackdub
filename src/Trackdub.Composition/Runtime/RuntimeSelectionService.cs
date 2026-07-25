using Microsoft.Extensions.Logging;
using Trackdub.Contracts;
using Trackdub.Application.Runtime;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Runtime;

public sealed class RuntimeSelectionService(
    IExecutionProviderDiscovery providerDiscovery,
    IHardwareProfileProvider hardwareProvider,
    IRuntimePlanner runtimePlanner,
    IHardwareProfilerService hardwareProfilerService,
    ILogger<RuntimeSelectionService> logger)
    : IRuntimeSelectionService
{
    private readonly IExecutionProviderDiscovery providerDiscovery = providerDiscovery ?? throw new ArgumentNullException(nameof(providerDiscovery));
    private readonly IHardwareProfileProvider hardwareProvider = hardwareProvider ?? throw new ArgumentNullException(nameof(hardwareProvider));
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly IHardwareProfilerService hardwareProfilerService = hardwareProfilerService ?? throw new ArgumentNullException(nameof(hardwareProfilerService));
    private readonly ILogger<RuntimeSelectionService> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<RuntimeRoute> SelectRouteAsync(
        RuntimeStage stage,
        ExecutionProviderKind? preference = null,
        CancellationToken cancellationToken = default)
    {
        HardwareProfile hardware = await hardwareProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        StageRuntimePlan plan = await runtimePlanner.PlanAsync(
            new StageRuntimePlanningRequest(stage, PreferredExecutionProvider: preference),
            cancellationToken).ConfigureAwait(false);

        ExecutionProviderKind selected = plan.ExecutionProvider ?? ExecutionProviderKind.Cpu;
        RuntimeRouteReadiness readiness = MapReadiness(plan);
        string? fallbackReason = FormatFallback(plan.Fallback);

        HardwareProfilerViewState? profilerState = null;
        try
        {
            profilerState = await hardwareProfilerService
                .GetViewStateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "HardwareProfilerService unavailable; benchmark evidence omitted from route.");
        }

        return new RuntimeRoute
        {
            Stage = stage,
            SelectedProvider = selected,
            DeviceId = plan.DeviceIndex,
            DeviceTarget = plan.DeviceAdapterDescription ?? hardware.GpuDescription,
            ModelId = plan.ModelId,
            Variant = plan.Variant,
            Readiness = readiness,
            FallbackReason = fallbackReason,
            BenchmarkEvidenceId = profilerState?.BenchmarkAvailableForPlanner == true
                ? profilerState.EvidenceIdForPlanner
                : null,
            Warnings = BuildWarnings(plan, fallbackReason)
        };
    }

    public async Task<IReadOnlyList<ProviderCapability>> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        HardwareProfile hardware = await hardwareProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ExecutionProviderAvailability> availabilities = await providerDiscovery.DiscoverAsync(hardware, cancellationToken).ConfigureAwait(false);

        bool benchmarkAvailable = false;
        try
        {
            HardwareProfilerViewState profilerState = await hardwareProfilerService
                .GetViewStateAsync(cancellationToken)
                .ConfigureAwait(false);
            benchmarkAvailable = profilerState.BenchmarkAvailableForPlanner;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "HardwareProfilerService unavailable; benchmark evidence omitted from capabilities.");
        }

        return availabilities.Select(a => new ProviderCapability
        {
            Provider = a.Provider,
            DeviceDetected = a.Provider switch
            {
                ExecutionProviderKind.Cpu => true,
                ExecutionProviderKind.Dnnl => true,
                ExecutionProviderKind.DirectMl => hardware.HasGpu,
                ExecutionProviderKind.TensorRTRtx => hardware.GpuDescription?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ?? false,
                ExecutionProviderKind.Cuda or ExecutionProviderKind.TensorRt =>
                    (hardware.GpuDescription?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ?? false) &&
                    (hardware.OperatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase) ||
                     hardware.OperatingSystem.Equals("windows", StringComparison.OrdinalIgnoreCase)),
                ExecutionProviderKind.Migraphx =>
                    hardware.GpuDescription?.Contains("AMD", StringComparison.OrdinalIgnoreCase) ?? false,
                _ => false
            },
            // RuntimePackageInstalled reflects whether the EP runtime package is present. Discovery
            // can report the TensorRT RTX EP ABI plugin route, but model readiness still requires a
            // smoke-test session selecting the plugin and running the graph. Report false here rather
            // than faking model readiness.
            RuntimePackageInstalled = false,
            ProviderLoadable = a.IsAvailable,
            ModelVariantCompatible = false,
            // SmokeTestPassed is only known after a real inference session succeeds. Discovery does not
            // run a smoke test, so this must not be faked as true.
            SmokeTestPassed = false,
            BenchmarkAvailable = benchmarkAvailable,
            BlockedReason = a.IsAvailable ? null : a.Detail
        }).ToList();
    }

    private static RuntimeRouteReadiness MapReadiness(StageRuntimePlan plan) =>
        plan.Status switch
        {
            StageRuntimePlanStatus.Ready or StageRuntimePlanStatus.Verified => plan.Fallback is null
                ? RuntimeRouteReadiness.Ready
                : RuntimeRouteReadiness.Fallback,
            StageRuntimePlanStatus.DownloadRequired or StageRuntimePlanStatus.Blocked => RuntimeRouteReadiness.NotReady,
            _ => RuntimeRouteReadiness.NotReady
        };

    private static string? FormatFallback(RuntimePlanFallback? fallback) =>
        fallback is null
            ? null
            : fallback.Detail is null
                ? fallback.Code.ToString()
                : $"{fallback.Code}: {fallback.Detail}";

    private static IReadOnlyList<string> BuildWarnings(StageRuntimePlan plan, string? fallbackReason)
    {
        List<string> warnings = [];
        if (fallbackReason is not null)
        {
            warnings.Add(fallbackReason);
        }

        foreach (RuntimePlanWarning warning in plan.Warnings)
        {
            warnings.Add(warning.Detail is null
                ? warning.Code.ToString()
                : $"{warning.Code}: {warning.Detail}");
        }

        return warnings;
    }
}
