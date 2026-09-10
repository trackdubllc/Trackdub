using Trackdub.Composition.Headless;
using Trackdub.Contracts;
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
            requirePreferred = true;
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
}
