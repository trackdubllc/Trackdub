using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Sdk;

/// <summary>
/// Configuration options for headless Trackdub SDK hosts.
/// Captures storage paths, execution preferences, and service overrides for <see cref="TrackdubBuilder"/> and headless sessions.
/// </summary>
public sealed record TrackdubOptions
{
    public string? ModelDirectory { get; init; }
    public string? ModelCacheDirectory { get; init; }
    public string? LogDirectory { get; init; }
    public ExecutionProviderPreference ExecutionProvider { get; init; } = ExecutionProviderPreference.Auto;
    public WindowsMlExecutionDevicePolicy WindowsMlExecutionDevicePolicy { get; init; } = WindowsMlExecutionDevicePolicy.Explicit;
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public IApplicationLogger? Logger { get; init; }
    public Action<IServiceCollection>? ServiceConfigurator { get; init; }
}
