using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Composition.Headless;

/// <summary>
/// Configuration for headless Trackdub DI bootstrap (CLI, benchmarks, workers).
/// </summary>
public sealed record HeadlessTrackdubOptions
{
    public string? ModelDirectory { get; init; }
    public string? ModelCacheDirectory { get; init; }
    public string? LogDirectory { get; init; }
    public IReadOnlyDictionary<string, ExecutionProviderKind>? HardwareOverrides { get; init; }

    /// <summary>
    /// When true, hardware overrides are hard requirements for stages that allow the provider.
    /// Headless CLI/SDK pins set this; UI soft preferences leave it false.
    /// </summary>
    public bool RequirePreferredExecutionProviders { get; init; }

    public WindowsMlExecutionDevicePolicy WindowsMlExecutionDevicePolicy { get; init; } =
        WindowsMlExecutionDevicePolicy.Explicit;
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public IApplicationLogger? Logger { get; init; }
    public Action<IServiceCollection>? ServiceConfigurator { get; init; }
}
