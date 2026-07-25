using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Dnnl;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Migraphx;
using Trackdub.Inference.Onnx.WinMlCatalog;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.Migraphx;
using Trackdub.Inference.Runtime.NativeCudaTensorRt;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

public sealed class OnnxExecutionProviderDiscovery : IExecutionProviderDiscovery
{
    private readonly IOpenVinoAvailabilityProvider _openVino;
    private readonly ILinuxNativeGpuRuntimeProbe _linuxRuntimeProbe;
    private readonly INativeCudaTensorRtWindowsPolicy _nativeCudaTensorRtWindowsPolicy;
    private readonly IMigraphxReadinessProbe _migraphxReadinessProbe;
    private readonly IDnnlReadinessProbe _dnnlReadinessProbe;
    private readonly IOpenVinoCatalogReadinessProbe _openVinoCatalogReadinessProbe;
    private readonly IQnnCatalogReadinessProbe _qnnCatalogReadinessProbe;
    private readonly IVitisAiCatalogReadinessProbe _vitisAiCatalogReadinessProbe;
    private readonly ITensorRtRtxReadinessProbe _tensorRtRtxReadinessProbe;
    private readonly Func<CancellationToken, Task<bool>> _isTensorRtRtxEnabled;

    public OnnxExecutionProviderDiscovery()
        : this(new NullOpenVinoAvailabilityProvider())
    {
    }

    public OnnxExecutionProviderDiscovery(IOpenVinoAvailabilityProvider openVino)
        : this(openVino, new LinuxNativeGpuRuntimeProbe(), NullNativeCudaTensorRtWindowsPolicy.Instance)
    {
    }

    public OnnxExecutionProviderDiscovery(
        IOpenVinoAvailabilityProvider openVino,
        ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe)
        : this(openVino, linuxRuntimeProbe, NullNativeCudaTensorRtWindowsPolicy.Instance)
    {
    }

    public OnnxExecutionProviderDiscovery(
        IOpenVinoAvailabilityProvider openVino,
        ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe,
        INativeCudaTensorRtWindowsPolicy nativeCudaTensorRtWindowsPolicy)
        : this(openVino, linuxRuntimeProbe, nativeCudaTensorRtWindowsPolicy, new MigraphxReadinessProbe())
    {
    }

    public OnnxExecutionProviderDiscovery(
        IOpenVinoAvailabilityProvider openVino,
        ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe,
        INativeCudaTensorRtWindowsPolicy nativeCudaTensorRtWindowsPolicy,
        IMigraphxReadinessProbe migraphxReadinessProbe)
        : this(
            openVino,
            linuxRuntimeProbe,
            nativeCudaTensorRtWindowsPolicy,
            migraphxReadinessProbe,
            new DnnlReadinessProbe(),
            new TensorRtRtxReadinessProbe(),
            new OpenVinoCatalogReadinessProbe(),
            new QnnCatalogReadinessProbe(),
            new VitisAiCatalogReadinessProbe())
    {
    }

    public OnnxExecutionProviderDiscovery(
        IOpenVinoAvailabilityProvider openVino,
        ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe,
        INativeCudaTensorRtWindowsPolicy nativeCudaTensorRtWindowsPolicy,
        IMigraphxReadinessProbe migraphxReadinessProbe,
        IDnnlReadinessProbe dnnlReadinessProbe,
        ITensorRtRtxReadinessProbe tensorRtRtxReadinessProbe,
        IOpenVinoCatalogReadinessProbe openVinoCatalogReadinessProbe,
        IQnnCatalogReadinessProbe qnnCatalogReadinessProbe,
        IVitisAiCatalogReadinessProbe vitisAiCatalogReadinessProbe,
        Func<CancellationToken, Task<bool>>? isTensorRtRtxEnabled = null)
    {
        _openVino = openVino ?? throw new ArgumentNullException(nameof(openVino));
        _linuxRuntimeProbe = linuxRuntimeProbe ?? throw new ArgumentNullException(nameof(linuxRuntimeProbe));
        _nativeCudaTensorRtWindowsPolicy = nativeCudaTensorRtWindowsPolicy
            ?? throw new ArgumentNullException(nameof(nativeCudaTensorRtWindowsPolicy));
        _migraphxReadinessProbe = migraphxReadinessProbe ?? throw new ArgumentNullException(nameof(migraphxReadinessProbe));
        _dnnlReadinessProbe = dnnlReadinessProbe ?? throw new ArgumentNullException(nameof(dnnlReadinessProbe));
        _tensorRtRtxReadinessProbe = tensorRtRtxReadinessProbe ?? throw new ArgumentNullException(nameof(tensorRtRtxReadinessProbe));
        _openVinoCatalogReadinessProbe = openVinoCatalogReadinessProbe
            ?? throw new ArgumentNullException(nameof(openVinoCatalogReadinessProbe));
        _qnnCatalogReadinessProbe = qnnCatalogReadinessProbe
            ?? throw new ArgumentNullException(nameof(qnnCatalogReadinessProbe));
        _vitisAiCatalogReadinessProbe = vitisAiCatalogReadinessProbe
            ?? throw new ArgumentNullException(nameof(vitisAiCatalogReadinessProbe));
        _isTensorRtRtxEnabled = isTensorRtRtxEnabled ?? (static _ => Task.FromResult(false));
    }

