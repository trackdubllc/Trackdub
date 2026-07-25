using Trackdub.Application.Dubbing;

namespace Trackdub.Sdk;

/// <summary>
/// Shared pipeline stage metadata for SDK and CLI callers.
/// Forwards to <see cref="DubbingPipelineStages"/>.
/// </summary>
public static class TrackdubPipelineStages
{
    /// <summary>
    /// Returns true when the stage reads from the original source media file directly.
    /// </summary>
    public static bool RequiresSourceMedia(string stageName) =>
        DubbingPipelineStages.RequiresSourceMedia(stageName);

    /// <summary>
    /// Returns true when the stage's execution logic consumes the session's target language code.
    /// </summary>
    public static bool RequiresTargetLanguage(string stageName) =>
        DubbingPipelineStages.RequiresTargetLanguage(stageName);
}
