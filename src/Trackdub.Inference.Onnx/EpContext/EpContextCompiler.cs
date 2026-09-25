using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.TensorRtRtx;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// AOT-compiles a source ONNX graph into an EP-context model via ORT <c>OrtModelCompilationOptions</c>
/// so the next session load deserializes engines instead of JIT-compiling them at create time.
/// Residual CUDA kernels still land in <c>nv_runtime_cache_path</c> on first load of the EP-context model.
/// </summary>
public sealed class EpContextCompiler
{
    private readonly ITensorRtRtxProviderBootstrap _tensorRtRtxBootstrap;

    public EpContextCompiler(ITensorRtRtxProviderBootstrap? tensorRtRtxBootstrap = null)
    {
        _tensorRtRtxBootstrap = tensorRtRtxBootstrap ?? CreateDefaultBootstrap();
    }

    /// <summary>
    /// Same wiring shape as <c>WindowsExecutionProviderBootstrapper</c>'s default: resolve the
    /// installed bundle under the user-data root. A bare <c>TensorRtRtxPluginService.Shared</c>
    /// has no install path and would report the plugin missing on a normal install.
    /// </summary>
    private static ITensorRtRtxProviderBootstrap CreateDefaultBootstrap()
    {
        string userDataRoot = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub");
        return TensorRtRtxProviderBootstrapFactory.CreateWithDefaultInstallPath(userDataRoot);
    }
    public sealed record CompileResult(
        bool Success,
        string? EpContextPath,
        double CompileMilliseconds,
        string? FailureReason,
        string SelectedProvider = "unknown");

