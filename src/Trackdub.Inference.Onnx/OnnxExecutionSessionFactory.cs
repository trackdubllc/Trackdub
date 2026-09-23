using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Dnnl;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.Pool;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif
using Trackdub.Inference.Onnx.Migraphx;
using Trackdub.Inference.Runtime.Migraphx;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Trackdub.Inference.Runtime.WinMlCatalog;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx;

internal static class OnnxExecutionSessionFactory
{
    // Canonical EP names returned by ORT device discovery. TensorRT RTX uses the standalone
    // EP ABI plugin, not the Windows ML catalog spelling. Single source of truth — must match the
    // name the plugin is registered under (see TensorRtRtxPluginService.RegistrationName); ORT reports
    // OrtEpDevice.EpName as that registration name.
    private const string TensorRtRtxExecutionProviderName = TensorRtRtxProviderConstants.PluginOrtExecutionProviderName;
    private const string DnnlExecutionProviderName = DnnlOrtProbe.OrtExecutionProviderName;
    private const string DnnlUpperExecutionProviderName = DnnlOrtProbe.OrtExecutionProviderNameUpper;
    private const string DirectMlExecutionProviderName = "DmlExecutionProvider";
    private const string DirectMlLongExecutionProviderName = "DirectMLExecutionProvider";
    private const string CacheRootEnvironmentVariable = "TRACKDUB_CACHE_ROOT";
    private const string EngineCacheRootEnvironmentVariable = "TRACKDUB_ENGINE_CACHE_ROOT";

    private static readonly object InitializeLock = new();
    private static int initializeCompleted;

    private static IExecutionProviderBootstrapper _bootstrapper = GetPlatformBootstrapper();
    private static IWindowsMlEpDevicePolicyProvider _devicePolicyProvider = NullWindowsMlEpDevicePolicyProvider.Instance;
    private static ILogger? _logger;
    private static readonly ConcurrentDictionary<string, byte> WarnedUnmappedCatalogEpNames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One-time process initialization. The first successful call wins; later calls are ignored.
    /// </summary>
    internal static void Initialize(
        IExecutionProviderBootstrapper bootstrapper,
        IWindowsMlEpDevicePolicyProvider? devicePolicyProvider = null,
        ILogger? logger = null)
    {
        lock (InitializeLock)
        {
            if (initializeCompleted != 0)
            {
                return;
            }

            _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
            _devicePolicyProvider = devicePolicyProvider ?? NullWindowsMlEpDevicePolicyProvider.Instance;
            _logger = logger;
            initializeCompleted = 1;
        }
    }

