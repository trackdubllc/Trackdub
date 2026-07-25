using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Composition.DeepFilterNet;
using Trackdub.Infrastructure.Settings;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Media.Enhancement;
using Trackdub.Media.Extraction;
using Trackdub.Media.Loudness;
using Trackdub.Media.Muxing;
using Trackdub.Media.Playback;
using Trackdub.Media.Probe;
using Trackdub.Media.Process;
using Trackdub.Media.Stretch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Trackdub.Composition.Headless;

/// <summary>
/// Headless variant of <see cref="CompositionRoot.AddTrackdub"/> that omits UI-coupled
/// services and substitutes no-op implementations suitable for CLI, benchmarks, and server-side scenarios.
/// </summary>
public static class HeadlessCompositionRoot
{
    /// <summary>
    /// Registers all standard Trackdub services and then replaces UI-coupled registrations
    /// with headless alternatives.
    /// </summary>
    public static IServiceCollection AddHeadlessTrackdub(
        this IServiceCollection services,
        HeadlessTrackdubOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new HeadlessTrackdubOptions();

        // Step 1: Register all standard services (same as UI app).
        services.AddTrackdub();

        // Step 1b: Register the cross-process transient-fault bus as a singleton so the
        // DubbingPipelineEngine ctor + DiagnosticsBundleExporter ctor resolve to the same
        // instance. Eliminates reviewer C8's "each engine instance owns a private bus" gap.
        services.AddSingleton<PipelineTransientFaultBus>();

        // Step 2: Override storage paths if custom directories provided.
        if (options.ModelDirectory is not null || options.ModelCacheDirectory is not null || options.LogDirectory is not null)
        {
            string userDataRoot = options.LogDirectory
                ?? options.ModelDirectory
                ?? options.ModelCacheDirectory
                ?? throw new InvalidOperationException("At least one storage directory must be provided.");
            string userCacheRoot = options.ModelDirectory
                ?? options.ModelCacheDirectory
                ?? options.LogDirectory
                ?? throw new InvalidOperationException("At least one storage directory must be provided.");

            var storageOptions = new TrackdubStorageOptions(
                UserDataRoot: userDataRoot,
                UserCacheRoot: userCacheRoot,
                SharedAssetRoot: null,
                IsPortable: false,
                ExplicitModelCacheDirectory: options.ModelCacheDirectory ?? options.ModelDirectory);
            var storagePaths = new TrackdubStoragePaths(storageOptions);

            // Reapply to the process env too: static consumers that read these vars directly
            // (e.g. OnnxExecutionSessionFactory, FfmpegAutoDownloader) don't go through DI.
            // Registered as a singleton so it's disposed (and the env restored) when this
            // host's ServiceProvider disposes, instead of leaking into other hosts created
            // later in the same process. Callers must resolve HeadlessStorageEnvironmentScope
            // eagerly right after building the provider — unrequested singletons are never
            // instantiated by the container, so the override wouldn't otherwise take effect.
            services.AddSingleton(_ => new HeadlessStorageEnvironmentScope(
                TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(storagePaths)));

            services.Replace(ServiceDescriptor.Singleton(storagePaths));
            services.Replace(ServiceDescriptor.Singleton<IAppStoragePaths>(storagePaths));
        }

        // Step 3: Replace UI-coupled services with headless alternatives.
        services.Replace(ServiceDescriptor.Scoped<IAudioPreviewTransport, NullAudioPreviewTransport>());
        services.RemoveAll<PlaybackService>();
        services.RemoveAll<PlaybackCapabilityProbe>();
        services.RemoveAll<IPlaybackBackendFactory>();

        // Step 4: Replace settings service with in-memory headless variant.
        services.Replace(ServiceDescriptor.Singleton<IStudioSettingsService>(
            new InMemoryStudioSettingsService(options)));

        // Step 5: Wire explicit FFmpeg/FFprobe paths into media services when configured.
        // AddTrackdub() registers these with null paths (PATH/cache discovery). Headless hosts
        // that pass FfmpegPath/FfprobePath must replace those registrations.
        if (options.FfmpegPath is not null || options.FfprobePath is not null)
        {
            string? ffmpegPath = options.FfmpegPath;
            string? ffprobePath = options.FfprobePath;

            services.Replace(ServiceDescriptor.Singleton<IMediaProbe>(
                _ => new FfmpegMediaProbe(ffmpegPath, ffprobePath)));
            services.Replace(ServiceDescriptor.Singleton<IExportToolAvailabilityService>(
                _ => new FfmpegExportToolAvailabilityService(ffmpegPath, ffprobePath)));
            services.Replace(ServiceDescriptor.Singleton<IAudioExtractionService>(
                _ => new FfmpegAudioExtractionService(ffmpegPath)));
            services.Replace(ServiceDescriptor.Singleton<IFfmpegVideoEncoderCapabilities>(
                _ => new FfmpegVideoEncoderCapabilityService(ffmpegPath)));
            services.Replace(ServiceDescriptor.Singleton<ILoudnessNormalizer>(
                _ => new FfmpegLoudnessNormalizer(ffmpegPath)));
            services.Replace(ServiceDescriptor.Singleton<IExportRenderer>(
                _ => new FfmpegMuxer(ffmpegPath)));
            services.Replace(ServiceDescriptor.Singleton<IVideoRecomposer>(
                _ => new FfmpegVideoRecomposer(ffmpegPath)));
            services.Replace(ServiceDescriptor.Singleton<ISpeechAudioProcessingService>(
                _ => new FfmpegSpeechAudioProcessingService(ffmpegPath)));
            services.Replace(ServiceDescriptor.Singleton<ISpeechAudioEnhancementService>(sp =>
                new ResolvingSpeechAudioEnhancementService(
                    sp.GetService<BundledModelManifestRegistry>(),
                    sp.GetService<IModelCacheInventory>(),
                    new FfmpegSpeechAudioEnhancementService(ffmpegPath))));
            services.Replace(ServiceDescriptor.Scoped<IAudioTimeStretchService>(
                _ => new AudioTimeStretchService(ffmpegPath)));
        }

        // Step 6: Override logger if provided.
        if (options.Logger is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(options.Logger));
        }

        // Step 7: Apply user-provided service overrides.
        options.ServiceConfigurator?.Invoke(services);

        return services;
    }
}

/// <summary>
/// DI-owned handle for a headless host's process-env storage-path override. The container
/// only instantiates unrequested singletons on demand, so callers must resolve this type
/// eagerly right after building the <see cref="System.IServiceProvider"/> (before any
/// pipeline work starts) to actually apply the override; disposing the provider then
/// restores the prior environment values via <see cref="Dispose"/>.
/// </summary>
public sealed class HeadlessStorageEnvironmentScope(IDisposable inner) : IDisposable
{
    public void Dispose() => inner.Dispose();
}
