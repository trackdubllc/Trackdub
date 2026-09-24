using System.Diagnostics;
using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Inference.Runtime.TensorRtRtx;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// One-shot warm pass: compile EP-context models for local ONNX graphs, then load each once so
/// <c>nv_runtime_cache_path</c> receives residual CUDA kernels. Re-run after install/update/driver change.
/// </summary>
public sealed class EpContextWarmupService : IEpContextWarmupService
{
    private static readonly string[] SkippedNameFragments =
    [
        // GenAI loads can hard-crash the host under TRT; never AOT those graphs.
        "phi-",
        "Phi-",
    ];

    private readonly IAppStoragePaths _storagePaths;
    private readonly IHardwareProfileProvider _hardwareProfileProvider;
    private readonly EpContextCompiler _compiler;

    public EpContextWarmupService(
        IAppStoragePaths storagePaths,
        IHardwareProfileProvider? hardwareProfileProvider = null,
        EpContextCompiler? compiler = null)
    {
        _storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
        _hardwareProfileProvider = hardwareProfileProvider ?? new MachineHardwareProfileProvider();
        _compiler = compiler ?? new EpContextCompiler();
    }

    public async Task<EpContextWarmReport> WarmAsync(
        IReadOnlyList<string>? sourceModelPaths = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        HardwareProfile hardware = await _hardwareProfileProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        string fingerprint =
            $"{hardware.NvidiaGpuArchitecture}|{(string.IsNullOrWhiteSpace(hardware.GpuDriverVersion) ? "unknown" : hardware.GpuDriverVersion)}|{Trackdub.Inference.Runtime.TensorRtRtx.TensorRtRtxProviderConstants.BundledFingerprintVersion}";

        IReadOnlyList<string> sources = sourceModelPaths is { Count: > 0 }
            ? sourceModelPaths
            : DiscoverLocalOnnxModels(_storagePaths.ModelCacheDirectory);

        var items = new List<EpContextWarmItem>(sources.Count);
        int compiled = 0, reused = 0, skipped = 0, failed = 0;

        foreach (string sourcePath in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkip(sourcePath))
            {
                skipped++;
                items.Add(new EpContextWarmItem(sourcePath, "skipped", null, 0, 0, "Excluded family or already an EP-context artifact."));
                continue;
            }

            var sourceInfo = new FileInfo(sourcePath);
            string sourceFingerprint = EpContextLoadPathResolver.CurrentEnvironmentFingerprint;
            EpContextArtifact.Stamp currentStamp = EpContextArtifact.CreateStamp(
                sourcePath,
                sourceInfo,
                sourceSha256: null,
                hardware.NvidiaGpuArchitecture.ToString(),
                hardware.GpuDriverVersion);

            string epContextPath = EpContextArtifact.GetEpContextPath(sourcePath);
            bool haveValid = EpContextArtifact.TryResolveValidLoadPath(sourcePath, sourceFingerprint) is not null;
            double compileMs = 0;
            string? detail = null;

            if (!haveValid)
            {
                progress?.Report($"compile {Path.GetFileName(sourcePath)}");
                EpContextCompiler.CompileResult compile = await _compiler
                    .CompileAsync(sourcePath, epContextPath, cancellationToken)
                    .ConfigureAwait(false);
                compileMs = compile.CompileMilliseconds;
                if (!compile.Success)
                {
                    failed++;
                    items.Add(new EpContextWarmItem(sourcePath, "failed", null, compileMs, 0, compile.FailureReason));
                    continue;
                }

                EpContextArtifact.WriteStamp(sourcePath, currentStamp);
                compiled++;
            }
            else
            {
                reused++;
                detail = "Existing EP-context artifact is current.";
            }

            // Residual JIT: load once so nv_runtime_cache_path stores CUDA kernels for this GPU.
            progress?.Report($"warm {Path.GetFileName(sourcePath)}");
            double warmMs = TryWarmLoad(epContextPath, cancellationToken, out string? warmFailure);
            if (warmFailure is not null)
            {
                failed++;
                items.Add(new EpContextWarmItem(sourcePath, "failed", epContextPath, compileMs, warmMs, warmFailure));
                continue;
            }

            items.Add(new EpContextWarmItem(
                sourcePath,
                haveValid ? "reused" : "compiled",
                epContextPath,
                compileMs,
                warmMs,
                detail));
        }

        return new EpContextWarmReport(
            _storagePaths.EngineCacheDirectory,
            fingerprint,
            compiled,
            reused,
            skipped,
            failed,
            items);
    }

    private static bool ShouldSkip(string sourcePath)
    {
        if (EpContextArtifact.IsEpContextPath(sourcePath))
        {
            return true;
        }

        string fileName = Path.GetFileName(sourcePath);
        return SkippedNameFragments.Any(fragment => fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> DiscoverLocalOnnxModels(string modelCacheDirectory)
    {
        if (!Directory.Exists(modelCacheDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(modelCacheDirectory, "*.onnx", SearchOption.AllDirectories)
            .Where(path => !EpContextArtifact.IsEpContextPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private double TryWarmLoad(string modelPath, CancellationToken cancellationToken, out string? failureReason)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
#if WINDOWS
            WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
            using SessionOptions options = new();
            OnnxExecutionSessionFactory.AppendTensorRtRtxOrFallbackProvider(options);
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
            using var session = new InferenceSession(modelPath, options);
            stopwatch.Stop();
            failureReason = null;
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            stopwatch.Stop();
            failureReason = ex.Message;
            return stopwatch.Elapsed.TotalMilliseconds;
        }
    }
}