    /// <summary>
    /// Resets process-level static state for test isolation. Not for production use.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (InitializeLock)
        {
            initializeCompleted = 0;
            _bootstrapper = GetPlatformBootstrapper();
            _devicePolicyProvider = NullWindowsMlEpDevicePolicyProvider.Instance;
            _logger = null;
            WarnedUnmappedCatalogEpNames.Clear();
        }
    }

    private sealed record BootstrapContext(
        ExecutionProviders.ExecutionProviderBootstrapResult Bootstrap,
        WindowsMlExecutionDevicePolicy DevicePolicy,
        string RequestedProviderLabel);

    private sealed record DualOptionsSelections(
        SessionOptionsSelection Encoder,
        SessionOptionsSelection Decoder);

    private sealed record DualSessionMetadata(
        string SelectedProviderLabel,
        string? BootstrapDetail);

    private sealed record DualPooledLeasePair(
        SessionLease EncoderLease,
        SessionLease DecoderLease,
        string RequestedProviderLabel,
        string SelectedProviderLabel,
        string? BootstrapDetail);

    private static async Task<BootstrapContext> BootstrapForProviderAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        return new BootstrapContext(bootstrapResult, devicePolicy, FormatProviderLabel(provider));
    }

    private static DualOptionsSelections CreateDualOptionsSelections(
        ExecutionProviderKind provider,
        ExecutionProviders.ExecutionProviderBootstrapResult bootstrapResult,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions,
        bool enableEncoderCudaGraph = false)
    {
        ExecutionProviderKind sessionProvider =
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider);
        return new DualOptionsSelections(
            CreateSessionOptions(sessionProvider, devicePolicy, additionalTrtEncoderOptions, enableEncoderCudaGraph),
            CreateSessionOptions(sessionProvider, devicePolicy, additionalTrtDecoderOptions));
    }

    private static DualSessionMetadata ResolveDualSessionMetadata(
        ExecutionProviderKind requestedProvider,
        BootstrapContext bootstrap,
        DualOptionsSelections selections,
        InferenceSession encoderSession,
        InferenceSession decoderSession)
    {
        ExecutionProviderKind resolvedSelectedProvider = ResolveEffectiveDualSessionProvider(
            requestedProvider,
            encoderSession,
            decoderSession,
            selections.Encoder.SelectedProvider,
            selections.Decoder.SelectedProvider,
            bootstrap.DevicePolicy);
        string? encoderFallbackReason = BuildSessionOptionsFallbackReason(
            requestedProvider,
            ResolveEffectiveProviderKindFromSession(
                encoderSession,
                selections.Encoder.SelectedProvider,
                ShouldUseCatalogDevicePolicy(bootstrap.DevicePolicy, selections.Encoder.SelectedProvider)),
            selections.Encoder);
        string? decoderFallbackReason = BuildSessionOptionsFallbackReason(
            requestedProvider,
            ResolveEffectiveProviderKindFromSession(
                decoderSession,
                selections.Decoder.SelectedProvider,
                ShouldUseCatalogDevicePolicy(bootstrap.DevicePolicy, selections.Decoder.SelectedProvider)),
            selections.Decoder);
        return new DualSessionMetadata(
            FormatProviderLabel(resolvedSelectedProvider),
            FormatBootstrapDetail(
                bootstrap.Bootstrap.Detail,
                MergeFallbackReasons(encoderFallbackReason, decoderFallbackReason)));
    }

    private static (SessionPoolKey EncoderKey, SessionPoolKey DecoderKey) BuildDualPooledKeys(
        string engineFamily,
        string encoderModelPath,
        string decoderModelPath,
        DualOptionsSelections selections,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions,
        string? modelId,
        string? variant,
        bool enableEncoderCudaGraph = false)
    {
        string encoderFingerprint = BuildSessionOptionsFingerprint(
            selections.Encoder.SelectedProvider, devicePolicy, additionalTrtEncoderOptions, enableEncoderCudaGraph);
        string decoderFingerprint = BuildSessionOptionsFingerprint(
            selections.Decoder.SelectedProvider, devicePolicy, additionalTrtDecoderOptions);
        return (
            SessionPoolKey.ForEncoder(
                engineFamily, encoderModelPath, selections.Encoder.SelectedProvider,
                modelId, variant, optionsFingerprint: encoderFingerprint),
            SessionPoolKey.ForDecoder(
                engineFamily, decoderModelPath, selections.Decoder.SelectedProvider,
                modelId, variant, optionsFingerprint: decoderFingerprint));
    }

    private static async Task<DualPooledLeasePair> AcquireDualPooledSessionsAsync(
        ExecutionProviderKind requestedProvider,
        BootstrapContext bootstrap,
        DualOptionsSelections selections,
        string encoderModelPath,
        string decoderModelPath,
        SessionPoolKey encoderKey,
        SessionPoolKey decoderKey,
        InferenceSessionPool pool,
        CancellationToken cancellationToken,
        Func<string, SessionOptions, InferenceSession>? sessionFactory)
    {
        SessionLease? encoderPoolLease = null;
        SessionLease? decoderPoolLease = null;
        try
        {
            encoderPoolLease = await pool
                .GetLeaseAsync(
                    encoderKey,
                    ct => Task.FromResult(CreateSession(encoderModelPath, selections.Encoder.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            decoderPoolLease = await pool
                .GetLeaseAsync(
                    decoderKey,
                    ct => Task.FromResult(CreateSession(decoderModelPath, selections.Decoder.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);

            DualSessionMetadata metadata = ResolveDualSessionMetadata(
                requestedProvider, bootstrap, selections,
                encoderPoolLease.Session, decoderPoolLease.Session);
            return new DualPooledLeasePair(
                encoderPoolLease, decoderPoolLease,
                bootstrap.RequestedProviderLabel,
                metadata.SelectedProviderLabel,
                metadata.BootstrapDetail);
        }
        catch
        {
            encoderPoolLease?.Dispose();
            decoderPoolLease?.Dispose();
            throw;
        }
    }

    public static async Task<SingleSessionLease> CreateSingleAsync(
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        bool allowTrtInitFallback = true)
    {
        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection sessionOptionsSelection = CreateSessionOptions(
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider),
            devicePolicy,
            additionalTrtOptions);
        InferenceSession? session = null;
        try
        {
            (session, sessionOptionsSelection) = CreateInferenceSessionWithTrtInitFallback(
                modelPath,
                sessionOptionsSelection,
                devicePolicy,
                additionalTrtOptions,
                sessionFactory: null,
                cancellationToken,
                allowTrtInitFallback);
            bool useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, sessionOptionsSelection.SelectedProvider);
            ExecutionProviderKind effectiveProvider = ResolveEffectiveProviderKindFromSession(
                session,
                sessionOptionsSelection.SelectedProvider,
                useCatalogDevicePolicy);
            string selectedProvider = FormatProviderLabel(effectiveProvider);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                BuildSessionOptionsFallbackReason(provider, effectiveProvider, sessionOptionsSelection));
            return new SingleSessionLease(
                session,
                requestedProvider,
                selectedProvider,
                bootstrapDetail);
        }
        catch
        {
            session?.Dispose();
            throw;
        }
        finally
        {
            sessionOptionsSelection.Options.Dispose();
        }
    }

    private static InferenceSession CreateSession(
        string modelPath,
        SessionOptions options,
        Func<string, SessionOptions, InferenceSession>? sessionFactory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var phase = BenchmarkPhaseCapture.Start("onnx-session-create");
        return sessionFactory is null
            ? new InferenceSession(modelPath, options)
            : sessionFactory(modelPath, options);
    }

    /// <summary>
    /// Creates an ORT session; when TensorRT RTX was selected and init fails due to unsupported
    /// ops/kernels, optionally retries once with DirectML (Windows) then CPU and records FallbackReason.
    /// Pass <paramref name="allowTrtInitFallback"/> <see langword="false"/> for hard-pin routes
    /// (<c>RequirePreferredExecutionProvider</c> / CLI <c>--require-execution-provider</c>).
    /// </summary>
    internal static (InferenceSession Session, SessionOptionsSelection Selection) CreateInferenceSessionWithTrtInitFallback(
        string modelPath,
        SessionOptionsSelection initialSelection,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        Func<string, SessionOptions, InferenceSession>? sessionFactory,
        CancellationToken cancellationToken,
        bool allowTrtInitFallback = true)
    {
        try
        {
            InferenceSession session = CreateSession(
                modelPath,
                initialSelection.Options,
                sessionFactory,
                cancellationToken);
            return (session, initialSelection);
        }
        catch (Exception ex) when (
            allowTrtInitFallback
            && initialSelection.SelectedProvider is ExecutionProviderKind.TensorRTRtx
            && LooksLikeTrtSessionInitFailure(ex))
        {
            string trtError = SummarizeExceptionMessage(ex);
            Exception? lastFailure = ex;

            foreach (SessionOptionsSelection fallbackSelection in EnumerateTrtInitFallbackProviders()
                         .Select(fallbackProvider => CreateSessionOptions(
                             fallbackProvider,
                             devicePolicy,
                             additionalTrtOptions: null)))
            {
                try
                {
                    InferenceSession session = CreateSession(
                        modelPath,
                        fallbackSelection.Options,
                        sessionFactory,
                        cancellationToken);
                    // Transfer ownership: dispose the failed TRT options; caller owns the fallback Options.
                    initialSelection.Options.Dispose();
                    string effectiveLabel = FormatProviderLabel(fallbackSelection.SelectedProvider);
                    string trtFallbackReason =
                        $"TensorRT RTX session init failed ({trtError}); fell back to {effectiveLabel}.";
                    return (
                        session,
                        new SessionOptionsSelection(
                            fallbackSelection.Options,
                            fallbackSelection.SelectedProvider,
                            MergeFallbackReasons(trtFallbackReason, fallbackSelection.FallbackReason)));
                }
                catch (Exception fallbackEx)
                {
                    fallbackSelection.Options.Dispose();
                    if (!IsRecoverableTrtFallbackInitFailure(fallbackEx))
                    {
                        throw;
                    }

                    lastFailure = fallbackEx;
                }
            }

            // Leave initialSelection.Options for the caller to dispose.
            throw lastFailure ?? ex;
        }
    }

    private static IEnumerable<ExecutionProviderKind> EnumerateTrtInitFallbackProviders()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ExecutionProviderKind.DirectMl;
        }

        yield return ExecutionProviderKind.Cpu;
    }

    internal static bool LooksLikeTrtSessionInitFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            if (message.Contains("Kernel not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ModelImporter", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No graph will run on TensorRT", StringComparison.OrdinalIgnoreCase)
                || message.Contains("NvTensorRTRTX", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TensorRT-RTX", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TensorRT RTX", StringComparison.OrdinalIgnoreCase)
                || message.Contains("transformer_memcpy", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ProcessInitializers", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRecoverableTrtFallbackInitFailure(Exception exception) =>
        LooksLikeTrtSessionInitFailure(exception)
        || exception is OnnxRuntimeException
        || exception is InvalidOperationException
        || exception is DllNotFoundException
        || exception is EntryPointNotFoundException;

    private static string SummarizeExceptionMessage(Exception exception)
    {
        string message = exception.GetBaseException().Message.ReplaceLineEndings(" ");
        const int maxLength = 240;
        return message.Length <= maxLength ? message : message[..maxLength] + "...";
    }

    public static async Task<WhisperSessionLease> CreateWhisperAsync(
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null)
    {
        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        InferenceSession? encoderSession = null;
        InferenceSession? decoderSession = null;
        try
        {
            encoderSession = new InferenceSession(encoderModelPath, encoderOptions);
            decoderSession = new InferenceSession(decoderModelPath, decoderOptions);
            DualSessionMetadata metadata = ResolveDualSessionMetadata(
                provider, bootstrap, selections, encoderSession, decoderSession);

            return new WhisperSessionLease(
                encoderSession,
                decoderSession,
                bootstrap.RequestedProviderLabel,
                metadata.SelectedProviderLabel,
                metadata.BootstrapDetail);
        }
        catch
        {
            encoderSession?.Dispose();
            decoderSession?.Dispose();
            throw;
        }
    }

    public static async Task<OpusSessionLease> CreateOpusAsync(
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null)
    {
        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        InferenceSession? encoderSession = null;
        InferenceSession? decoderSession = null;
        try
        {
            encoderSession = new InferenceSession(encoderModelPath, encoderOptions);
            decoderSession = new InferenceSession(decoderModelPath, decoderOptions);
            DualSessionMetadata metadata = ResolveDualSessionMetadata(
                provider, bootstrap, selections, encoderSession, decoderSession);

            return new OpusSessionLease(
                encoderSession,
                decoderSession,
                bootstrap.RequestedProviderLabel,
                metadata.SelectedProviderLabel,
                metadata.BootstrapDetail);
        }
        catch
        {
            encoderSession?.Dispose();
            decoderSession?.Dispose();
            throw;
        }
    }

    // ── Pooled factory methods ─────────────────────────────────────────────
    //
    // These methods create ONNX sessions and register them with the shared
    // InferenceSessionPool so that sessions are reused across engine calls.
    // Metadata (selected provider, bootstrap detail) is captured at creation
    // time and cached alongside the pool entry for subsequent pool hits.

    private sealed class SessionOptionsDisposeHolder : IDisposable
    {
        public SessionOptionsDisposeHolder(SessionOptions current)
        {
            Current = current;
        }

        public SessionOptions Current { get; set; }

        public void Dispose()
        {
            Current.Dispose();
        }
    }

    public static async Task<SingleSessionLease> CreatePooledSingleAsync(
        string engineFamily,
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null,
        bool allowTrtInitFallback = true)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection optionsSelection = CreateSessionOptions(
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider),
            devicePolicy,
            additionalTrtOptions);

        // Options ownership may transfer to a replacement SessionOptionsSelection when TRT init
        // falls back; keep a movable dispose target managed by a `using` scope.
        using var optionsHolder = new SessionOptionsDisposeHolder(optionsSelection.Options);
        SessionOptionsSelection leaseSelection = optionsSelection;
        bool useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, optionsSelection.SelectedProvider);
        ExecutionProviderKind optionsSelectedProvider = optionsSelection.SelectedProvider;

        string optionsFingerprint =
            $"{BuildSessionOptionsFingerprint(optionsSelectedProvider, devicePolicy, additionalTrtOptions)}|trt-init-fallback:{allowTrtInitFallback}";
        SessionPoolKey key = SessionPoolKey.ForSingle(
            engineFamily,
            modelPath,
            optionsSelection.SelectedProvider,
            modelId,
            variant,
            optionsFingerprint: optionsFingerprint);

        SessionLease? poolLease = null;
        try
        {
            poolLease = await pool
                .GetLeaseAsync(
                    key,
                    ct =>
                    {
                        (InferenceSession session, SessionOptionsSelection selection) =
                            CreateInferenceSessionWithTrtInitFallback(
                                modelPath,
                                optionsSelection,
                                devicePolicy,
                                additionalTrtOptions,
                                sessionFactory,
                                ct,
                                allowTrtInitFallback);
                        leaseSelection = selection;
                        optionsSelectedProvider = selection.SelectedProvider;
                        useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, selection.SelectedProvider);
                        if (!ReferenceEquals(selection.Options, optionsHolder.Current))
                        {
                            optionsHolder.Current = selection.Options;
                        }

                        return Task.FromResult(session);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            ExecutionProviderKind effectiveProvider = ResolveEffectiveProviderKindFromSession(
                poolLease.Session,
                optionsSelectedProvider,
                useCatalogDevicePolicy);
            string selectedProvider = FormatProviderLabel(effectiveProvider);
            string? epFallbackReason = BuildSessionOptionsFallbackReason(provider, effectiveProvider, leaseSelection);
            string? bootstrapDetail = FormatBootstrapDetail(bootstrapResult.Detail, epFallbackReason);

            return new SingleSessionLease(poolLease.Session, requestedProvider, selectedProvider, bootstrapDetail)
            {
                PoolLease = poolLease
            };
        }
        catch
        {
            poolLease?.Dispose();
            throw;
        }
    }

    public static async Task<WhisperSessionLease> CreatePooledWhisperAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        (SessionPoolKey encoderKey, SessionPoolKey decoderKey) = BuildDualPooledKeys(
            engineFamily, encoderModelPath, decoderModelPath, selections,
            bootstrap.DevicePolicy, additionalTrtEncoderOptions, additionalTrtDecoderOptions,
            modelId, variant);

        DualPooledLeasePair pair = await AcquireDualPooledSessionsAsync(
            provider, bootstrap, selections,
            encoderModelPath, decoderModelPath,
            encoderKey, decoderKey, pool, cancellationToken, sessionFactory)
            .ConfigureAwait(false);

        return new WhisperSessionLease(
            pair.EncoderLease.Session, pair.DecoderLease.Session,
            pair.RequestedProviderLabel, pair.SelectedProviderLabel, pair.BootstrapDetail)
        {
            EncoderPoolLease = pair.EncoderLease,
            DecoderPoolLease = pair.DecoderLease
        };
    }

    public static async Task<Qwen3AsrSessionLease> CreatePooledQwen3AsrAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderInitModelPath,
        string decoderStepModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection encoderOptionsSelection = CreateSessionOptions(
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider),
            devicePolicy,
            additionalTrtEncoderOptions);
        SessionOptionsSelection decoderOptionsSelection = CreateSessionOptions(
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider),
            devicePolicy,
            additionalTrtDecoderOptions);

        using SessionOptions encoderOptions = encoderOptionsSelection.Options;
        using SessionOptions decoderInitOptions = decoderOptionsSelection.Options;
        using SessionOptions decoderStepOptions = decoderOptionsSelection.Options;

        ExecutionProviderKind encoderOptionsSelectedProvider = encoderOptionsSelection.SelectedProvider;
        ExecutionProviderKind decoderOptionsSelectedProvider = decoderOptionsSelection.SelectedProvider;

        string encoderOptionsFingerprint = BuildSessionOptionsFingerprint(encoderOptionsSelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        string decoderOptionsFingerprint = BuildSessionOptionsFingerprint(decoderOptionsSelectedProvider, devicePolicy, additionalTrtDecoderOptions);

        SessionPoolKey encoderKey = SessionPoolKey.ForEncoder(
            engineFamily,
            encoderModelPath,
            encoderOptionsSelection.SelectedProvider,
            modelId,
            variant,
            optionsFingerprint: encoderOptionsFingerprint);
        SessionPoolKey decoderInitKey = SessionPoolKey.ForDecoderInit(
            engineFamily,
            decoderInitModelPath,
            decoderOptionsSelection.SelectedProvider,
            modelId,
            variant,
            optionsFingerprint: decoderOptionsFingerprint);
        SessionPoolKey decoderStepKey = SessionPoolKey.ForDecoderStep(
            engineFamily,
            decoderStepModelPath,
            decoderOptionsSelection.SelectedProvider,
            modelId,
            variant,
            optionsFingerprint: decoderOptionsFingerprint);

        SessionLease? encoderPoolLease = null;
        SessionLease? decoderInitPoolLease = null;
        SessionLease? decoderStepPoolLease = null;
        try
        {
            encoderPoolLease = await pool
                .GetLeaseAsync(
                    encoderKey,
                    ct => Task.FromResult(CreateSession(encoderModelPath, encoderOptionsSelection.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            decoderInitPoolLease = await pool
                .GetLeaseAsync(
                    decoderInitKey,
                    ct => Task.FromResult(CreateSession(decoderInitModelPath, decoderInitOptions, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            decoderStepPoolLease = await pool
                .GetLeaseAsync(
                    decoderStepKey,
                    ct => Task.FromResult(CreateSession(decoderStepModelPath, decoderStepOptions, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);

            ExecutionProviderKind effectiveProvider = ResolveEffectiveTripleSessionProvider(
                provider,
                encoderPoolLease.Session,
                decoderInitPoolLease.Session,
                decoderStepPoolLease.Session,
                encoderOptionsSelectedProvider,
                decoderOptionsSelectedProvider,
                devicePolicy);
            string selectedProvider = FormatProviderLabel(effectiveProvider);
            string? encoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    encoderPoolLease.Session,
                    encoderOptionsSelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, encoderOptionsSelectedProvider)),
                encoderOptionsSelection);
            string? decoderInitFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    decoderInitPoolLease.Session,
                    decoderOptionsSelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider)),
                decoderOptionsSelection);
            string? decoderStepFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    decoderStepPoolLease.Session,
                    decoderOptionsSelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider)),
                decoderOptionsSelection);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(
                    encoderFallbackReason,
                    MergeFallbackReasons(decoderInitFallbackReason, decoderStepFallbackReason)));

            return new Qwen3AsrSessionLease(
                encoderPoolLease.Session,
                decoderInitPoolLease.Session,
                decoderStepPoolLease.Session,
                requestedProvider,
                selectedProvider,
                bootstrapDetail)
            {
                EncoderPoolLease = encoderPoolLease,
                DecoderInitPoolLease = decoderInitPoolLease,
                DecoderStepPoolLease = decoderStepPoolLease,
            };
        }
        catch
        {
            encoderPoolLease?.Dispose();
            decoderInitPoolLease?.Dispose();
            decoderStepPoolLease?.Dispose();
            throw;
        }
    }

    public static async Task<LatentSyncSessionLease> CreatePooledLatentSyncAsync(
        string engineFamily,
        string unetModelPath,
        string vaeEncoderModelPath,
        string vaeDecoderModelPath,
        string whisperEncoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        ExecutionProviderKind sessionProvider = ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider);
        SessionOptionsSelection unetOptionsSelection = CreateSessionOptions(sessionProvider, devicePolicy, null);
        SessionOptionsSelection vaeEncOptionsSelection = CreateSessionOptions(sessionProvider, devicePolicy, null);
        SessionOptionsSelection vaeDecOptionsSelection = CreateSessionOptions(sessionProvider, devicePolicy, null);
        SessionOptionsSelection whisperOptionsSelection = CreateSessionOptions(sessionProvider, devicePolicy, null);
        using SessionOptions unetOptions = unetOptionsSelection.Options;
        using SessionOptions vaeEncOptions = vaeEncOptionsSelection.Options;
        using SessionOptions vaeDecOptions = vaeDecOptionsSelection.Options;
        using SessionOptions whisperOptions = whisperOptionsSelection.Options;

        ExecutionProviderKind unetSelectedProvider = unetOptionsSelection.SelectedProvider;
        ExecutionProviderKind whisperSelectedProvider = whisperOptionsSelection.SelectedProvider;

        SessionPoolKey unetKey = SessionPoolKey.ForLatentSyncUNet(
            unetModelPath, unetOptionsSelection.SelectedProvider, modelId, variant);
        SessionPoolKey vaeEncKey = SessionPoolKey.ForLatentSyncVaeEncoder(
            vaeEncoderModelPath, vaeEncOptionsSelection.SelectedProvider, modelId, variant);
        SessionPoolKey vaeDecKey = SessionPoolKey.ForLatentSyncVaeDecoder(
            vaeDecoderModelPath, vaeDecOptionsSelection.SelectedProvider, modelId, variant);
        SessionPoolKey whisperKey = SessionPoolKey.ForLatentSyncWhisperEncoder(
            whisperEncoderModelPath, whisperOptionsSelection.SelectedProvider, modelId, variant);

        SessionLease? unetPoolLease = null;
        SessionLease? vaeEncPoolLease = null;
        SessionLease? vaeDecPoolLease = null;
        SessionLease? whisperPoolLease = null;
        try
        {
            unetPoolLease = await pool
                .GetLeaseAsync(
                    unetKey,
                    ct => Task.FromResult(CreateSession(unetModelPath, unetOptions, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            vaeEncPoolLease = await pool
                .GetLeaseAsync(
                    vaeEncKey,
                    ct => Task.FromResult(CreateSession(vaeEncoderModelPath, vaeEncOptions, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            vaeDecPoolLease = await pool
                .GetLeaseAsync(
                    vaeDecKey,
                    ct => Task.FromResult(CreateSession(vaeDecoderModelPath, vaeDecOptions, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            whisperPoolLease = await pool
                .GetLeaseAsync(
                    whisperKey,
                    ct => Task.FromResult(CreateSession(whisperEncoderModelPath, whisperOptions, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);

            ExecutionProviderKind effective = ResolveEffectiveQuadSessionProvider(
                provider,
                unetPoolLease.Session, vaeEncPoolLease.Session,
                vaeDecPoolLease.Session, whisperPoolLease.Session,
                unetSelectedProvider, whisperSelectedProvider,
                devicePolicy);
            string selectedProvider = FormatProviderLabel(effective);
            string? unetFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    unetPoolLease.Session,
                    unetOptionsSelection.SelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, unetOptionsSelection.SelectedProvider)),
                unetOptionsSelection);
            string? vaeEncFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    vaeEncPoolLease.Session,
                    vaeEncOptionsSelection.SelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, vaeEncOptionsSelection.SelectedProvider)),
                vaeEncOptionsSelection);
            string? vaeDecFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    vaeDecPoolLease.Session,
                    vaeDecOptionsSelection.SelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, vaeDecOptionsSelection.SelectedProvider)),
                vaeDecOptionsSelection);
            string? whisperFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    whisperPoolLease.Session,
                    whisperOptionsSelection.SelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, whisperOptionsSelection.SelectedProvider)),
                whisperOptionsSelection);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(
                    MergeFallbackReasons(unetFallbackReason, vaeEncFallbackReason),
                    MergeFallbackReasons(vaeDecFallbackReason, whisperFallbackReason)));

            return new LatentSyncSessionLease(
                unetPoolLease.Session,
                vaeEncPoolLease.Session,
                vaeDecPoolLease.Session,
                whisperPoolLease.Session,
                requestedProvider,
                selectedProvider,
                bootstrapDetail)
            {
                UNetPoolLease = unetPoolLease,
                VaeEncoderPoolLease = vaeEncPoolLease,
                VaeDecoderPoolLease = vaeDecPoolLease,
                WhisperEncoderPoolLease = whisperPoolLease,
            };
        }
        catch
        {
            unetPoolLease?.Dispose();
            vaeEncPoolLease?.Dispose();
            vaeDecPoolLease?.Dispose();
            whisperPoolLease?.Dispose();
            throw;
        }
    }

    public static async Task<NemotronAsrSessionLease> CreatePooledNemotronAsrAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderJointModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        // CUDA graph replay reads and writes the device addresses captured on the first run, so every
        // binding must stay at a fixed address across runs. The encoder does not meet that yet:
        // NemotronAsrGreedyDecoder rebinds fresh processed_signal / length / prompt OrtValues every
        // chunk, and CachePingPongBuffer alternates the cache input/output buffers between two
        // host allocations. Keep capture off for both sessions until the full binding set uses fixed
        // device buffers and is validated across multiple chunks on TRT-RTX hardware.
        const bool enableEncoderCudaGraph = false;
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions, enableEncoderCudaGraph);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        (SessionPoolKey encoderKey, SessionPoolKey decoderKey) = BuildDualPooledKeys(
            engineFamily, encoderModelPath, decoderJointModelPath, selections,
            bootstrap.DevicePolicy, additionalTrtEncoderOptions, additionalTrtDecoderOptions,
            modelId, variant, enableEncoderCudaGraph);

        DualPooledLeasePair pair = await AcquireDualPooledSessionsAsync(
            provider, bootstrap, selections,
            encoderModelPath, decoderJointModelPath,
            encoderKey, decoderKey, pool, cancellationToken, sessionFactory)
            .ConfigureAwait(false);

        return new NemotronAsrSessionLease(
            pair.EncoderLease.Session,
            pair.DecoderLease.Session,
            pair.RequestedProviderLabel,
            pair.SelectedProviderLabel,
            pair.BootstrapDetail)
        {
            EncoderPoolLease = pair.EncoderLease,
            DecoderJointPoolLease = pair.DecoderLease,
        };
    }

    private static ExecutionProviderKind ResolveEffectiveTripleSessionProvider(
        ExecutionProviderKind requestedProvider,
        InferenceSession encoderSession,
        InferenceSession decoderInitSession,
        InferenceSession decoderStepSession,
        ExecutionProviderKind encoderOptionsSelectedProvider,
        ExecutionProviderKind decoderOptionsSelectedProvider,
        WindowsMlExecutionDevicePolicy devicePolicy)
    {
        ExecutionProviderKind encoderEffective = ResolveEffectiveProviderKindFromSession(
            encoderSession,
            encoderOptionsSelectedProvider,
            ShouldUseCatalogDevicePolicy(devicePolicy, encoderOptionsSelectedProvider));
        ExecutionProviderKind decoderInitEffective = ResolveEffectiveProviderKindFromSession(
            decoderInitSession,
            decoderOptionsSelectedProvider,
            ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider));
        ExecutionProviderKind decoderStepEffective = ResolveEffectiveProviderKindFromSession(
            decoderStepSession,
            decoderOptionsSelectedProvider,
            ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider));

        if (encoderEffective != decoderInitEffective || encoderEffective != decoderStepEffective)
        {
            return ExecutionProviderKind.Cpu;
        }

        return encoderEffective;
    }

    private static ExecutionProviderKind ResolveEffectiveQuadSessionProvider(
        ExecutionProviderKind requestedProvider,
        InferenceSession sessionA,
        InferenceSession sessionB,
        InferenceSession sessionC,
        InferenceSession sessionD,
        ExecutionProviderKind abOptionsSelectedProvider,
        ExecutionProviderKind cdOptionsSelectedProvider,
        WindowsMlExecutionDevicePolicy devicePolicy)
    {
        ExecutionProviderKind aEffective = ResolveEffectiveProviderKindFromSession(
            sessionA, abOptionsSelectedProvider, ShouldUseCatalogDevicePolicy(devicePolicy, abOptionsSelectedProvider));
        ExecutionProviderKind bEffective = ResolveEffectiveProviderKindFromSession(
            sessionB, abOptionsSelectedProvider, ShouldUseCatalogDevicePolicy(devicePolicy, abOptionsSelectedProvider));
        ExecutionProviderKind cEffective = ResolveEffectiveProviderKindFromSession(
            sessionC, cdOptionsSelectedProvider, ShouldUseCatalogDevicePolicy(devicePolicy, cdOptionsSelectedProvider));
        ExecutionProviderKind dEffective = ResolveEffectiveProviderKindFromSession(
            sessionD, cdOptionsSelectedProvider, ShouldUseCatalogDevicePolicy(devicePolicy, cdOptionsSelectedProvider));

        if (aEffective != bEffective || aEffective != cEffective || aEffective != dEffective)
        {
            return ExecutionProviderKind.Cpu;
        }

        return aEffective;
    }

    public static async Task<OpusSessionLease> CreatePooledOpusAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        (SessionPoolKey encoderKey, SessionPoolKey decoderKey) = BuildDualPooledKeys(
            engineFamily, encoderModelPath, decoderModelPath, selections,
            bootstrap.DevicePolicy, additionalTrtEncoderOptions, additionalTrtDecoderOptions,
            modelId, variant);

        DualPooledLeasePair pair = await AcquireDualPooledSessionsAsync(
            provider, bootstrap, selections,
            encoderModelPath, decoderModelPath,
            encoderKey, decoderKey, pool, cancellationToken, sessionFactory)
            .ConfigureAwait(false);

        return new OpusSessionLease(
            pair.EncoderLease.Session, pair.DecoderLease.Session,
            pair.RequestedProviderLabel, pair.SelectedProviderLabel, pair.BootstrapDetail)
        {
            EncoderPoolLease = pair.EncoderLease,
            DecoderPoolLease = pair.DecoderLease
        };
    }

    internal sealed record SessionOptionsFactoryBundle(
        Func<SessionOptions> CreateOptions,
        ExecutionProviderKind RequestedProvider,
        ExecutionProviderKind SelectedProvider,
        string BootstrapDetail);

    internal static async Task<SessionOptionsFactoryBundle> CreateSessionOptionsFactoryAsync(
        ExecutionProviderKind requestedProvider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null)
    {
        var bootstrapResult = await _bootstrapper.BootstrapAsync(requestedProvider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        ExecutionProviderKind sessionProvider = ResolveSessionOptionsProvider(
            requestedProvider,
            bootstrapResult.SelectedProvider);
        SessionOptionsSelection probeSelection = CreateSessionOptions(
            sessionProvider,
            devicePolicy,
            additionalTrtOptions);
        ExecutionProviderKind selectedProvider = probeSelection.SelectedProvider;
        probeSelection.Options.Dispose();

        return new SessionOptionsFactoryBundle(
            () => CreateSessionOptions(sessionProvider, devicePolicy, additionalTrtOptions).Options,
            requestedProvider,
            selectedProvider,
            FormatBootstrapDetail(bootstrapResult.Detail, probeSelection.FallbackReason) ?? string.Empty);
    }

    /// <summary>
    /// Packaged DirectML is included with Windows ML. Catalog RegisterCertifiedAsync is not a
    /// prerequisite for GetEpDevices/append, so a bootstrap CPU select must not skip the DML path.
    /// Native CUDA on Windows still wins when bootstrap selected CUDA.
    /// </summary>
    internal static ExecutionProviderKind ResolveSessionOptionsProvider(
        ExecutionProviderKind requestedProvider,
        ExecutionProviderKind bootstrapSelectedProvider)
    {
        if (requestedProvider is ExecutionProviderKind.DirectMl)
        {
            return OperatingSystem.IsWindows()
                ? ExecutionProviderKind.DirectMl
                : bootstrapSelectedProvider;
        }

        if (requestedProvider is ExecutionProviderKind.Cuda
            && bootstrapSelectedProvider is ExecutionProviderKind.Cpu
            && OperatingSystem.IsWindows())
        {
            return ExecutionProviderKind.DirectMl;
        }

        return bootstrapSelectedProvider;
    }

    internal static string FormatProviderLabel(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.TensorRTRtx => "tensorrt-rtx",
            ExecutionProviderKind.OpenVino => "openvino",
            ExecutionProviderKind.CoreMl => "coreml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRt => "tensorrt",
            ExecutionProviderKind.Migraphx => "migraphx",
            ExecutionProviderKind.Dnnl => "dnnl",
            ExecutionProviderKind.Qnn => "qnn",
            ExecutionProviderKind.OpenVinoCatalog => "openvino-catalog",
            ExecutionProviderKind.VitisAi => "vitisai",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported execution provider kind.")
        };

    private static SessionOptionsSelection CreateSessionOptions(
        ExecutionProviderKind provider,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        bool enableCudaGraph = false)
    {
        bool useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, provider);
        SessionOptions options = CreateBaseSessionOptions(
            useCatalogDevicePolicy ? devicePolicy : WindowsMlExecutionDevicePolicy.Explicit,
            out bool devicePolicyApplied);

        if (provider is ExecutionProviderKind.Cpu)
        {
            return new SessionOptionsSelection(options, ExecutionProviderKind.Cpu);
        }

        if (useCatalogDevicePolicy && IsCatalogGpuProvider(provider))
        {
            return new SessionOptionsSelection(
                options,
                provider,
                BuildDevicePolicyFallbackReason(devicePolicy, devicePolicyApplied));
        }

        return AppendProviderSpecificSelection(options, provider, additionalTrtOptions, enableCudaGraph)
            ?? throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported execution provider kind.");
    }

    private static SessionOptionsSelection? AppendProviderSpecificSelection(
        SessionOptions options,
        ExecutionProviderKind provider,
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        bool enableCudaGraph = false)
    {
        return provider switch
        {
            ExecutionProviderKind.DirectMl => CreateDirectMlSelection(options),
            ExecutionProviderKind.Dnnl => CreateDnnlSelection(options),
            ExecutionProviderKind.TensorRTRtx => CreateTensorRtRtxSelection(options, additionalTrtOptions, enableCudaGraph),
            ExecutionProviderKind.Migraphx => CreateMigraphxSelection(options),
            ExecutionProviderKind.CoreMl => CreateCoreMlSelection(options),
            ExecutionProviderKind.OpenVinoCatalog => CreateOpenVinoCatalogSelection(options),
            ExecutionProviderKind.Qnn => CreateQnnSelection(options),
            ExecutionProviderKind.VitisAi => CreateVitisAiSelection(options),
#if LINUX || WINDOWS
            ExecutionProviderKind.Cuda => CreateCudaSelection(options),
            ExecutionProviderKind.TensorRt => CreateTensorRtSelection(options),
#endif
            _ => null,
        };
    }

    private static SessionOptionsSelection CreateDirectMlSelection(SessionOptions options)
    {
        if (!TryAppendDirectMlProvider(options, out _) &&
            !TryAppendDirectMlProviderDirect(options, out _))
        {
            return new SessionOptionsSelection(
                options,
                ExecutionProviderKind.Cpu,
                "Requested dml but DirectML append failed; CPU fallback activated.");
        }

        return new SessionOptionsSelection(options, ExecutionProviderKind.DirectMl);
    }

    private static SessionOptionsSelection CreateDnnlSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider = DnnlSessionOptionsExtensions.AppendDnnlOrFallback(
            options,
            out string? failureReason);
        return selectedProvider is ExecutionProviderKind.Dnnl
            ? new SessionOptionsSelection(options, selectedProvider)
            : new SessionOptionsSelection(
                options,
                ExecutionProviderKind.Cpu,
                $"Requested dnnl but AppendExecutionProvider_Dnnl failed: {failureReason ?? "unknown failure"}");
    }

    private static SessionOptionsSelection CreateTensorRtRtxSelection(
        SessionOptions options,
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        bool enableCudaGraph = false)
    {
        ExecutionProviderKind selectedProvider =
            AppendTensorRtRtxOrFallbackProvider(options, additionalTrtOptions, enableCudaGraph);
        if (selectedProvider is ExecutionProviderKind.TensorRTRtx)
        {
            return new SessionOptionsSelection(options, selectedProvider);
        }

        string fallbackLabel = FormatProviderLabel(selectedProvider);
        return new SessionOptionsSelection(
            options,
            selectedProvider,
            $"Requested tensorrt-rtx but TensorRT RTX EP device was unavailable; fell back to {fallbackLabel}.");
    }

    private static SessionOptionsSelection CreateMigraphxSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider = MigraphxSessionOptionsExtensions.AppendMigraphxOrFallback(options);
        return new SessionOptionsSelection(options, selectedProvider);
    }

    private static SessionOptionsSelection CreateCoreMlSelection(SessionOptions options)
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException("CoreML EP is only available on macOS.");
        options.AppendExecutionProvider_CoreML(GetCoreMlFlags());
        return new SessionOptionsSelection(options, ExecutionProviderKind.CoreMl);
    }

