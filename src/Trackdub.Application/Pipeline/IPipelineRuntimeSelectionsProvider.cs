using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Dubbing;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Optional host-provided runtime model selections for engine runs.
/// <para>
/// The default engine path derives <see cref="RuntimeModelSelections"/> from
/// persisted <c>StudioSettings</c> plus <see cref="DubbingSessionOptions.ModelPreferences"/>.
/// Interactive hosts register an implementation that returns selections built from
/// live UI state (per-stage model choices, hardware overrides, variant picks).
/// </para>
/// <para>
/// Resolved from the session service scope. Return null to defer to the default
/// settings-based selection logic.
/// </para>
/// </summary>
public interface IPipelineRuntimeSelectionsProvider
{
    /// <summary>
    /// Builds runtime selections for an engine run. <paramref name="state"/> is the
    /// project state the engine has loaded so far, or null when no project state is
    /// available yet (e.g. before media-spine creation).
    /// </summary>
    Task<RuntimeModelSelections?> CreateSelectionsAsync(
        TranscriptProjectState? state,
        DubbingSessionOptions options,
        CancellationToken cancellationToken);
}
