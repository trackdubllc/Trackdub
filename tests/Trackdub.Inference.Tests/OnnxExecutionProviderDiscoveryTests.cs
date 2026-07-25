using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Xunit;

namespace Trackdub.Inference.Tests;

public sealed class OnnxExecutionProviderDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_LinuxWithNvidiaDriverWithoutTensorRt_ReportsCudaOnly()
    {
        var discovery = new OnnxExecutionProviderDiscovery(
            new StubOpenVinoProvider(false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: false));

        IReadOnlyList<ExecutionProviderAvailability> availabilities = await discovery.DiscoverAsync(
            new HardwareProfile("linux", "x64", HasGpu: true, GpuDescription: "NVIDIA RTX 4090"),
            CancellationToken.None);

        Assert.True(availabilities.Single(a => a.Provider == ExecutionProviderKind.Cuda).IsAvailable);
        ExecutionProviderAvailability tensorRt = availabilities.Single(a => a.Provider == ExecutionProviderKind.TensorRt);
        Assert.False(tensorRt.IsAvailable);
        Assert.Contains("libnvinfer", tensorRt.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsync_LinuxWithNvidiaDriverAndTensorRt_ReportsTensorRtAvailable()
    {
        var discovery = new OnnxExecutionProviderDiscovery(
            new StubOpenVinoProvider(false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: true, nativeTensorRtAvailable: true));

        IReadOnlyList<ExecutionProviderAvailability> availabilities = await discovery.DiscoverAsync(
            new HardwareProfile("linux", "x64", HasGpu: true, GpuDescription: "NVIDIA RTX 4090"),
            CancellationToken.None);

        Assert.True(availabilities.Single(a => a.Provider == ExecutionProviderKind.Cuda).IsAvailable);
        Assert.True(availabilities.Single(a => a.Provider == ExecutionProviderKind.TensorRt).IsAvailable);
    }

    [Fact]
    public async Task DiscoverAsync_LinuxWithoutNvidiaDriver_ReportsCudaAndTensorRtUnavailable()
    {
        var discovery = new OnnxExecutionProviderDiscovery(
            new StubOpenVinoProvider(false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: false, nativeTensorRtAvailable: true));

        IReadOnlyList<ExecutionProviderAvailability> availabilities = await discovery.DiscoverAsync(
            new HardwareProfile("linux", "x64", HasGpu: true, GpuDescription: "NVIDIA RTX 4090"),
            CancellationToken.None);

        Assert.False(availabilities.Single(a => a.Provider == ExecutionProviderKind.Cuda).IsAvailable);
        Assert.False(availabilities.Single(a => a.Provider == ExecutionProviderKind.TensorRt).IsAvailable);
    }

    [Fact]
    public async Task DiscoverAsync_Windows_IncludesWinMlCatalogEpProviders()
    {
        var discovery = new OnnxExecutionProviderDiscovery(
            new StubOpenVinoProvider(false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: false, nativeTensorRtAvailable: false),
            new StubNativeCudaTensorRtWindowsPolicy(allowed: false),
            new StubMigraphxReadinessProbe(),
            new StubDnnlReadinessProbe(isReady: false),
            new StubTensorRtRtxReadinessProbe(),
            new StubWinMlCatalogReadinessProbe(),
            new StubWinMlCatalogReadinessProbe(),
            new StubWinMlCatalogReadinessProbe());

        IReadOnlyList<ExecutionProviderAvailability> availabilities = await discovery.DiscoverAsync(
            new HardwareProfile("windows", "x64", HasGpu: true, GpuDescription: "NVIDIA RTX 5080"),
            CancellationToken.None);

        Assert.Contains(availabilities, a => a.Provider == ExecutionProviderKind.Qnn);
        Assert.Contains(availabilities, a => a.Provider == ExecutionProviderKind.OpenVinoCatalog);
        Assert.Contains(availabilities, a => a.Provider == ExecutionProviderKind.VitisAi);
    }

    [Fact]
    public async Task DiscoverAsync_WhenTensorRtRtxLicenseIsNotAccepted_DoesNotProbeOrRegisterPlugin()
    {
        var tensorRtProbe = new StubTensorRtRtxReadinessProbe();
        var discovery = new OnnxExecutionProviderDiscovery(
            new StubOpenVinoProvider(false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: false, nativeTensorRtAvailable: false),
            new StubNativeCudaTensorRtWindowsPolicy(allowed: false),
            new StubMigraphxReadinessProbe(),
            new StubDnnlReadinessProbe(isReady: false),
            tensorRtProbe,
            new StubWinMlCatalogReadinessProbe(),
            new StubWinMlCatalogReadinessProbe(),
            new StubWinMlCatalogReadinessProbe(),
            isTensorRtRtxEnabled: static _ => Task.FromResult(false));

        IReadOnlyList<ExecutionProviderAvailability> availabilities = await discovery.DiscoverAsync(
            new HardwareProfile("windows", "x64", HasGpu: true, GpuDescription: "NVIDIA RTX 5070"),
            CancellationToken.None);

        ExecutionProviderAvailability tensorRt = Assert.Single(
            availabilities,
            availability => availability.Provider == ExecutionProviderKind.TensorRTRtx);
        Assert.False(tensorRt.IsAvailable);
        Assert.Contains("license", tensorRt.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tensorRtProbe.CallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoverAsync_IncludesDnnlReadinessProbeResult(bool dnnlReady)
    {
        var discovery = new OnnxExecutionProviderDiscovery(
            new StubOpenVinoProvider(false),
            new StubLinuxNativeGpuRuntimeProbe(nvidiaDriverLoaded: false, nativeTensorRtAvailable: false),
            new StubNativeCudaTensorRtWindowsPolicy(allowed: false),
            new StubMigraphxReadinessProbe(),
            new StubDnnlReadinessProbe(dnnlReady),
            new StubTensorRtRtxReadinessProbe(),
            new StubWinMlCatalogReadinessProbe(),
            new StubWinMlCatalogReadinessProbe(),
            new StubWinMlCatalogReadinessProbe());

        IReadOnlyList<ExecutionProviderAvailability> availabilities = await discovery.DiscoverAsync(
            new HardwareProfile("linux", "x64", HasGpu: false, GpuDescription: null),
            CancellationToken.None);

        ExecutionProviderAvailability dnnl = Assert.Single(availabilities, a => a.Provider == ExecutionProviderKind.Dnnl);
        Assert.Equal(dnnlReady, dnnl.IsAvailable);
        Assert.Contains("DNNL fake", dnnl.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubNativeCudaTensorRtWindowsPolicy(bool allowed) : INativeCudaTensorRtWindowsPolicy
    {
        public Task<bool> IsNativeProvidersAllowedOnWindowsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);
    }

    private sealed class StubMigraphxReadinessProbe : IMigraphxReadinessProbe
    {
        public Task<MigraphxReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MigraphxReadinessReport(
                ProviderId: string.Empty,
                MigraphxPlatformRoute.None,
                MigraphxReadinessBlocker.PlatformUnsupported,
                IsHardwareEligible: false,
                IsOrtProviderListed: false,
                IsRegisteredWithOrt: false,
                Detail: "MIGraphX not part of this test."));
    }

    private sealed class StubTensorRtRtxReadinessProbe : ITensorRtRtxReadinessProbe
    {
        public int CallCount { get; private set; }

        public Task<TensorRtRtxReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new TensorRtRtxReadinessReport(
                TensorRtRtxProviderIds.PluginEpAbi,
                TensorRtRtxPlatformRoute.PluginEpAbi,
                TensorRtRtxReadinessBlocker.EpNotPresent,
                IsHardwareEligible: true,
                IsOrtProviderListed: false,
                IsRegisteredWithOrt: false,
                Detail: "Plugin fake not registered."));
        }
    }

    private sealed class StubDnnlReadinessProbe(bool isReady) : IDnnlReadinessProbe
    {
        public Task<DnnlReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnnlReadinessReport(
                DnnlProviderIds.NativeOrt,
                isReady ? DnnlReadinessBlocker.None : DnnlReadinessBlocker.OrtProviderUnavailable,
                IsSupportedRid: true,
                IsOrtProviderListed: isReady,
                CanAppendSessionOptions: isReady,
                SmokeTestPassed: isReady,
                Detail: isReady ? "DNNL fake ready." : "DNNL fake unavailable."));
    }

    private sealed class StubWinMlCatalogReadinessProbe :
        IOpenVinoCatalogReadinessProbe,
        IQnnCatalogReadinessProbe,
        IVitisAiCatalogReadinessProbe
    {
        public Task<WinMlCatalogReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinMlCatalogReadinessReport(
                ProviderId: string.Empty,
                WinMlCatalogPlatformRoute.WinMlCatalog,
                WinMlCatalogReadinessBlocker.EpNotPresent,
                IsHardwareEligible: true,
                IsOrtProviderListed: false,
                IsRegisteredWithOrt: false,
                Detail: "Catalog fake not installed."));
    }

    private sealed class StubOpenVinoProvider(bool isAvailable) : IOpenVinoAvailabilityProvider
    {
        public bool IsAvailable { get; } = isAvailable;

        public bool UseOpenVinoCpuProxy => false;
    }

    private sealed class StubLinuxNativeGpuRuntimeProbe(
        bool nvidiaDriverLoaded,
        bool nativeTensorRtAvailable,
        bool amdGpuPresent = false)
        : ILinuxNativeGpuRuntimeProbe
    {
        public bool IsNvidiaDriverLoaded() => nvidiaDriverLoaded;

        public bool IsAmdGpuPresent() => amdGpuPresent;

        public bool IsNativeTensorRtAvailable() => nativeTensorRtAvailable;

        public bool IsCudaOrtProviderAvailable() => true;

        public bool IsMigraphxOrtProviderAvailable() => false;
    }
}
