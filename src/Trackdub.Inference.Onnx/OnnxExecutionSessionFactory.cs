using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Trackdub.Contracts.ApplicationContracts;
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

    public static async Task<SingleSessionLease> CreateSingleAsync(
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null)
    {
        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection sessionOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtOptions);
        using SessionOptions sessionOptions = sessionOptionsSelection.Options;
        bool useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, sessionOptionsSelection.SelectedProvider);
        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(modelPath, sessionOptions);
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
    }

    public static async Task<WhisperSessionLease> CreateWhisperAsync(
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null)
    {
        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection encoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        SessionOptionsSelection decoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtDecoderOptions);
        using SessionOptions encoderOptions = encoderOptionsSelection.Options;
        using SessionOptions decoderOptions = decoderOptionsSelection.Options;
        InferenceSession? encoderSession = null;
        InferenceSession? decoderSession = null;
        try
        {
            encoderSession = new InferenceSession(encoderModelPath, encoderOptions);
            decoderSession = new InferenceSession(decoderModelPath, decoderOptions);
            ExecutionProviderKind resolvedSelectedProvider = ResolveEffectiveDualSessionProvider(
                provider,
                encoderSession,
                decoderSession,
                encoderOptionsSelection.SelectedProvider,
                decoderOptionsSelection.SelectedProvider,
                devicePolicy);
            string selectedProvider = FormatProviderLabel(resolvedSelectedProvider);
            string? encoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    encoderSession,
                    encoderOptionsSelection.SelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, encoderOptionsSelection.SelectedProvider)),
                encoderOptionsSelection);
            string? decoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    decoderSession,
                    decoderOptionsSelection.SelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelection.SelectedProvider)),
                decoderOptionsSelection);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(encoderFallbackReason, decoderFallbackReason));

            return new WhisperSessionLease(
                encoderSession,
                decoderSession,
                requestedProvider,
                selectedProvider,
                bootstrapDetail);
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
        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection encoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        SessionOptionsSelection decoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtDecoderOptions);
        using SessionOptions encoderOptions = encoderOptionsSelection.Options;
        using SessionOptions decoderOptions = decoderOptionsSelection.Options;
        InferenceSession? encoderSession = null;
        InferenceSession? decoderSession = null;
        try
        {
            encoderSession = new InferenceSession(encoderModelPath, encoderOptions);
            decoderSession = new InferenceSession(decoderModelPath, decoderOptions);
            ExecutionProviderKind resolvedSelectedProvider = ResolveEffectiveDualSessionProvider(
                provider,
                encoderSession,
                decoderSession,
                encoderOptionsSelection.SelectedProvider,
                decoderOptionsSelection.SelectedProvider,
                devicePolicy);
            string selectedProvider = FormatProviderLabel(resolvedSelectedProvider);
            string? encoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    encoderSession,
                    encoderOptionsSelection.SelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, encoderOptionsSelection.SelectedProvider)),
                encoderOptionsSelection);
            string? decoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    decoderSession,
                    decoderOptionsSelection.SelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelection.SelectedProvider)),
                decoderOptionsSelection);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(encoderFallbackReason, decoderFallbackReason));

            return new OpusSessionLease(
                encoderSession,
                decoderSession,
                requestedProvider,
                selectedProvider,
                bootstrapDetail);
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

    public static async Task<SingleSessionLease> CreatePooledSingleAsync(
        string engineFamily,
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection optionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtOptions);
        using SessionOptions options = optionsSelection.Options;
        bool useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, optionsSelection.SelectedProvider);
        ExecutionProviderKind optionsSelectedProvider = optionsSelection.SelectedProvider;

        string optionsFingerprint = BuildSessionOptionsFingerprint(optionsSelectedProvider, devicePolicy, additionalTrtOptions);
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
                    ct => Task.FromResult(CreateSession(modelPath, optionsSelection.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);

            ExecutionProviderKind effectiveProvider = ResolveEffectiveProviderKindFromSession(
                poolLease.Session,
                optionsSelectedProvider,
                useCatalogDevicePolicy);
            string selectedProvider = FormatProviderLabel(effectiveProvider);
            string? epFallbackReason = BuildSessionOptionsFallbackReason(provider, effectiveProvider, optionsSelection);
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

        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection encoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        SessionOptionsSelection decoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtDecoderOptions);

        using SessionOptions encoderOptions = encoderOptionsSelection.Options;
        using SessionOptions decoderOptions = decoderOptionsSelection.Options;

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
        SessionPoolKey decoderKey = SessionPoolKey.ForDecoder(
            engineFamily,
            decoderModelPath,
            decoderOptionsSelection.SelectedProvider,
            modelId,
            variant,
            optionsFingerprint: decoderOptionsFingerprint);

        SessionLease? encoderPoolLease = null;
        SessionLease? decoderPoolLease = null;
        try
        {
            encoderPoolLease = await pool
                .GetLeaseAsync(
                    encoderKey,
                    ct => Task.FromResult(CreateSession(encoderModelPath, encoderOptionsSelection.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            decoderPoolLease = await pool
                .GetLeaseAsync(
                    decoderKey,
                    ct => Task.FromResult(CreateSession(decoderModelPath, decoderOptionsSelection.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);

            ExecutionProviderKind effectiveProvider = ResolveEffectiveDualSessionProvider(
                provider,
                encoderPoolLease.Session,
                decoderPoolLease.Session,
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
            string? decoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    decoderPoolLease.Session,
                    decoderOptionsSelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider)),
                decoderOptionsSelection);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(encoderFallbackReason, decoderFallbackReason));

            return new WhisperSessionLease(encoderPoolLease.Session, decoderPoolLease.Session, requestedProvider, selectedProvider, bootstrapDetail)
            {
                EncoderPoolLease = encoderPoolLease,
                DecoderPoolLease = decoderPoolLease
            };
        }
        catch
        {
            encoderPoolLease?.Dispose();
            decoderPoolLease?.Dispose();
            throw;
        }
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
        SessionOptionsSelection encoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        SessionOptionsSelection decoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtDecoderOptions);

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

        SessionOptionsSelection unetOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, null);
        SessionOptionsSelection vaeEncOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, null);
        SessionOptionsSelection vaeDecOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, null);
        SessionOptionsSelection whisperOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, null);
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

        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection encoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        SessionOptionsSelection decoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtDecoderOptions);

        using SessionOptions encoderOptions = encoderOptionsSelection.Options;
        using SessionOptions decoderOptions = decoderOptionsSelection.Options;

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
        SessionPoolKey decoderKey = SessionPoolKey.ForDecoder(
            engineFamily,
            decoderJointModelPath,
            decoderOptionsSelection.SelectedProvider,
            modelId,
            variant,
            optionsFingerprint: decoderOptionsFingerprint);

        SessionLease? encoderPoolLease = null;
        SessionLease? decoderPoolLease = null;
        try
        {
            encoderPoolLease = await pool
                .GetLeaseAsync(
                    encoderKey,
                    ct => Task.FromResult(CreateSession(encoderModelPath, encoderOptionsSelection.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            decoderPoolLease = await pool
                .GetLeaseAsync(
                    decoderKey,
                    ct => Task.FromResult(CreateSession(decoderJointModelPath, decoderOptionsSelection.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);

            ExecutionProviderKind effectiveProvider = ResolveEffectiveDualSessionProvider(
                provider,
                encoderPoolLease.Session,
                decoderPoolLease.Session,
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
            string? decoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    decoderPoolLease.Session,
                    decoderOptionsSelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider)),
                decoderOptionsSelection);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(encoderFallbackReason, decoderFallbackReason));

            return new NemotronAsrSessionLease(
                encoderPoolLease.Session,
                decoderPoolLease.Session,
                requestedProvider,
                selectedProvider,
                bootstrapDetail)
            {
                EncoderPoolLease = encoderPoolLease,
                DecoderJointPoolLease = decoderPoolLease,
            };
        }
        catch
        {
            encoderPoolLease?.Dispose();
            decoderPoolLease?.Dispose();
            throw;
        }
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

        var requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        var encoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        var decoderOptionsSelection = CreateSessionOptions(bootstrapResult.SelectedProvider, devicePolicy, additionalTrtDecoderOptions);

        using var encoderOptions = encoderOptionsSelection.Options;
        using var decoderOptions = decoderOptionsSelection.Options;

        var encoderOptionsSelectedProvider = encoderOptionsSelection.SelectedProvider;
        var decoderOptionsSelectedProvider = decoderOptionsSelection.SelectedProvider;

        var encoderOptionsFingerprint = BuildSessionOptionsFingerprint(encoderOptionsSelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        var decoderOptionsFingerprint = BuildSessionOptionsFingerprint(decoderOptionsSelectedProvider, devicePolicy, additionalTrtDecoderOptions);

        var encoderKey = SessionPoolKey.ForEncoder(
            engineFamily,
            encoderModelPath,
            encoderOptionsSelection.SelectedProvider,
            modelId,
            variant,
            optionsFingerprint: encoderOptionsFingerprint);
        var decoderKey = SessionPoolKey.ForDecoder(
            engineFamily,
            decoderModelPath,
            decoderOptionsSelection.SelectedProvider,
            modelId,
            variant,
            optionsFingerprint: decoderOptionsFingerprint);

        SessionLease? encoderPoolLease = null;
        SessionLease? decoderPoolLease = null;
        try
        {
            encoderPoolLease = await pool
                .GetLeaseAsync(
                    encoderKey,
                    ct => Task.FromResult(CreateSession(encoderModelPath, encoderOptionsSelection.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);
            decoderPoolLease = await pool
                .GetLeaseAsync(
                    decoderKey,
                    ct => Task.FromResult(CreateSession(decoderModelPath, decoderOptionsSelection.Options, sessionFactory, ct)),
                    cancellationToken)
                .ConfigureAwait(false);

            ExecutionProviderKind effectiveProvider = ResolveEffectiveDualSessionProvider(
                provider,
                encoderPoolLease.Session,
                decoderPoolLease.Session,
                encoderOptionsSelectedProvider,
                decoderOptionsSelectedProvider,
                devicePolicy);
            var selectedProvider = FormatProviderLabel(effectiveProvider);
            var encoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    encoderPoolLease.Session,
                    encoderOptionsSelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, encoderOptionsSelectedProvider)),
                encoderOptionsSelection);
            var decoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                ResolveEffectiveProviderKindFromSession(
                    decoderPoolLease.Session,
                    decoderOptionsSelectedProvider,
                    ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider)),
                decoderOptionsSelection);
            var bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(encoderFallbackReason, decoderFallbackReason));

            return new OpusSessionLease(encoderPoolLease.Session, decoderPoolLease.Session, requestedProvider, selectedProvider, bootstrapDetail)
            {
                EncoderPoolLease = encoderPoolLease,
                DecoderPoolLease = decoderPoolLease
            };
        }
        catch
        {
            encoderPoolLease?.Dispose();
            decoderPoolLease?.Dispose();
            throw;
        }
    }

    private static InferenceSession CreateSession(
        string modelPath,
        SessionOptions options,
        Func<string, SessionOptions, InferenceSession>? sessionFactory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return sessionFactory is null
            ? new InferenceSession(modelPath, options)
            : sessionFactory(modelPath, options);
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
        ExecutionProviderKind bootstrapSelected = bootstrapResult.SelectedProvider;
        SessionOptionsSelection probeSelection = CreateSessionOptions(
            bootstrapSelected,
            devicePolicy,
            additionalTrtOptions);
        ExecutionProviderKind selectedProvider = probeSelection.SelectedProvider;
        probeSelection.Options.Dispose();

        return new SessionOptionsFactoryBundle(
            () => CreateSessionOptions(bootstrapSelected, devicePolicy, additionalTrtOptions).Options,
            requestedProvider,
            selectedProvider,
            FormatBootstrapDetail(bootstrapResult.Detail, probeSelection.FallbackReason) ?? string.Empty);
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
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null)
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

        if (provider is ExecutionProviderKind.DirectMl)
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

        if (provider is ExecutionProviderKind.Dnnl)
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

        if (provider is ExecutionProviderKind.TensorRTRtx)
        {
            ExecutionProviderKind selectedProvider = AppendTensorRtRtxOrFallbackProvider(options, additionalTrtOptions);
            return new SessionOptionsSelection(options, selectedProvider);
        }

        if (provider is ExecutionProviderKind.Migraphx)
        {
            ExecutionProviderKind selectedProvider = MigraphxSessionOptionsExtensions.AppendMigraphxOrFallback(options);
            return new SessionOptionsSelection(options, selectedProvider);
        }

        if (provider is ExecutionProviderKind.CoreMl)
        {
            if (!OperatingSystem.IsMacOS())
                throw new InvalidOperationException("CoreML EP is only available on macOS.");
            options.AppendExecutionProvider_CoreML(GetCoreMlFlags());
            return new SessionOptionsSelection(options, ExecutionProviderKind.CoreMl);
        }

