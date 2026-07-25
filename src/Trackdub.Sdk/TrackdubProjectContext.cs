using Trackdub.Application.Dubbing;

namespace Trackdub.Sdk;

/// <summary>
/// Source media and language settings loaded from an on-disk project.
/// </summary>
public sealed record TrackdubProjectContext(
    string? SourceMediaPath,
    string? TargetLanguageCode)
{
    /// <summary>
    /// Creates an SDK context from the Application DTO.
    /// </summary>
    public static TrackdubProjectContext From(DubbingProjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(context.SourceMediaPath, context.TargetLanguageCode);
    }
}
