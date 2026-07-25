using System.Reflection;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Diagnostics;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Runtime;

internal sealed class DiagnosticsRuntimeInfoProvider : IDiagnosticsRuntimeInfo
{
    private static readonly string[] WindowsAppSdkAssemblyNames =
    [
        "Microsoft.WindowsAppSDK.ML",
        "Microsoft.WindowsAppRuntime.Bootstrap.Net",
        "Microsoft.WindowsAppRuntime"
    ];

    private readonly IHardwareProfileProvider hardwareProfileProvider;
    private readonly IExecutionProviderDiscovery executionProviderDiscovery;
    private readonly IMigraphxReadinessProbe migraphxReadinessProbe;
    private readonly Lazy<DiagnosticsRuntimeSnapshot> snapshot;

    public DiagnosticsRuntimeInfoProvider(
        IHardwareProfileProvider hardwareProfileProvider,
        IExecutionProviderDiscovery executionProviderDiscovery,
        IMigraphxReadinessProbe migraphxReadinessProbe)
    {
        this.hardwareProfileProvider = hardwareProfileProvider ?? throw new ArgumentNullException(nameof(hardwareProfileProvider));
        this.executionProviderDiscovery = executionProviderDiscovery ?? throw new ArgumentNullException(nameof(executionProviderDiscovery));
        this.migraphxReadinessProbe = migraphxReadinessProbe ?? throw new ArgumentNullException(nameof(migraphxReadinessProbe));
        snapshot = new Lazy<DiagnosticsRuntimeSnapshot>(CaptureSnapshot);
    }

    public string? GpuDescription => snapshot.Value.GpuDescription;

    public bool DirectMlAvailable => snapshot.Value.DirectMlAvailable;

    public string? OnnxRuntimeVersion => snapshot.Value.OnnxRuntimeVersion;

    public string? WindowsAppSdkVersion => snapshot.Value.WindowsAppSdkVersion;

    public bool MigraphxAvailable => snapshot.Value.MigraphxAvailable;

    public string? MigraphxReadinessDetail => snapshot.Value.MigraphxReadinessDetail;

    private DiagnosticsRuntimeSnapshot CaptureSnapshot()
    {
        string? onnxRuntimeVersion = ResolveTypeAssemblyVersion("Microsoft.ML.OnnxRuntime.InferenceSession, Microsoft.ML.OnnxRuntime");
        string? windowsAppSdkVersion = ResolveLoadedAssemblyVersion(WindowsAppSdkAssemblyNames);

        try
        {
            HardwareProfile hardwareProfile = hardwareProfileProvider
                .GetCurrentAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            IReadOnlyList<ExecutionProviderAvailability> providers = executionProviderDiscovery
                .DiscoverAsync(hardwareProfile, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            bool directMlAvailable = providers.Any(
                provider => provider.Provider == ExecutionProviderKind.DirectMl && provider.IsAvailable);

            MigraphxReadinessReport migraphxReport = migraphxReadinessProbe
                .ProbeAsync(allowProviderDownloads: false, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return new DiagnosticsRuntimeSnapshot(
                hardwareProfile.GpuDescription,
                directMlAvailable,
                onnxRuntimeVersion,
                windowsAppSdkVersion,
                migraphxReport.IsReady,
                migraphxReport.Detail);
        }
        catch
        {
            return new DiagnosticsRuntimeSnapshot(
                GpuDescription: null,
                DirectMlAvailable: false,
                onnxRuntimeVersion,
                windowsAppSdkVersion,
                MigraphxAvailable: false,
                MigraphxReadinessDetail: null);
        }
    }

    private static string? ResolveTypeAssemblyVersion(string assemblyQualifiedTypeName) =>
        Type.GetType(assemblyQualifiedTypeName, throwOnError: false)
            ?.Assembly
            .GetName()
            .Version
            ?.ToString();

    private static string? ResolveLoadedAssemblyVersion(IReadOnlyCollection<string> assemblyNames)
    {
        Assembly? assembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(loadedAssembly =>
                loadedAssembly.GetName().Name is { } name &&
                assemblyNames.Contains(name));
        return assembly?.GetName().Version?.ToString();
    }

    private sealed record DiagnosticsRuntimeSnapshot(
        string? GpuDescription,
        bool DirectMlAvailable,
        string? OnnxRuntimeVersion,
        string? WindowsAppSdkVersion,
        bool MigraphxAvailable,
        string? MigraphxReadinessDetail);
}
