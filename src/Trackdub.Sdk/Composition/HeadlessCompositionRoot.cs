using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Sdk.Composition;

/// <summary>
/// SDK-facing headless composition entry point. Delegates to
/// <see cref="Trackdub.Composition.Headless.HeadlessCompositionRoot"/>.
/// </summary>
public static class HeadlessCompositionRoot
{
    private static readonly string[] HardwareOverrideStageKeys =
    [
        "Vad",
        "Asr",
        "AsrGenAi",
        "AsrOnnxRuntime",
        "AsrNemotron",
        "Separation",
        "OverlapRescue",
        "Diarization",
        "Translation",
        "Tts",
        "TextRefinement",
        "LipSync",
        "LipSynthesis",
    ];

    /// <summary>
    /// Registers all standard Trackdub services and then replaces UI-coupled registrations
    /// with headless alternatives. Public for Sdk.Tests and advanced hosts. After building
    /// the provider, construct a <see cref="HeadlessDubbingSessionFactory"/>; its constructor
    /// eagerly activates any scoped storage-environment override before session work begins.
    /// </summary>
    public static IServiceCollection AddHeadlessTrackdub(
        this IServiceCollection services,
        TrackdubOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return services.AddHeadlessTrackdub(ToHeadlessOptions(options));
    }

    /// <summary>
    /// Overload accepting optional options (defaults) for test hosts.
    /// </summary>
    public static IServiceCollection AddHeadlessTrackdub(this IServiceCollection services) =>
        services.AddHeadlessTrackdub(new TrackdubOptions());

    internal static HeadlessTrackdubOptions ToHeadlessOptions(TrackdubOptions options)
    {
        IReadOnlyDictionary<string, ExecutionProviderKind>? hardwareOverrides = null;
        bool requirePreferred = options.RequirePreferredExecutionProvider;

        if (options.PreferredExecutionProvider is ExecutionProviderKind provider)
        {
            hardwareOverrides = HardwareOverrideStageKeys.ToDictionary(
                key => key,
                _ => provider,
                StringComparer.Ordinal);
        }

        return new HeadlessTrackdubOptions
        {
            ModelDirectory = options.ModelDirectory,
            ModelCacheDirectory = options.ModelCacheDirectory,
            LogDirectory = options.LogDirectory,
            HardwareOverrides = hardwareOverrides,
            RequirePreferredExecutionProviders = requirePreferred && hardwareOverrides is not null,
            WindowsMlExecutionDevicePolicy = options.WindowsMlExecutionDevicePolicy,
            FfmpegPath = options.FfmpegPath,
            FfprobePath = options.FfprobePath,
            Logger = options.Logger,
            ServiceConfigurator = options.ServiceConfigurator,
        };
    }

    /// <summary>
    /// Hands the container's process-GPU reader to the shared ONNX session pool so accelerator
    /// admission accounts for this process's real dedicated GPU footprint (opt-out via
    /// <c>TRACKDUB_SESSION_PROCESS_GPU_ADMISSION</c>). The SDK builds the headless composition
    /// directly in <see cref="TrackdubBuilder.Build"/> — not through
    /// <see cref="Trackdub.Composition.Headless.HeadlessDubbingHost"/> — so every SDK composition
    /// owner must call this once the provider is built or the advertised default-on admission
    /// silently stays reservation-only for that path. Best-effort.
    /// </summary>
    public static IProcessGpuMemoryReader? BindSharedPoolProcessGpuAdmission(IServiceProvider services) =>
        Trackdub.Composition.Headless.HeadlessCompositionRoot.BindSharedPoolProcessGpuAdmission(services);

    /// <summary>
    /// Releases the process-wide pool binding owned by <paramref name="reader"/> only while it
    /// still refers to that reader, so disposing one composition owner never tears down a newer
    /// owner's registration. Safe to call with null.
    /// </summary>
    public static void ClearSharedPoolProcessGpuAdmission(IProcessGpuMemoryReader? reader) =>
        Trackdub.Composition.Headless.HeadlessCompositionRoot.ClearSharedPoolProcessGpuAdmission(reader);

    /// <summary>Registers the device-to-LUID mapping used for per-adapter GPU admission.</summary>
    public static IReadOnlyDictionary<int, long>? BindSharedPoolAdapterLuidMap(IServiceProvider services) =>
        Trackdub.Composition.Headless.HeadlessCompositionRoot.BindSharedPoolAdapterLuidMap(services);
}
