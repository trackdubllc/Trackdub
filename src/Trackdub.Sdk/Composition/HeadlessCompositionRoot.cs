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
        if (options.ExecutionProvider != ExecutionProviderPreference.Auto)
        {
            ExecutionProviderKind provider = options.ExecutionProvider switch
            {
                ExecutionProviderPreference.Cpu => ExecutionProviderKind.Cpu,
                ExecutionProviderPreference.DirectML => ExecutionProviderKind.DirectMl,
                ExecutionProviderPreference.Cuda => ExecutionProviderKind.TensorRTRtx,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(options.ExecutionProvider),
                    options.ExecutionProvider,
                    "Unknown execution provider preference."),
            };

            hardwareOverrides = new Dictionary<string, ExecutionProviderKind>
            {
                ["Vad"] = provider,
                ["Asr"] = provider,
                ["AsrGenAi"] = provider,
                ["AsrOnnxRuntime"] = provider,
                ["AsrNemotron"] = provider,
                ["Separation"] = provider,
                ["OverlapRescue"] = provider,
                ["Diarization"] = provider,
                ["Translation"] = provider,
                ["Tts"] = provider,
                ["TextRefinement"] = provider,
                ["LipSync"] = provider,
                ["LipSynthesis"] = provider,
            };
        }

        return new HeadlessTrackdubOptions
        {
            ModelDirectory = options.ModelDirectory,
            ModelCacheDirectory = options.ModelCacheDirectory,
            LogDirectory = options.LogDirectory,
            HardwareOverrides = hardwareOverrides,
            WindowsMlExecutionDevicePolicy = options.WindowsMlExecutionDevicePolicy,
            FfmpegPath = options.FfmpegPath,
            FfprobePath = options.FfprobePath,
            Logger = options.Logger,
            ServiceConfigurator = options.ServiceConfigurator,
        };
    }
}
