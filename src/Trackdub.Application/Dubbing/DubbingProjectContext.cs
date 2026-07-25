namespace Trackdub.Application.Dubbing;

/// <summary>
/// Source media and language settings loaded from an on-disk project.
/// </summary>
public sealed record DubbingProjectContext(
    string? SourceMediaPath,
    string? TargetLanguageCode);
