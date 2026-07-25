namespace Trackdub.Domain.Speakers;

public sealed record ProjectSpeaker(
    Guid Id,
    Guid ProjectId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc)
{
    public static ProjectSpeaker Create(
        Guid projectId,
        string displayName,
        DateTimeOffset createdAtUtc)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        return new ProjectSpeaker(
            Guid.NewGuid(),
            projectId,
            displayName.Trim(),
            createdAtUtc);
    }

    public ProjectSpeaker Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        return this with
        {
            DisplayName = displayName.Trim()
        };
    }
}