    public async Task<CompileResult> CompileAsync(string sourceModelPath, string epContextPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourceModelPath))
        {
            return new CompileResult(false, null, 0, $"Source model not found: {sourceModelPath}");
        }

        // TensorRT RTX is a standalone ORT EP ABI plugin. Unless registered before the first OrtEnv
        // touch, GetEpDevices() lists no TRT device and AppendTensorRtRtxOrFallbackProvider silently
        // compiles under DirectML/CPU — producing a reserialized graph with zero EP-context nodes
        // (measured: +146KB, and a ~6s cold-load regression versus the source). Register here so
        // the compile cannot quietly AOT the wrong provider.
        TensorRtRtxBootstrapResult registration = await _tensorRtRtxBootstrap
            .EnsureRegisteredAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        if (!registration.Succeeded)
        {
            return new CompileResult(
                false,
                null,
                0,
                $"TensorRT RTX plugin registration failed, so EP-context AOT compile is unavailable: {registration.Detail}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(epContextPath))!);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
#if WINDOWS
            WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
            using SessionOptions sessionOptions = CreateCompileSessionOptions(out ExecutionProviderKind selectedProvider);
            string selectedLabel = selectedProvider.ToString();
            // EP context engines are provider-specific. Compiling on anything but TensorRT RTX
            // only reserializes (and graph-optimizes) the source — it cannot skip engine rebuild.
            if (selectedProvider is not ExecutionProviderKind.TensorRTRtx)
            {
                stopwatch.Stop();
                return new CompileResult(
                    false,
                    null,
                    stopwatch.Elapsed.TotalMilliseconds,
                    "EP-context AOT compile requires TensorRT RTX (the engine blob is TRT-RTX specific); "
                    + $"selected provider was '{selectedLabel}'.",
                    selectedLabel);
            }

            using var compileOptions = new OrtModelCompilationOptions(sessionOptions);
            compileOptions.SetInputModelPath(sourceModelPath);
            compileOptions.SetOutputModelPath(epContextPath);
            // Embed the compiled engine in the EP-context graph under 2GB (NVIDIA protobuf limit).
            // Externalize initializers only when embedding is off, so sub-2GB models stay one file.
            bool embed = EpContextArtifact.ShouldEmbedEpContext(sourceModelPath);
            compileOptions.SetEpContextEmbedMode(embed);
            if (!embed)
            {
                compileOptions.SetOutputModelExternalInitializersFile(
                    Path.GetFileNameWithoutExtension(epContextPath) + ".ext_init",
                    64);
            }

            compileOptions.CompileModel();
            stopwatch.Stop();

            // CompileModel() can succeed yet emit a plain reserialized graph (no com.microsoft.ep.context
            // nodes) when the EP performs no AOT capture. Loading such an artifact rebuilds engines just
            // like the source and adds parse overhead, so refuse to keep it — measured: +146KB of extra
            // Conv/Squeeze nodes and a ~6s cold-load regression versus the source graph.
            if (!TryContainsEpContextNodes(epContextPath))
            {
                TryDeletePartial(epContextPath);
                return new CompileResult(
                    false,
                    null,
                    stopwatch.Elapsed.TotalMilliseconds,
                    "CompileModel() produced no EP-context nodes (com.microsoft.ep.context / engine_data "
                    + $"markers absent) under provider '{selectedLabel}'. The output would not skip engine "
                    + "rebuild on load, so it was discarded.",
                    selectedLabel);
            }

            return new CompileResult(true, epContextPath, stopwatch.Elapsed.TotalMilliseconds, null, selectedLabel);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            stopwatch.Stop();
            TryDeletePartial(epContextPath);
            return new CompileResult(false, null, stopwatch.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// Known divergence from the production session path: production also passes each model's
    /// <c>nv_profile_min/max/opt_shapes</c> (see <c>OnnxExecutionSessionFactory</c> call sites),
    /// which this generic, per-family-agnostic compile path does not have. The compiled engine
    /// can therefore capture a different (TRT-RTX-auto-inferred) shape window than production's
    /// profiled one. <see cref="TryContainsEpContextNodes"/> only catches the zero-capture case,
    /// not a shape mismatch — accepted for now since threading per-model profiles through the
    /// generic warmup scan would need a family-to-profile lookup this path doesn't have.
    /// </summary>
    private static SessionOptions CreateCompileSessionOptions(out ExecutionProviderKind selectedProvider)
    {
        var options = new SessionOptions
        {
            // ORT_ENABLE_ALL re-fuses contrib ops (SkipLayerNormalization, BiasGelu) the TRT-RTX
            // parser cannot import — same reasoning as CreateBaseSessionOptions(tensorRtRtx: true).
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
        };
        selectedProvider = OnnxExecutionSessionFactory.AppendTensorRtRtxOrFallbackProvider(options);
        return options;
    }

    /// <summary>
    /// Cheap marker scan: ONNX protobuf stores op-type and attribute names as plain ASCII, and a
    /// real EP-context graph carries <c>com.microsoft.ep.context</c> nodes with an
    /// <c>engine_data</c> attribute. Reads in chunks so multi-hundred-MB models never land in RAM.
    /// </summary>
    private static bool TryContainsEpContextNodes(string modelPath)
    {
        byte[][] needles =
        [
            "com.microsoft.ep.context"u8.ToArray(),
            "engine_data"u8.ToArray(),
        ];
        const int chunkSize = 8 * 1024 * 1024;
        const int overlap = 32;

        try
        {
            using FileStream stream = File.OpenRead(modelPath);
            var buffer = new byte[chunkSize + overlap];
            int carry = 0;
            int read;
            while ((read = stream.Read(buffer, carry, chunkSize)) > 0)
            {
                int span = carry + read;
                foreach (byte[] needle in needles)
                {
                    if (IndexOf(buffer.AsSpan(0, span), needle) >= 0)
                    {
                        return true;
                    }
                }

                carry = Math.Min(overlap, span);
                Buffer.BlockCopy(buffer, span - carry, buffer, 0, carry);
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot verify; treat as missing so we never keep an unproven artifact.
            return false;
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private static void TryDeletePartial(string epContextPath)
    {
        try
        {
            if (File.Exists(epContextPath))
            {
                File.Delete(epContextPath);
            }

            // A rejected compile can still have written the external-initializers sidecar
            // before the EP-context-node check ran; remove it too so no orphan is left behind.
            string sidecarPath = EpContextArtifact.GetArtifactExternalInitializersPath(epContextPath);
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a rejected compile's partial output; failure to delete is non-fatal.
        }
    }
}
