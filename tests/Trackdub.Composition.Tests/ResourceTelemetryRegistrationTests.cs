using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Application.Benchmarking;
using Trackdub.Composition.Headless;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Infrastructure.Diagnostics;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Composition.Tests;

public sealed class ResourceTelemetryRegistrationTests
{
    [Fact]
    public void Headless_registers_resolvable_singleton_resource_services()
    {
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub();
        using var provider = services.BuildServiceProvider();
        var collector = provider.GetRequiredService<IResourceTelemetryCollector>();
        var validator = provider.GetRequiredService<IResourceTelemetryValidator>();
        var workingSetSampler = provider.GetRequiredService<IWorkingSetSampler>();
        var gpuMemoryReader = provider.GetRequiredService<IProcessGpuMemoryReader>();
        Assert.IsType<ProcessResourceTelemetryCollector>(collector);
        Assert.IsType<ResourceTelemetryValidator>(validator);
        Assert.IsType<ProcessWorkingSetSampler>(workingSetSampler);
        Assert.Same(collector, provider.GetRequiredService<IResourceTelemetryCollector>());
        Assert.Same(validator, provider.GetRequiredService<IResourceTelemetryValidator>());
        Assert.Same(workingSetSampler, provider.GetRequiredService<IWorkingSetSampler>());
        Assert.Same(gpuMemoryReader, provider.GetRequiredService<IProcessGpuMemoryReader>());
    }

    [Fact]
    public void Headless_process_gpu_memory_reader_degrades_instead_of_throwing()
    {
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub();
        using var provider = services.BuildServiceProvider();

        IProcessGpuMemoryReader reader = provider.GetRequiredService<IProcessGpuMemoryReader>();

        // Exercised through the same port the collector uses, so this covers the real
        // Windows PDH probe where the host provides it and the explicit unavailable fallback
        // everywhere else. Either way the reading never throws and never invents a zero.
        long? reading = reader.ReadDedicatedGpuMemoryBytes();
        if (reading is null)
        {
            Assert.False(string.IsNullOrWhiteSpace(reader.UnavailableReason));
        }
        else
        {
            Assert.True(reading.Value >= 0);
        }
    }

    [Fact]
    public void Headless_host_startup_binds_the_process_gpu_reader_to_shared_pool_admission()
    {
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub();
        using var provider = services.BuildServiceProvider();

        // Host construction — not telemetry-service resolution — is what hands the final reader
        // registration to the shared session pool, so accelerator admission is armed before the
        // first session even when nothing ever asks for the collector. This test deliberately
        // never resolves IProcessGpuMemoryReader itself: it only builds the factory that every
        // headless host path constructs (HeadlessDubbingHost and TrackdubBuilder →
        // TrackdubSessionFactory), then verifies the pool picked up the container's reader.
        _ = new HeadlessDubbingSessionFactory(provider);

        IProcessGpuMemoryReader reader = provider.GetRequiredService<IProcessGpuMemoryReader>();
        try
        {
            Assert.Same(reader, SharedPoolOptions.ProcessGpuMemoryReader);
        }
        finally
        {
            SharedPoolOptions.UseProcessGpuMemoryReader(null);
        }
    }

    [Fact]
    public void Headless_preserves_pre_registered_resource_services()
    {
        var collector = new ProcessResourceTelemetryCollector();
        var validator = new ResourceTelemetryValidator();
        var services = new ServiceCollection();
        services.AddSingleton<IResourceTelemetryCollector>(collector);
        services.AddSingleton<IResourceTelemetryValidator>(validator);
        var workingSetSampler = new ProcessWorkingSetSampler();
        services.AddSingleton<IWorkingSetSampler>(workingSetSampler);
        var gpuMemoryReader = new UnavailableProcessGpuMemoryReader();
        services.AddSingleton<IProcessGpuMemoryReader>(gpuMemoryReader);
        services.AddHeadlessTrackdub();
        using var provider = services.BuildServiceProvider();
        Assert.Same(collector, provider.GetRequiredService<IResourceTelemetryCollector>());
        Assert.Same(validator, provider.GetRequiredService<IResourceTelemetryValidator>());
        Assert.Same(workingSetSampler, provider.GetRequiredService<IWorkingSetSampler>());
        Assert.Same(gpuMemoryReader, provider.GetRequiredService<IProcessGpuMemoryReader>());
    }

    [Fact]
    public void Headless_configurator_can_replace_resource_services_after_defaults()
    {
        var collector = new ProcessResourceTelemetryCollector();
        var validator = new ResourceTelemetryValidator();
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(new HeadlessTrackdubOptions
        {
            ServiceConfigurator = configured =>
            {
                Assert.Contains(configured, item => item.ServiceType == typeof(IResourceTelemetryCollector));
                Assert.Contains(configured, item => item.ServiceType == typeof(IResourceTelemetryValidator));
                Assert.Contains(configured, item => item.ServiceType == typeof(IWorkingSetSampler));
                configured.Replace(ServiceDescriptor.Singleton<IResourceTelemetryCollector>(collector));
                configured.Replace(ServiceDescriptor.Singleton<IResourceTelemetryValidator>(validator));
            }
        });
        using var provider = services.BuildServiceProvider();
        Assert.Same(collector, provider.GetRequiredService<IResourceTelemetryCollector>());
        Assert.Same(validator, provider.GetRequiredService<IResourceTelemetryValidator>());
    }
}
