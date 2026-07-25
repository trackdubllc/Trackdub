#if WINDOWS
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Onnx.WindowsMl;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Composition.Tests.LipSynthesis;

/// <summary>
/// Hardware-agnostic execution-provider smoke. Loads a small cached ONNX model (SCRFD 500m), creates a real
/// ORT session on the requested EP, and runs a fixed-duration compute loop to prove the EP executes.
///
/// Opt-in via <c>TRACKDUB_EP_SMOKE</c>=&lt;CPU|DIRECTML|TENSORRTRTX|CUDA&gt; (one EP per run, so GPU utilization
/// can be attributed cleanly with an external sampler such as <c>nvidia-smi</c>). Optional
/// <c>TRACKDUB_EP_SMOKE_SECONDS</c> sets the loop budget (default 12). Skips when the env var is unset or the
/// SCRFD model is not cached. Results (throughput + any provider error) are written to
/// <c>%TEMP%\trackdub_ep_smoke.txt</c>; the path is also emitted to test output.
///
/// This is a deliberate EP-capability probe, independent of the LatentSync pipeline.
/// </summary>
public sealed class ExecutionProviderSmokeTest(Xunit.ITestOutputHelper output)
{
    private const string ScrfdModelId = "InsightFace/scrfd-500m";
    private const string ScrfdModelFile = "scrfd_500m.onnx";