    public async Task<IReadOnlyList<ExecutionProviderAvailability>> DiscoverAsync(
        HardwareProfile hardwareProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hardwareProfile);
        cancellationToken.ThrowIfCancellationRequested();

        bool allowNativeCudaTensorRtOnWindows = await _nativeCudaTensorRtWindowsPolicy
            .IsNativeProvidersAllowedOnWindowsAsync(cancellationToken)
            .ConfigureAwait(false);

        var availabilities = new List<ExecutionProviderAvailability>
        {
            new(ExecutionProviderKind.Cpu, true, "CPU execution is always available.")
        };

        bool isWindows = hardwareProfile.OperatingSystem.Equals("windows", StringComparison.OrdinalIgnoreCase);
        bool isMacOs = hardwareProfile.OperatingSystem.Equals("macos", StringComparison.OrdinalIgnoreCase);
        bool isLinux = hardwareProfile.OperatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase);
        bool isNvidiaGpu = hardwareProfile.GpuDescription?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ?? false;
        bool isAmdGpu = MigraphxProviderOrdering.ShouldPreferMigraphxOnAmdGpu(hardwareProfile);
        bool tensorRtRtxEnabled = await _isTensorRtRtxEnabled(cancellationToken).ConfigureAwait(false);

        // Windows providers
        bool directMlAvailable = isWindows && hardwareProfile.HasGpu;
        availabilities.Add(directMlAvailable
            ? new(ExecutionProviderKind.DirectMl, true,
                "Windows ML legacy DirectML route can be probed on this machine.")
            : new(ExecutionProviderKind.DirectMl, false,
                "DirectML legacy GPU probing requires Windows with a GPU-capable Windows ML path."));

        TensorRtRtxReadinessReport? tensorRtRtxReport = null;
        bool tensorRtAvailable = false;
        if (tensorRtRtxEnabled && (isWindows || isLinux) && isNvidiaGpu)
        {
            tensorRtRtxReport = await _tensorRtRtxReadinessProbe
                .ProbeAsync(allowProviderDownloads: false, cancellationToken)
                .ConfigureAwait(false);
            tensorRtAvailable = tensorRtRtxReport.IsReady;
        }
        availabilities.Add(tensorRtAvailable
            ? new(ExecutionProviderKind.TensorRTRtx, true,
                "NVIDIA GPU detected and TensorRT RTX EP ABI plugin is registered for installed-provider readiness.")
            : new(ExecutionProviderKind.TensorRTRtx, false,
                tensorRtRtxEnabled
                    ? ResolveTensorRtRtxUnavailableDetail(isWindows, isLinux, isNvidiaGpu, tensorRtRtxReport)
                    : "TensorRT RTX EP ABI plugin is disabled until the NVIDIA TensorRT RTX license is accepted in Model Manager."));

        availabilities.Add(await ResolveMigraphxAvailabilityAsync(
            isWindows,
            isLinux,
            isAmdGpu,
            cancellationToken).ConfigureAwait(false));

        // macOS providers
        availabilities.Add(isMacOs
            ? new(ExecutionProviderKind.CoreMl, true,
                "CoreML is always available on macOS 10.15+.")
            : new(ExecutionProviderKind.CoreMl, false,
                "CoreML is macOS-only."));

        // Native ORT CUDA / TensorRT (Linux always; Windows when advanced setting enabled)
        bool linuxNvidiaDriverLoaded = isLinux && isNvidiaGpu && _linuxRuntimeProbe.IsNvidiaDriverLoaded();
        bool linuxTensorRtAvailable = linuxNvidiaDriverLoaded && _linuxRuntimeProbe.IsNativeTensorRtAvailable();

        bool windowsNativeProbe = isWindows && isNvidiaGpu && allowNativeCudaTensorRtOnWindows;
        bool windowsCudaListed = windowsNativeProbe && CudaOrtProbe.IsCudaProviderListed();
        bool windowsTensorRtLibs = windowsNativeProbe && NativeTensorRtLibraryProbe.IsNativeTensorRtAvailable();
        bool windowsTensorRtListed = windowsNativeProbe && TensorRtRtxOrtProbe.IsNativeTensorRtProviderListed();
        bool windowsCudaAvailable = windowsCudaListed;
        bool windowsTensorRtAvailable = windowsTensorRtLibs && windowsTensorRtListed;

        availabilities.Add(ResolveCudaAvailability(
            isLinux,
            linuxNvidiaDriverLoaded,
            isWindows,
            isNvidiaGpu,
            allowNativeCudaTensorRtOnWindows,
            windowsCudaAvailable));

        availabilities.Add(ResolveTensorRtAvailability(
            isLinux,
            linuxNvidiaDriverLoaded,
            linuxTensorRtAvailable,
            isWindows,
            isNvidiaGpu,
            allowNativeCudaTensorRtOnWindows,
            windowsTensorRtAvailable,
            windowsTensorRtLibs,
            windowsTensorRtListed));

        DnnlReadinessReport dnnlReport = await _dnnlReadinessProbe
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        availabilities.Add(dnnlReport.IsReady
            ? new(ExecutionProviderKind.Dnnl, true, dnnlReport.Detail)
            : new(ExecutionProviderKind.Dnnl, false, dnnlReport.Detail));

        // OpenVINO — Linux and Windows both, via runtime availability check
        availabilities.Add(_openVino.IsAvailable
            ? new(ExecutionProviderKind.OpenVino, true, "OpenVINO runtime loaded.")
            : new(ExecutionProviderKind.OpenVino, false, "OpenVINO not installed."));

        availabilities.Add(await ResolveWinMlCatalogEpAvailabilityAsync(
            ExecutionProviderKind.OpenVinoCatalog,
            isWindows,
            _openVinoCatalogReadinessProbe.ProbeAsync,
            cancellationToken).ConfigureAwait(false));
        availabilities.Add(await ResolveWinMlCatalogEpAvailabilityAsync(
            ExecutionProviderKind.Qnn,
            isWindows,
            _qnnCatalogReadinessProbe.ProbeAsync,
            cancellationToken).ConfigureAwait(false));
        availabilities.Add(await ResolveWinMlCatalogEpAvailabilityAsync(
            ExecutionProviderKind.VitisAi,
            isWindows,
            _vitisAiCatalogReadinessProbe.ProbeAsync,
            cancellationToken).ConfigureAwait(false));

        return availabilities;
    }

    private static ExecutionProviderAvailability ResolveCudaAvailability(
        bool isLinux,
        bool linuxNvidiaDriverLoaded,
        bool isWindows,
        bool isNvidiaGpu,
        bool allowNativeCudaTensorRtOnWindows,
        bool windowsCudaAvailable)
    {
        if (isLinux)
        {
            return linuxNvidiaDriverLoaded
                ? new(ExecutionProviderKind.Cuda, true,
                    "NVIDIA GPU detected; CUDA EP verified at session bootstrap.")
                : new(ExecutionProviderKind.Cuda, false,
                    "No loaded NVIDIA driver detected on Linux.");
        }

        if (isWindows && allowNativeCudaTensorRtOnWindows)
        {
            return windowsCudaAvailable
                ? new(ExecutionProviderKind.Cuda, true,
                    "Native CUDAExecutionProvider is listed; use for explicit CUDA requests (advanced).")
                : new(ExecutionProviderKind.Cuda, false,
                    isNvidiaGpu
                        ? "CUDAExecutionProvider is not listed. Install NVIDIA drivers, CUDA, and ONNX Runtime GPU package."
                        : "Native CUDA on Windows requires an NVIDIA GPU.");
        }

        return new(ExecutionProviderKind.Cuda, false,
            isWindows
                ? "CUDA native EP on Windows is off by default. Enable it in Settings → Hardware (advanced)."
                : "CUDA native EP is available on Linux and Windows (when enabled).");
    }

    private static ExecutionProviderAvailability ResolveTensorRtAvailability(
        bool isLinux,
        bool linuxNvidiaDriverLoaded,
        bool linuxTensorRtAvailable,
        bool isWindows,
        bool isNvidiaGpu,
        bool allowNativeCudaTensorRtOnWindows,
        bool windowsTensorRtAvailable,
        bool windowsTensorRtLibs,
        bool windowsTensorRtListed)
    {
        if (isLinux)
        {
            return linuxTensorRtAvailable
                ? new(ExecutionProviderKind.TensorRt, true,
                    "NVIDIA driver and libnvinfer detected; TensorRT availability is verified at session bootstrap.")
                : new(ExecutionProviderKind.TensorRt, false,
                    ResolveLinuxTensorRtUnavailableDetail(isLinux: true, linuxNvidiaDriverLoaded));
        }

        if (isWindows && allowNativeCudaTensorRtOnWindows)
        {
            return windowsTensorRtAvailable
                ? new(ExecutionProviderKind.TensorRt, true,
                    "Native TensorRT libraries and TensorrtExecutionProvider are available (advanced).")
                : new(ExecutionProviderKind.TensorRt, false,
                    ResolveWindowsNativeTensorRtUnavailableDetail(isNvidiaGpu, windowsTensorRtLibs, windowsTensorRtListed));
        }

        return new(ExecutionProviderKind.TensorRt, false,
            isWindows
                ? "Native TensorRT on Windows is off by default. Enable it in Settings → Hardware (advanced), or use TensorRT RTX (WinML)."
                : "TensorRT native EP is available on Linux and Windows (when enabled).");
    }

    private static string ResolveWindowsNativeTensorRtUnavailableDetail(
        bool isNvidiaGpu,
        bool librariesPresent,
        bool ortProviderListed)
    {
        if (!isNvidiaGpu)
        {
            return "Native TensorRT on Windows requires an NVIDIA GPU.";
        }

        if (!librariesPresent && !ortProviderListed)
        {
            return "TensorRT libraries and TensorrtExecutionProvider are not available.";
        }

        if (!librariesPresent)
        {
            return "nvinfer.dll was not found on PATH or common install locations.";
        }

        return "TensorrtExecutionProvider is not listed by ONNX Runtime.";
    }

    private static string ResolveLinuxTensorRtUnavailableDetail(bool isLinux, bool linuxNvidiaDriverLoaded)
    {
        if (!isLinux)
        {
            return "TensorRT native EP is Linux-only.";
        }

        return linuxNvidiaDriverLoaded
            ? "libnvinfer not found; TensorRT native EP is unavailable."
            : "No loaded NVIDIA driver detected on Linux; TensorRT native EP is unavailable.";
    }

    private static string ResolveTensorRtRtxUnavailableDetail(
        bool isWindows,
        bool isLinux,
        bool isNvidiaGpu,
        TensorRtRtxReadinessReport? readinessReport)
    {
        if (!isWindows && !isLinux)
        {
            return "TensorRT RTX EP ABI plugin is not supported on this platform.";
        }

        if (!isNvidiaGpu)
        {
            return "TensorRT RTX path requires an NVIDIA GPU.";
        }

        return readinessReport?.Detail ?? "TensorRT RTX EP ABI plugin is not registered.";
    }

    private async Task<ExecutionProviderAvailability> ResolveMigraphxAvailabilityAsync(
        bool isWindows,
        bool isLinux,
        bool isAmdGpu,
        CancellationToken cancellationToken)
    {
        if (!isWindows && !isLinux)
        {
            return new(
                ExecutionProviderKind.Migraphx,
                false,
                "MIGraphX is available on Windows (WinML catalog) and Linux (system ROCm ORT build) only.");
        }

        if (isWindows && !isAmdGpu)
        {
            return new(
                ExecutionProviderKind.Migraphx,
                false,
                "MIGraphX WinML route requires an AMD GPU with the documented driver version.");
        }

        if (isLinux && !isAmdGpu)
        {
            return new(
                ExecutionProviderKind.Migraphx,
                false,
                "MIGraphX on Linux requires an AMD GPU and a ROCm/MIGraphX-capable ONNX Runtime build.");
        }

        MigraphxReadinessReport report = await _migraphxReadinessProbe
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        return new(
            ExecutionProviderKind.Migraphx,
            report.IsReady,
            report.Detail);
    }

    private static async Task<ExecutionProviderAvailability> ResolveWinMlCatalogEpAvailabilityAsync(
        ExecutionProviderKind provider,
        bool isWindows,
        Func<bool, CancellationToken, Task<WinMlCatalogReadinessReport>> probeAsync,
        CancellationToken cancellationToken)
    {
        if (!isWindows)
        {
            return new(provider, false, $"{provider} WinML catalog route is Windows-only.");
        }

        WinMlCatalogReadinessReport report = await probeAsync(false, cancellationToken).ConfigureAwait(false);
        return new(provider, report.IsReady, report.Detail);
    }

    private sealed class NullNativeCudaTensorRtWindowsPolicy : INativeCudaTensorRtWindowsPolicy
    {
        public static NullNativeCudaTensorRtWindowsPolicy Instance { get; } = new();

        public Task<bool> IsNativeProvidersAllowedOnWindowsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
