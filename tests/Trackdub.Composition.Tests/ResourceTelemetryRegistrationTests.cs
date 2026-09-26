using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Application.Benchmarking;
using Trackdub.Composition.Headless;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Infrastructure.Diagnostics;

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
        Assert.IsType<ProcessResourceTelemetryCollector>(collector);
        Assert.IsType<ResourceTelemetryValidator>(validator);
        Assert.Same(collector, provider.GetRequiredService<IResourceTelemetryCollector>());
        Assert.Same(validator, provider.GetRequiredService<IResourceTelemetryValidator>());
    }

    [Fact]
    public void Headless_preserves_pre_registered_resource_services()
    {
        var collector = new ProcessResourceTelemetryCollector();
        var validator = new ResourceTelemetryValidator();
        var services = new ServiceCollection();
        services.AddSingleton<IResourceTelemetryCollector>(collector);
        services.AddSingleton<IResourceTelemetryValidator>(validator);
        services.AddHeadlessTrackdub();
        using var provider = services.BuildServiceProvider();
        Assert.Same(collector, provider.GetRequiredService<IResourceTelemetryCollector>());
        Assert.Same(validator, provider.GetRequiredService<IResourceTelemetryValidator>());
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
                configured.Replace(ServiceDescriptor.Singleton<IResourceTelemetryCollector>(collector));
                configured.Replace(ServiceDescriptor.Singleton<IResourceTelemetryValidator>(validator));
            }
        });
        using var provider = services.BuildServiceProvider();
        Assert.Same(collector, provider.GetRequiredService<IResourceTelemetryCollector>());
        Assert.Same(validator, provider.GetRequiredService<IResourceTelemetryValidator>());
    }
}
