using Trackdub.Application.Dubbing;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Composition.Headless;

/// <summary>
/// Owns the headless DI container and session factory for pipeline execution.
/// Used by benchmarks and other non-SDK headless hosts.
/// </summary>
public sealed class HeadlessDubbingHost : IDisposable
{
    private readonly HeadlessDubbingSessionFactory _sessionFactory;
    private readonly IServiceProvider _serviceProvider;

    private HeadlessDubbingHost(HeadlessDubbingSessionFactory sessionFactory, IServiceProvider serviceProvider)
    {
        _sessionFactory = sessionFactory;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Session factory for creating per-project dubbing sessions.
    /// </summary>
    public IDubbingSessionFactory SessionFactory => _sessionFactory;

    /// <summary>
    /// Builds a headless host with the given options.
    /// </summary>
    public static HeadlessDubbingHost Create(HeadlessTrackdubOptions? options = null)
    {
        options ??= new HeadlessTrackdubOptions();

        if (options.ModelDirectory is not null && !Directory.Exists(options.ModelDirectory))
        {
            throw new DirectoryNotFoundException($"Model directory not found: {options.ModelDirectory}");
        }

        if (options.FfmpegPath is not null && !File.Exists(options.FfmpegPath))
        {
            throw new FileNotFoundException($"FFmpeg executable not found: {options.FfmpegPath}", options.FfmpegPath);
        }

        if (options.FfprobePath is not null && !File.Exists(options.FfprobePath))
        {
            throw new FileNotFoundException($"FFprobe executable not found: {options.FfprobePath}", options.FfprobePath);
        }

        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(options);
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        return new HeadlessDubbingHost(new HeadlessDubbingSessionFactory(serviceProvider), serviceProvider);
    }

    /// <summary>
    /// Creates a new <see cref="DubbingPipelineEngine"/> bound to this host's session factory.
    /// Resolves the Composition-singleton <see cref="PipelineTransientFaultBus"/> so the
    /// engine publishes to the same stream the diagnostics exporter reads from.
    /// </summary>
    public DubbingPipelineEngine CreateEngine() =>
        new(_sessionFactory, _serviceProvider.GetRequiredService<PipelineTransientFaultBus>());

    /// <summary>
    /// Composition-singleton transient-fault bus. Exposed for Composition-level tests so
    /// they can pin the shared-bus identity contract across the engine and any consumer
    /// that takes the bus via DI.
    /// </summary>
    public PipelineTransientFaultBus TransientFaultBus =>
        _serviceProvider.GetRequiredService<PipelineTransientFaultBus>();

    /// <summary>
    /// Composition-singleton diagnostics bundle exporter. Exposed so tests can pin
    /// shared-bus identity on the exporter side of the C8 wire (spec §4.4 follow-up)
    /// without resorting to reflection. The exporter's own
    /// <c>TransientFaultBus</c> property surfaces the same singleton the engine publishes to.
    /// Cast to concrete is safe: the headless composition only registers the concrete
    /// <see cref="DiagnosticsBundleExporter"/> under the
    /// <c>IDiagnosticsBundleExporter</c> contract via
    /// <c>CompositionRoot.TryAddSingleton&lt;IDiagnosticsBundleExporter, DiagnosticsBundleExporter&gt;</c>.
    /// </summary>
    public DiagnosticsBundleExporter Exporter =>
        (DiagnosticsBundleExporter)_serviceProvider.GetRequiredService<IDiagnosticsBundleExporter>();

    /// <inheritdoc />
    public void Dispose() => _sessionFactory.Dispose();
}
