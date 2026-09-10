using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
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

    /// <summary>
    /// Preferred execution provider pin. <c>null</c> means auto (planner probe order).
    /// </summary>
    public ExecutionProviderKind? PreferredExecutionProvider { get; init; }

    /// <summary>
    /// When true and <see cref="PreferredExecutionProvider"/> is set, stages that allow the
    /// provider must use it (no silent soft preference). Headless CLI/SDK pins set this.
    /// </summary>
    public bool RequirePreferredExecutionProvider { get; init; }

    /// <summary>
    /// Legacy four-value preference. Prefer <see cref="PreferredExecutionProvider"/>.
    /// </summary>
    public ExecutionProviderPreference ExecutionProvider
    {
        get => ExecutionProviderPreferenceMapping.ToLegacyPreference(PreferredExecutionProvider);
        init => PreferredExecutionProvider = ExecutionProviderPreferenceMapping.ToPreferredKind(value);
    }

    public WindowsMlExecutionDevicePolicy WindowsMlExecutionDevicePolicy { get; init; } = WindowsMlExecutionDevicePolicy.Explicit;
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public IApplicationLogger? Logger { get; init; }
    public Action<IServiceCollection>? ServiceConfigurator { get; init; }
}