    [Fact]
    [Trait("Category", "Diagnostic")]
    public async Task EpSmoke()
    {
        string? ep = Environment.GetEnvironmentVariable("TRACKDUB_EP_SMOKE");
        if (string.IsNullOrWhiteSpace(ep))
        {
            return; // opt-in only
        }
        ep = ep.ToUpperInvariant();

        string modelPath = Path.Combine(
            LipSynthesisIntegrationSupport.ResolveModelRoot(ScrfdModelId), ScrfdModelFile);
        if (!File.Exists(modelPath))
        {
            output.WriteLine($"SKIP: SCRFD model not cached at {modelPath}");
            return;
        }

        int seconds = int.TryParse(Environment.GetEnvironmentVariable("TRACKDUB_EP_SMOKE_SECONDS"), out int s) ? s : 12;
        // Write next to the test binary so the result is deterministically locatable regardless of the
        // test host's temp directory. Override with TRACKDUB_EP_SMOKE_OUT for a custom path.
        string outPath = Environment.GetEnvironmentVariable("TRACKDUB_EP_SMOKE_OUT")
            ?? Path.Combine(AppContext.BaseDirectory, "ep_smoke.txt");

        var sb = new StringBuilder();
        void Log(string m)
        {
            sb.AppendLine(m);
            output.WriteLine(m);
        }

        Log("============================================================");
        Log($"EP smoke: requested={ep} model={ScrfdModelFile} budget={seconds}s out={outPath}");

        // Native CUDA/TensorRT require the GPU onnxruntime.dll (shipped dormant under runtimes/win-x64/native).
        // For those, load that build instead of the Windows ML onnxruntime.dll; everything else uses WinML.
        bool nativeGpu = ep is "CUDA" or "TENSORRT";
        try
        {
            if (nativeGpu)
            {
                InstallGpuOnnxRuntimeResolver(Log);
            }
            else
            {
                WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
            }
        }
        catch (Exception ex)
        {
            Log($"resolver init THREW: {ex.GetType().Name}: {ex.Message}");
        }

        Log("=== GetAvailableProviders ===");
        try { foreach (string p in OrtEnv.Instance().GetAvailableProviders()) Log("  " + p); }
        catch (Exception ex) { Log($"  GetAvailableProviders THREW: {ex.Message}"); }

        SessionOptions options;
        try
        {
            options = await BuildOptionsAsync(ep, Log, TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            Log($"EP APPEND FAILED: {ex.GetType().Name}: {ex.Message}");
            File.AppendAllText(outPath, sb.ToString());
            throw;
        }

        try
        {
            using (options)
            using (var session = new InferenceSession(modelPath, options))
            {
                List<NamedOnnxValue> inputs = BuildZeroInputs(session, Log);

                // Warmup absorbs JIT / TensorRT engine build / DirectML shader compile.
                var warmup = Stopwatch.StartNew();
                using (session.Run(inputs)) { }
                warmup.Stop();
                Log($"warmup: {warmup.ElapsedMilliseconds} ms (includes EP engine/shader build)");

                var sw = Stopwatch.StartNew();
                int iters = 0;
                while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
                {
                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> r = session.Run(inputs);
                    iters++;
                }
                sw.Stop();

                double msPer = sw.Elapsed.TotalMilliseconds / Math.Max(1, iters);
                Log($"RESULT: ep={ep} OK iters={iters} elapsed={sw.Elapsed.TotalSeconds:F1}s " +
                    $"ms/iter={msPer:F2} (~{iters / sw.Elapsed.TotalSeconds:F1} it/s)");
            }
        }
        catch (Exception ex)
        {
            Log($"RESULT: ep={ep} SESSION/RUN FAILED: {ex.GetType().Name}: {ex.Message}");
            File.AppendAllText(outPath, sb.ToString());
            throw;
        }

        File.AppendAllText(outPath, sb.ToString());
    }

    private static async Task<SessionOptions> BuildOptionsAsync(string ep, Action<string> log, CancellationToken ct)
    {
        var options = new SessionOptions();
        switch (ep)
        {
            case "CPU":
                log("EP: CPU (default, no provider appended)");
                return options;

            case "DIRECTML":
            {
                OrtEpDevice dev = OrtEnv.Instance().GetEpDevices().First(d =>
                    d.HardwareDevice.Type == OrtHardwareDeviceType.GPU &&
                    string.Equals(d.EpName, "DmlExecutionProvider", StringComparison.OrdinalIgnoreCase));
                options.AppendExecutionProvider(OrtEnv.Instance(), new[] { dev }, new Dictionary<string, string>(StringComparer.Ordinal));
                log($"EP: DirectML device appended (EpName='{dev.EpName}')");
                return options;
            }

            case "TENSORRTRTX":
            {
                var storagePaths = new TrackdubStoragePaths();
                var settingsService = new JsonStudioSettingsService(storagePaths);
                string defaultDir = TensorRtRtxProviderConstants.GetDefaultInstallDirectory(storagePaths.UserDataRoot, "win-x64");
                var svc = new TensorRtRtxPluginService(
                    async c => (await settingsService.LoadAsync(c)).TensorRtRtxPluginDirectory,
                    _ => ValueTask.FromResult<string?>(defaultDir),
                    bundleEnsureAsync: null);
                TensorRtRtxBootstrapResult reg = await svc.EnsureRegisteredAsync(allowProviderDownloads: false, ct);
                log($"EP: TRT-RTX register Succeeded={reg.Succeeded} Detail={reg.Detail}");
                OrtEpDevice dev = OrtEnv.Instance().GetEpDevices().First(d =>
                    d.HardwareDevice.Type == OrtHardwareDeviceType.GPU &&
                    string.Equals(d.EpName, TensorRtRtxProviderConstants.PluginOrtExecutionProviderName, StringComparison.Ordinal));
                options.AppendExecutionProvider(OrtEnv.Instance(), new[] { dev }, new Dictionary<string, string>(StringComparer.Ordinal));
                log($"EP: TRT-RTX device appended (EpName='{dev.EpName}')");
                return options;
            }

            case "CUDA":
            {
                // GPU onnxruntime.dll loaded via InstallGpuOnnxRuntimeResolver. Try the legacy convenience export
                // first, then the provider-shared string API (modern ORT loads CUDA from onnxruntime_providers_cuda.dll).
                try
                {
                    options.AppendExecutionProvider_CUDA(deviceId: 0);
                    log("EP: CUDA appended via AppendExecutionProvider_CUDA(0)");
                }
                catch (EntryPointNotFoundException)
                {
                    options.AppendExecutionProvider("CUDAExecutionProvider",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["device_id"] = "0" });
                    log("EP: CUDA appended via AppendExecutionProvider(\"CUDAExecutionProvider\")");
                }
                return options;
            }

            case "TENSORRT":
            {
                try
                {
                    options.AppendExecutionProvider_Tensorrt(deviceId: 0);
                    log("EP: TensorRT appended via AppendExecutionProvider_Tensorrt(0)");
                }
                catch (EntryPointNotFoundException)
                {
                    options.AppendExecutionProvider("TensorrtExecutionProvider",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["device_id"] = "0" });
                    log("EP: TensorRT appended via AppendExecutionProvider(\"TensorrtExecutionProvider\")");
                }
                return options;
            }

            default:
                log($"EP: unknown '{ep}', falling back to CPU");
                return options;
        }
    }

    // Loads the GPU build of onnxruntime.dll (shipped dormant under runtimes/win-x64/native by Microsoft.ML.OnnxRuntime.Gpu)
    // instead of the Windows ML build that wins the output root. Must run before any OrtEnv use. This is exactly the
    // selection the Windows native-CUDA/TRT "advanced" path needs: the resolver must prefer this dll when enabled.
    private static void InstallGpuOnnxRuntimeResolver(Action<string> log)
    {
        // Override the native dir (e.g. point at the raw OnnxRuntime.Gpu NuGet package which the WinML build
        // clobbers in the normal output) to test whether the genuine CUDA-capable onnxruntime.dll registers CUDA.
        string nativeDir = Environment.GetEnvironmentVariable("TRACKDUB_EP_SMOKE_ORT_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        string ortPath = Path.Combine(nativeDir, "onnxruntime.dll");
        log($"GPU resolver: onnxruntime.dll={ortPath} exists={File.Exists(ortPath)}");
        NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (string name, Assembly asm, DllImportSearchPath? search) =>
        {
            if (name is "onnxruntime" or "onnxruntime.dll")
            {
                return NativeLibrary.Load(ortPath);
            }
            if (name is "onnxruntime_providers_shared" or "onnxruntime_providers_shared.dll")
            {
                return NativeLibrary.Load(Path.Combine(nativeDir, "onnxruntime_providers_shared.dll"));
            }
            return nint.Zero;
        });
    }

    private static List<NamedOnnxValue> BuildZeroInputs(InferenceSession session, Action<string> log)
    {
        var inputs = new List<NamedOnnxValue>();
        foreach (KeyValuePair<string, NodeMetadata> kv in session.InputMetadata)
        {
            int rank = kv.Value.Dimensions.Length;
            // Concrete sizes for dynamic dims: batch (index 0) -> 1; spatial dims of a 4D image tensor
            // (indices 2,3) -> 640 (SCRFD native size); any other dynamic dim -> 1.
            int[] dims = kv.Value.Dimensions
                .Select((d, i) => d > 0 ? d : (rank == 4 && i >= 2 ? 640 : 1))
                .ToArray();
            long len = dims.Aggregate(1L, (a, b) => a * b);
            var data = new float[len];
            inputs.Add(NamedOnnxValue.CreateFromTensor(kv.Key, new DenseTensor<float>(data, dims)));
            log($"input '{kv.Key}' dims=[{string.Join(",", dims)}] elems={len}");
        }
        return inputs;
    }
}
#endif
