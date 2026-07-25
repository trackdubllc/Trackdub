using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Read-only service that evaluates pipeline readiness without prompting or provisioning.
/// Call EvaluateAsync with draft selections (for the panel) or frozen snapshot selections
/// (for the pre-run backstop). Never opens dialogs — pure evaluation only.
/// </summary>
public interface IPipelineReadinessService
{
    /// <summary>
    /// Evaluates readiness for all enabled stages against the given model selections and
    /// current project state. Results are cached by (stage, selection-hash, artifact-fingerprint)
    /// and invalidated by selection changes.
    /// </summary>
    Task<PipelineReadinessReport> EvaluateAsync(
        IReadOnlyList<RuntimeStage> enabledStages,
        RuntimeModelSelections selections,
        TranscriptProjectState? state,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null,
        string? targetLanguageCode = null);

    /// <summary>
    /// Invalidates the cached evaluation for the given stages, forcing re-evaluation on
    /// the next EvaluateAsync call. Call after artifact-store writes or selection changes.
    /// </summary>
    void InvalidateCache(IReadOnlyList<RuntimeStage>? stages = null);
}