#if LINUX || WINDOWS
    private static SessionOptionsSelection CreateCudaSelection(SessionOptions options)
    {
        options.AppendExecutionProvider_CUDA(deviceId: 0);
        return new SessionOptionsSelection(options, ExecutionProviderKind.Cuda);
    }

    private static SessionOptionsSelection CreateTensorRtSelection(SessionOptions options)
    {
        options.AppendExecutionProvider_Tensorrt(deviceId: 0);
        return new SessionOptionsSelection(options, ExecutionProviderKind.TensorRt);
    }
#endif

    private static SessionOptionsSelection CreateOpenVinoCatalogSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider =
            WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendOpenVinoCatalogOrFallback(options);
        return new SessionOptionsSelection(options, selectedProvider);
    }

    private static SessionOptionsSelection CreateQnnSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider =
            WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendQnnOrFallback(options);
        return new SessionOptionsSelection(options, selectedProvider);
    }

    private static SessionOptionsSelection CreateVitisAiSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider =
            WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendVitisAiOrFallback(options);
        return new SessionOptionsSelection(options, selectedProvider);
    }

    private static CoreMLFlags GetCoreMlFlags()
    {
        const CoreMLFlags appleSiliconFlags =
            CoreMLFlags.COREML_FLAG_ONLY_ENABLE_DEVICE_WITH_ANE |
            CoreMLFlags.COREML_FLAG_CREATE_MLPROGRAM;

        bool isAppleSilicon = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && OperatingSystem.IsMacOS();

        return isAppleSilicon
            ? appleSiliconFlags
            : CoreMLFlags.COREML_FLAG_USE_NONE;
    }

    private static ExecutionProviderKind ResolveSharedSelectedProvider(
        ExecutionProviderKind requestedProvider,
        ExecutionProviderKind firstSessionProvider,
        ExecutionProviderKind secondSessionProvider)
    {
        if (firstSessionProvider == secondSessionProvider)
        {
            return firstSessionProvider;
        }

        return requestedProvider is ExecutionProviderKind.TensorRTRtx
            ? ExecutionProviderKind.Cpu
            : requestedProvider;
    }

    private static ExecutionProviderKind ResolveEffectiveDualSessionProvider(
        ExecutionProviderKind requestedProvider,
        InferenceSession encoderSession,
        InferenceSession decoderSession,
        ExecutionProviderKind encoderOptionsSelectedProvider,
        ExecutionProviderKind decoderOptionsSelectedProvider,
        WindowsMlExecutionDevicePolicy devicePolicy)
    {
        ExecutionProviderKind encoderEffective = ResolveEffectiveProviderKindFromSession(
            encoderSession,
            encoderOptionsSelectedProvider,
            ShouldUseCatalogDevicePolicy(devicePolicy, encoderOptionsSelectedProvider));
        ExecutionProviderKind decoderEffective = ResolveEffectiveProviderKindFromSession(
            decoderSession,
            decoderOptionsSelectedProvider,
            ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider));
        return ResolveSharedSelectedProvider(requestedProvider, encoderEffective, decoderEffective);
    }

    internal static ExecutionProviderKind ResolveEffectiveProviderKindFromSession(
        InferenceSession session,
        ExecutionProviderKind optionsSelectedProvider,
        bool usedCatalogDevicePolicy)
    {
        // DNNL only claims a subset of ops; ORT can silently place an unsupported graph on
        // CPU even after AppendExecutionProvider_Dnnl succeeds. Verify actual placement here
        // rather than trusting the append-time selection, mirroring DnnlReadinessProbe's
        // smoke-session check.
        if (optionsSelectedProvider is ExecutionProviderKind.Dnnl)
        {
            return ResolveDnnlEffectiveProviderFromSession(session);
        }

        // Native ORT CUDA / classic TensorRT are not WinML catalog EP names. Probing
        // GetEpDeviceForInputs through the catalog mapper would treat unmapped names as CPU.
        if (optionsSelectedProvider is ExecutionProviderKind.Cuda or ExecutionProviderKind.TensorRt)
        {
            return optionsSelectedProvider;
        }

        // Always probe GetEpDeviceForInputs for GPU / catalog routes so TRT sessions that
        // assigned zero nodes (or fell through) report DirectML/CPU honestly.
        if (usedCatalogDevicePolicy || ShouldProbeEffectiveEpDevices(optionsSelectedProvider))
        {
            return ResolveCatalogEffectiveProviderFromSession(session);
        }

        return optionsSelectedProvider;
    }

    private static bool ShouldProbeEffectiveEpDevices(ExecutionProviderKind optionsSelectedProvider) =>
        optionsSelectedProvider is ExecutionProviderKind.TensorRTRtx
            or ExecutionProviderKind.DirectMl
            or ExecutionProviderKind.Migraphx
            or ExecutionProviderKind.Qnn
            or ExecutionProviderKind.OpenVinoCatalog
            or ExecutionProviderKind.VitisAi;

    private static ExecutionProviderKind ResolveDnnlEffectiveProviderFromSession(InferenceSession session)
    {
        try
        {
            IReadOnlyList<OrtEpDevice> devices = session.GetEpDeviceForInputs();
            return devices.Any(device => DnnlOrtProbe.IsDnnlExecutionProviderName(device.EpName))
                ? ExecutionProviderKind.Dnnl
                : ExecutionProviderKind.Cpu;
        }
        catch (OnnxRuntimeException)
        {
            return ExecutionProviderKind.Cpu;
        }
    }

    internal static ExecutionProviderKind ResolveCatalogEffectiveProviderFromSession(InferenceSession session)
    {
        IReadOnlyList<OrtEpDevice> devices;
        try
        {
            devices = session.GetEpDeviceForInputs();
        }
        catch (OnnxRuntimeException)
        {
            return ExecutionProviderKind.Cpu;
        }

        if (devices.Count == 0)
        {
            return ExecutionProviderKind.Cpu;
        }

        ExecutionProviderKind? bestProvider = null;
        int bestRank = int.MaxValue;
        foreach (OrtEpDevice device in devices)
        {
            if (!TryMapCatalogEpNameToExecutionProviderKind(device.EpName, out ExecutionProviderKind mappedProvider))
            {
                ReportUnmappedCatalogEpName(device.EpName, CatalogEpNameSource.GetEpDeviceForInputs, devices.Count);
                continue;
            }

            int rank = GetCatalogEpHonestyRank(mappedProvider, device.HardwareDevice.Type);
            if (rank >= bestRank)
            {
                continue;
            }

            bestRank = rank;
            bestProvider = mappedProvider;
        }

        return bestProvider ?? ExecutionProviderKind.Cpu;
    }

    internal static bool TryMapCatalogEpNameToExecutionProviderKind(
        string epName,
        out ExecutionProviderKind executionProviderKind)
    {
        if (IsTensorRtRtxDeviceCandidate(epName, OrtHardwareDeviceType.GPU))
        {
            executionProviderKind = ExecutionProviderKind.TensorRTRtx;
            return true;
        }

        if (string.Equals(epName, MigraphxProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Migraphx;
            return true;
        }

        if (IsDnnlExecutionProviderName(epName))
        {
            executionProviderKind = ExecutionProviderKind.Dnnl;
            return true;
        }

        if (IsDirectMlDeviceCandidate(epName, OrtHardwareDeviceType.GPU))
        {
            executionProviderKind = ExecutionProviderKind.DirectMl;
            return true;
        }

        if (string.Equals(epName, OpenVinoCatalogProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.OpenVinoCatalog;
            return true;
        }

        if (string.Equals(epName, QnnProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Qnn;
            return true;
        }

        if (string.Equals(epName, VitisAiProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.VitisAi;
            return true;
        }

        if (string.Equals(epName, "CPUExecutionProvider", StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Cpu;
            return true;
        }

        executionProviderKind = default;
        return false;
    }

    internal enum CatalogEpNameSource
    {
        GetEpDeviceForInputs,
        GetEpDevices
    }

    internal static void ReportUnmappedCatalogEpName(
        string epName,
        CatalogEpNameSource source,
        int deviceCount)
    {
        if (string.IsNullOrWhiteSpace(epName))
        {
            return;
        }

        if (!WarnedUnmappedCatalogEpNames.TryAdd(epName, 0))
        {
            return;
        }

        _logger?.LogWarning(
            "Unmapped Windows ML catalog EP name '{EpName}' encountered ({Source}, deviceCount={DeviceCount}); treating as CPU only for honesty resolution. Add a mapping in TryMapCatalogEpNameToExecutionProviderKind if this EP is supported. See docs/internal/windows-ml-phase-5-catalog-eps.md.",
            epName,
            source,
            deviceCount);
    }

    private static int GetCatalogEpHonestyRank(ExecutionProviderKind provider, OrtHardwareDeviceType deviceType) =>
        (provider, deviceType) switch
        {
            (ExecutionProviderKind.TensorRTRtx, _) => 0,
            (ExecutionProviderKind.Migraphx, _) => 1,
            (ExecutionProviderKind.Qnn, OrtHardwareDeviceType.NPU) => 2,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.NPU) => 3,
            (ExecutionProviderKind.VitisAi, _) => 4,
            (ExecutionProviderKind.DirectMl, _) => 5,
            (ExecutionProviderKind.Qnn, OrtHardwareDeviceType.GPU) => 6,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.GPU) => 7,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.CPU) => 8,
            (ExecutionProviderKind.Dnnl, OrtHardwareDeviceType.CPU) => 9,
            (ExecutionProviderKind.Cpu, _) => 10,
            _ => 11
        };

    private static string? BuildEpFallbackReason(ExecutionProviderKind requestedProvider, ExecutionProviderKind effectiveProvider) =>
        effectiveProvider != requestedProvider
            ? $"Requested {FormatProviderLabel(requestedProvider)} but effective {FormatProviderLabel(effectiveProvider)}."
            : null;

    private static string? BuildSessionOptionsFallbackReason(
        ExecutionProviderKind requestedProvider,
        ExecutionProviderKind effectiveProvider,
        SessionOptionsSelection sessionOptionsSelection) =>
        MergeFallbackReasons(
            sessionOptionsSelection.FallbackReason,
            BuildEpFallbackReason(requestedProvider, effectiveProvider));

    internal static bool TryAppendDirectMlProvider(SessionOptions options, out string? failureReason)
    {
        try
        {
            AppendDirectMlProvider(options);
            failureReason = null;
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    // Direct DML path — no WinML catalog/WinAppSDK bootstrap required.
    // Used when the catalog path is unavailable (e.g., test processes, headless runners).
    internal static bool TryAppendDirectMlProviderDirect(SessionOptions options, out string? failureReason)
    {
        try
        {
            options.AppendExecutionProvider_DML(deviceId: 0);
            failureReason = null;
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    internal static ExecutionProviderKind AppendTensorRtRtxOrFallbackProvider(
        SessionOptions options,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        bool enableCudaGraph = false)
    {
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
        var devices = OrtEnv.Instance().GetEpDevices();
        var trtDevice = devices.FirstOrDefault(d => IsTensorRtRtxDeviceCandidate(d.EpName, d.HardwareDevice.Type));
        if (trtDevice != null)
        {
            IReadOnlyDictionary<string, string> trtOptions = BuildTensorRtRtxOptions(additionalTrtOptions, enableCudaGraph);
            options.AppendExecutionProvider(OrtEnv.Instance(), new[] { trtDevice }, trtOptions);
            return ExecutionProviderKind.TensorRTRtx;
        }

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            foreach (OrtEpDevice device in devices.Where(d => d.HardwareDevice.Type is OrtHardwareDeviceType.GPU))
            {
                if (!TryMapCatalogEpNameToExecutionProviderKind(device.EpName, out _))
                {
                    ReportUnmappedCatalogEpName(device.EpName, CatalogEpNameSource.GetEpDevices, devices.Count);
                }
            }
        }

        // TensorRT RTX plugin device not available; fallback to DirectML if possible, else CPU is default.
        return TryAppendDirectMlProvider(options, out _)
            ? ExecutionProviderKind.DirectMl
            : ExecutionProviderKind.Cpu;
    }

    internal static void AppendDirectMlProvider(SessionOptions options)
    {
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
        var devices = OrtEnv.Instance().GetEpDevices();
        OrtEpDevice? directMlDevice = devices.FirstOrDefault(d => IsDirectMlDeviceCandidate(d.EpName, d.HardwareDevice.Type))
            ?? devices.FirstOrDefault(d => IsDirectMlExecutionProviderName(d.EpName));
        if (directMlDevice is not null)
        {
            options.AppendExecutionProvider(
                OrtEnv.Instance(),
                new[] { directMlDevice },
                new Dictionary<string, string>(StringComparer.Ordinal));
            return;
        }

        try
        {
            options.AppendExecutionProvider(DirectMlExecutionProviderName);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "DirectML catalog execution provider is not visible in OrtEnv.GetEpDevices().",
                ex);
        }
    }

    internal sealed record SessionOptionsSelection(
        SessionOptions Options,
        ExecutionProviderKind SelectedProvider,
        string? FallbackReason = null);

    private static bool IsTensorRtRtxDeviceCandidate(string epName, OrtHardwareDeviceType hardwareDeviceType) =>
        // Only accept the single canonical standalone EP ABI plugin name.
        // "NvTensorRtExecutionProvider" and "TensorrtExecutionProvider" are the old CUDA-based TensorRT EP —
        // they must NOT be treated as TRT RTX candidates (different EP family, different options).
        hardwareDeviceType is OrtHardwareDeviceType.GPU &&
        string.Equals(epName, TensorRtRtxExecutionProviderName, StringComparison.Ordinal);

    private static bool IsDirectMlExecutionProviderName(string epName) =>
        string.Equals(epName, DirectMlExecutionProviderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(epName, DirectMlLongExecutionProviderName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectMlDeviceCandidate(string epName, OrtHardwareDeviceType hardwareDeviceType) =>
        hardwareDeviceType is OrtHardwareDeviceType.GPU &&
        IsDirectMlExecutionProviderName(epName);

    private static bool IsDnnlExecutionProviderName(string epName) =>
        string.Equals(epName, DnnlExecutionProviderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(epName, DnnlUpperExecutionProviderName, StringComparison.OrdinalIgnoreCase);

    // Classic TensorRT EP uses trt_profile_*. NvTensorRTRTX rejects those keys.
    // Same shape grammar: "input:dim1xdim2x...,input2:..."
    // https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html
    private static readonly IReadOnlyDictionary<string, string> ClassicTrtProfileKeysToNvRtx =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trt_profile_min_shapes"] = "nv_profile_min_shapes",
            ["trt_profile_max_shapes"] = "nv_profile_max_shapes",
            ["trt_profile_opt_shapes"] = "nv_profile_opt_shapes",
        };

    /// <summary>
    /// Builds TensorRT-RTX provider options.
    /// </summary>
    /// <param name="enableCudaGraph">
    /// CUDA graph capture replays a fixed sequence of GPU kernel launches against fixed device
    /// memory addresses (see ONNX Runtime's TensorRT-RTX EP docs: "Avoid enabling CUDA Graph ...
    /// if input shapes or device bindings frequently change"). Defaults to <see langword="false"/>
    /// because most call sites build fresh input tensors on every <c>Run()</c>, which violates that
    /// precondition and can silently replay stale device buffers. Pass <see langword="true"/> only
    /// for a session whose call site is verified to feed stable, IoBinding-backed device buffers
    /// across iterations.
    /// </param>
    private static IReadOnlyDictionary<string, string> BuildTensorRtRtxOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        bool enableCudaGraph = false)
    {
        var trtOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Cache compiled TRT-RTX kernels locally to speed up subsequent session loads.
            ["nv_runtime_cache_path"] = ResolveTensorRtRtxRuntimeCachePath(),
        };

        // Always write the flag explicitly: the TensorRT RTX EP defaults enable_cuda_graph to
        // true, so omitting it leaves capture on rather than off.
        trtOptions["enable_cuda_graph"] = enableCudaGraph ? "1" : "0";

        if (additionalTrtOptions is null)
        {
            return trtOptions;
        }

        foreach ((string key, string value) in additionalTrtOptions)
        {
            if (ClassicTrtProfileKeysToNvRtx.ContainsKey(key))
            {
                continue;
            }

            trtOptions[key] = value;
        }

        foreach ((string classicKey, string nvKey) in ClassicTrtProfileKeysToNvRtx)
        {
            if (trtOptions.ContainsKey(nvKey))
            {
                continue;
            }

            if (additionalTrtOptions.TryGetValue(classicKey, out string? shapes))
            {
                trtOptions[nvKey] = shapes;
            }
        }

        return trtOptions;
    }

    private static string ResolveTensorRtRtxRuntimeCachePath()
    {
        string? engineCacheRoot = Environment.GetEnvironmentVariable(EngineCacheRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(engineCacheRoot))
        {
            return NormalizePath(engineCacheRoot);
        }

        string? cacheRoot = Environment.GetEnvironmentVariable(CacheRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(cacheRoot))
        {
            return Path.Combine(NormalizePath(cacheRoot), "EngineCache");
        }

        string localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            localAppDataRoot = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppDataRoot, "Trackdub", "EngineCache");
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    private static string BuildSessionOptionsFingerprint(
        ExecutionProviderKind selectedProviderKind,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        bool enableCudaGraph = false)
    {
        if (selectedProviderKind is ExecutionProviderKind.TensorRTRtx)
        {
            var combinedOptions = new Dictionary<string, string>(
                BuildTensorRtRtxOptions(additionalTrtOptions, enableCudaGraph), StringComparer.Ordinal);

            return SessionPoolKey.HashOptions(combinedOptions);
        }

        if (ShouldIncludePolicyInFingerprint(devicePolicy, selectedProviderKind))
        {
            return SessionPoolKey.HashOptions(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["windows_ml_device_policy"] = WindowsMlExecutionDevicePolicySettings.ToKey(devicePolicy)
            });
        }

        return SessionPoolKey.HashOptions(null);
    }

    private static bool ShouldIncludePolicyInFingerprint(
        WindowsMlExecutionDevicePolicy devicePolicy,
        ExecutionProviderKind selectedProviderKind) =>
        ShouldUseCatalogDevicePolicy(devicePolicy, selectedProviderKind);

    private static string? MergeFallbackReasons(string? encoder, string? decoder) =>
        (encoder, decoder) switch
        {
            (null, null) => null,
            ({ } e, null) => e,
            (null, { } d) => d,
            ({ } e, { } d) when e == d => e,
            ({ } e, { } d) => $"Encoder: {e} Decoder: {d}"
        };

    private static string? FormatBootstrapDetail(
        string? bootstrapDetail,
        string? sessionOptionsFallbackReason)
    {
        if (string.IsNullOrWhiteSpace(sessionOptionsFallbackReason))
        {
            return bootstrapDetail;
        }

        return string.IsNullOrWhiteSpace(bootstrapDetail)
            ? $"Session options fallback reason: {sessionOptionsFallbackReason}"
            : $"{bootstrapDetail} Session options fallback reason: {sessionOptionsFallbackReason}";
    }

    private static SessionOptions CreateBaseSessionOptions(
        WindowsMlExecutionDevicePolicy devicePolicy,
        out bool devicePolicyApplied)
    {
        SessionOptions options = new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        devicePolicyApplied = false;
#if WINDOWS
        devicePolicyApplied = WindowsMlExecutionDevicePolicyMapper.ApplyIfNeeded(options, devicePolicy);
#endif
        return options;
    }

    /// <summary>
    /// Reports the truth about whether a requested extended Windows ML device policy
    /// (<see cref="WindowsMlExecutionDevicePolicy.DefaultRender"/> /
    /// <see cref="WindowsMlExecutionDevicePolicy.MinPower"/>) actually took effect on the
    /// session. <see cref="WindowsMlExecutionDevicePolicyMapper.ApplyIfNeeded"/> silently skips
    /// <c>SetEpSelectionPolicy</c> when the loaded ORT managed binding lacks the required enum
    /// members, so callers must not report the policy as active without checking
    /// <paramref name="devicePolicyApplied"/> — otherwise diagnostics/fingerprints would claim a
    /// device-selection behavior that never actually happened.
    /// </summary>
    private static string? BuildDevicePolicyFallbackReason(
        WindowsMlExecutionDevicePolicy devicePolicy,
        bool devicePolicyApplied) =>
        !devicePolicyApplied &&
        devicePolicy is WindowsMlExecutionDevicePolicy.DefaultRender or WindowsMlExecutionDevicePolicy.MinPower
            ? $"Requested Windows ML device policy '{WindowsMlExecutionDevicePolicySettings.ToKey(devicePolicy)}' " +
              "was not applied: the loaded ONNX Runtime managed binding does not expose the required " +
              "extended ExecutionProviderDevicePolicy members (DEFAULT_RENDER/MIN_POWER); the session " +
              "falls back to Explicit EP selection instead."
            : null;

    private static async Task<WindowsMlExecutionDevicePolicy> ResolveDevicePolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _devicePolicyProvider.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return WindowsMlExecutionDevicePolicy.Explicit;
        }
    }

    private static bool IsCatalogGpuProvider(ExecutionProviderKind provider) =>
        provider is ExecutionProviderKind.DirectMl
            or ExecutionProviderKind.Migraphx;

    private static bool IsNativeGpuProvider(ExecutionProviderKind provider) =>
#if LINUX || WINDOWS
        provider is ExecutionProviderKind.Cuda or ExecutionProviderKind.TensorRt;
#else
        false;
#endif

    internal static bool ShouldUseCatalogDevicePolicy(
        WindowsMlExecutionDevicePolicy devicePolicy,
        ExecutionProviderKind provider) =>
        OperatingSystem.IsWindows() &&
        devicePolicy != WindowsMlExecutionDevicePolicy.Explicit &&
        IsWindowsMlCatalogProvider(provider);

    /// <summary>
    /// WinML catalog EPs that can take ORT <c>SetEpSelectionPolicy</c> (device auto-select among
    /// registered catalog devices). DirectML stays on the legacy explicit-append path; native
    /// CUDA/TensorRT/TensorRT RTX/DNNL/CoreML are not Windows ML catalog routes.
    /// </summary>
    private static bool IsWindowsMlCatalogProvider(ExecutionProviderKind provider) =>
        provider is ExecutionProviderKind.Migraphx
            or ExecutionProviderKind.Qnn
            or ExecutionProviderKind.VitisAi
            or ExecutionProviderKind.OpenVinoCatalog;

    private sealed class NullWindowsMlEpDevicePolicyProvider : IWindowsMlEpDevicePolicyProvider
    {
        internal static readonly NullWindowsMlEpDevicePolicyProvider Instance = new();

        public Task<WindowsMlExecutionDevicePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<WindowsMlExecutionDevicePolicy>(cancellationToken)
                : Task.FromResult(WindowsMlExecutionDevicePolicy.Explicit);

        public void InvalidateCache()
        {
        }
    }

    private static IExecutionProviderBootstrapper GetPlatformBootstrapper()
    {
#if WINDOWS
        return new ExecutionProviders.Windows.WindowsExecutionProviderBootstrapper();
#elif MACOS
        return new ExecutionProviders.Mac.MacExecutionProviderBootstrapper();
#elif LINUX
        return new ExecutionProviders.Linux.LinuxExecutionProviderBootstrapper(
            NullOpenVinoAvailabilityProvider.Instance);
#else
        return new ExecutionProviders.PortableExecutionProviderBootstrapper(
            TensorRtRtx.TensorRtRtxProviderBootstrapFactory.CreateWithDefaultInstallPath(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Trackdub")));
#endif
    }

    private sealed class NullOpenVinoAvailabilityProvider : IOpenVinoAvailabilityProvider
    {
        public static readonly NullOpenVinoAvailabilityProvider Instance = new();
        public bool IsAvailable => false;
        public bool UseOpenVinoCpuProxy => false;
    }

    internal sealed record SingleSessionLease(
        InferenceSession Session,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? PoolLease { get; init; }

        public void Dispose()
        {
            if (PoolLease is not null)
            {
                PoolLease.Dispose();
            }
            else
            {
                Session.Dispose();
            }
        }
    }

    internal sealed record Qwen3AsrSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderInitSession,
        InferenceSession DecoderStepSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? EncoderPoolLease { get; init; }
        internal SessionLease? DecoderInitPoolLease { get; init; }
        internal SessionLease? DecoderStepPoolLease { get; init; }

        public void Dispose()
        {
            EncoderPoolLease?.Dispose();
            DecoderInitPoolLease?.Dispose();
            DecoderStepPoolLease?.Dispose();
        }
    }

    internal sealed record LatentSyncSessionLease(
        InferenceSession UNetSession,
        InferenceSession VaeEncoderSession,
        InferenceSession VaeDecoderSession,
        InferenceSession WhisperEncoderSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? UNetPoolLease { get; init; }
        internal SessionLease? VaeEncoderPoolLease { get; init; }
        internal SessionLease? VaeDecoderPoolLease { get; init; }
        internal SessionLease? WhisperEncoderPoolLease { get; init; }

        public void Dispose()
        {
            UNetPoolLease?.Dispose();
            VaeEncoderPoolLease?.Dispose();
            VaeDecoderPoolLease?.Dispose();
            WhisperEncoderPoolLease?.Dispose();
        }
    }

    internal sealed record NemotronAsrSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderJointSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? EncoderPoolLease { get; init; }
        internal SessionLease? DecoderJointPoolLease { get; init; }

        public void Dispose()
        {
            EncoderPoolLease?.Dispose();
            DecoderJointPoolLease?.Dispose();
        }
    }

    internal sealed record WhisperSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? EncoderPoolLease { get; init; }
        internal SessionLease? DecoderPoolLease { get; init; }

        public void Dispose()
        {
            if (EncoderPoolLease is not null)
            {
                EncoderPoolLease.Dispose();
            }
            else
            {
                EncoderSession.Dispose();
            }

            if (DecoderPoolLease is not null)
            {
                DecoderPoolLease.Dispose();
            }
            else
            {
                DecoderSession.Dispose();
            }
        }
    }

    internal sealed record OpusSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? EncoderPoolLease { get; init; }
        internal SessionLease? DecoderPoolLease { get; init; }

        public void Dispose()
        {
            if (EncoderPoolLease is not null)
            {
                EncoderPoolLease.Dispose();
            }
            else
            {
                EncoderSession.Dispose();
            }

            if (DecoderPoolLease is not null)
            {
                DecoderPoolLease.Dispose();
            }
            else
            {
                DecoderSession.Dispose();
            }
        }
    }
}