#if LINUX || WINDOWS
        if (provider is ExecutionProviderKind.Cuda)
        {
            options.AppendExecutionProvider_CUDA(deviceId: 0);
            return new SessionOptionsSelection(options, ExecutionProviderKind.Cuda);
        }

        if (provider is ExecutionProviderKind.TensorRt)
        {
            options.AppendExecutionProvider_Tensorrt(deviceId: 0);
            return new SessionOptionsSelection(options, ExecutionProviderKind.TensorRt);
        }
#endif

        if (provider is ExecutionProviderKind.OpenVinoCatalog)
        {
            ExecutionProviderKind selectedProvider =
                WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendOpenVinoCatalogOrFallback(options);
            return new SessionOptionsSelection(options, selectedProvider);
        }

        if (provider is ExecutionProviderKind.Qnn)
        {
            ExecutionProviderKind selectedProvider =
                WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendQnnOrFallback(options);
            return new SessionOptionsSelection(options, selectedProvider);
        }

        if (provider is ExecutionProviderKind.VitisAi)
        {
            ExecutionProviderKind selectedProvider =
                WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendVitisAiOrFallback(options);
            return new SessionOptionsSelection(options, selectedProvider);
        }

        throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported execution provider kind.");
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

        if (!usedCatalogDevicePolicy)
        {
            return optionsSelectedProvider;
        }

        return ResolveCatalogEffectiveProviderFromSession(session);
    }

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
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null)
    {
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
        var devices = OrtEnv.Instance().GetEpDevices();
        var trtDevice = devices.FirstOrDefault(d => IsTensorRtRtxDeviceCandidate(d.EpName, d.HardwareDevice.Type));
        if (trtDevice != null)
        {
            IReadOnlyDictionary<string, string> trtOptions = BuildTensorRtRtxOptions(additionalTrtOptions);
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
        var directMlDevice = devices.FirstOrDefault(d => IsDirectMlDeviceCandidate(d.EpName, d.HardwareDevice.Type));
        if (directMlDevice is null)
        {
            throw new InvalidOperationException("DirectML catalog execution provider is not visible in OrtEnv.GetEpDevices().");
        }

        options.AppendExecutionProvider(
            OrtEnv.Instance(),
            new[] { directMlDevice },
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private sealed record SessionOptionsSelection(
        SessionOptions Options,
        ExecutionProviderKind SelectedProvider,
        string? FallbackReason = null);

    private static bool IsTensorRtRtxDeviceCandidate(string epName, OrtHardwareDeviceType hardwareDeviceType) =>
        // Only accept the single canonical standalone EP ABI plugin name.
        // "NvTensorRtExecutionProvider" and "TensorrtExecutionProvider" are the old CUDA-based TensorRT EP —
        // they must NOT be treated as TRT RTX candidates (different EP family, different options).
        hardwareDeviceType is OrtHardwareDeviceType.GPU &&
        string.Equals(epName, TensorRtRtxExecutionProviderName, StringComparison.Ordinal);

    private static bool IsDirectMlDeviceCandidate(string epName, OrtHardwareDeviceType hardwareDeviceType) =>
        hardwareDeviceType is OrtHardwareDeviceType.GPU &&
        (string.Equals(epName, DirectMlExecutionProviderName, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(epName, DirectMlLongExecutionProviderName, StringComparison.OrdinalIgnoreCase));

    private static bool IsDnnlExecutionProviderName(string epName) =>
        string.Equals(epName, DnnlExecutionProviderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(epName, DnnlUpperExecutionProviderName, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> BuildTensorRtRtxOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions)
    {
        var trtOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Cache compiled TRT-RTX kernels locally to speed up subsequent session loads.
            ["nv_runtime_cache_path"] = ResolveTensorRtRtxRuntimeCachePath(),
            ["enable_cuda_graph"] = "1"
        };

        if (additionalTrtOptions != null)
        {
            foreach ((string key, string value) in additionalTrtOptions)
            {
                trtOptions[key] = value;
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
        IReadOnlyDictionary<string, string>? additionalTrtOptions)
    {
        if (selectedProviderKind is ExecutionProviderKind.TensorRTRtx)
        {
            var combinedOptions = new Dictionary<string, string>(BuildTensorRtRtxOptions(additionalTrtOptions), StringComparer.Ordinal);

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
        devicePolicy != WindowsMlExecutionDevicePolicy.Explicit &&
        IsCatalogGpuProvider(selectedProviderKind);

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
        IsCatalogGpuProvider(provider);

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
        return new ExecutionProviders.PortableExecutionProviderBootstrapper();
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
