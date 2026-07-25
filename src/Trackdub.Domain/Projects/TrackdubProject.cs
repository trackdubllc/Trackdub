namespace Trackdub.Domain.Projects;

public sealed record TrackdubProject(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
