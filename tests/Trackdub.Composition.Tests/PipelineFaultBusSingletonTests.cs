using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.Dubbing;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Composition.Headless;
using Trackdub.Infrastructure.Diagnostics;

namespace Trackdub.Composition.Tests;

/// <summary>
/// Pins the spec §4.4 follow-up lane C8 cross-process telemetry wire:
/// <list type="bullet">
/// <item><c>PipelineTransientFaultBus</c> registered as a Composition singleton.</item>
/// <item><c>HeadlessDubbingHost.CreateEngine</c> resolves the singleton rather than letting
/// the engine fallback to its own private instance.</item>
/// <item><c>DiagnosticsBundleExporter.TransientFaultBus</c> mirrors the same instance via its
/// optional ctor parameter.</item>
/// </list>
/// </summary>
public sealed class PipelineFaultBusSingletonTests
{
    [Fact]
    public void PipelineTransientFaultBus_is_registered_as_singleton_in_headless_addHeadlessTrackdub()
    {
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub();

        ServiceDescriptor? descriptor = services
            .SingleOrDefault(sd => sd.ServiceType == typeof(PipelineTransientFaultBus));

        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor!.Lifetime);
    }

    [Fact]
    public void HeadlessDubbingHost_CreateEngine_passes_shared_pipeline_bus_to_engine()
    {
        using HeadlessDubbingHost host = HeadlessDubbingHost.Create();

        PipelineTransientFaultBus hostBus = host.TransientFaultBus;
        DubbingPipelineEngine engine = host.CreateEngine();

        // The internal accessor pins the documented identity contract so future refactors
        // cannot regress the wire to per-engine-instance fallback. Visible to this test
        // assembly via InternalsVisibleTo("Trackdub.Composition.Tests") on Application.
        PipelineTransientFaultBus engineBus = engine.TransientFaultBus;

        Assert.NotNull(engineBus);
        Assert.Same(hostBus, engineBus);
    }

    [Fact]
    public void Host_diagnostics_exporter_resolves_same_pipeline_bus_singleton()
    {
        using HeadlessDubbingHost host = HeadlessDubbingHost.Create();

        PipelineTransientFaultBus hostBus = host.TransientFaultBus;
        DiagnosticsBundleExporter exporter = host.Exporter;

        // Exporter singleton is registered as IDiagnosticsBundleExporter in CompositionRoot
        // (CompositionRoot.cs:177 TryAddSingleton) and its ctor's optional 5th param
        // (PipelineTransientFaultBus?) gets DI-injected with the headless singleton so
        // the engine -> host -> exporter end-to-end identity holds.
        Assert.NotNull(exporter);
        Assert.NotNull(exporter.TransientFaultBus);
        Assert.Same(hostBus, exporter.TransientFaultBus);
    }
}
