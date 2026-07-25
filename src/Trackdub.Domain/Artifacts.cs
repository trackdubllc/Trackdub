namespace Trackdub.Domain;

public sealed record ArtifactRecord(
    Guid Id,
    Guid ProjectId,
    Guid? StageRunId,
    string Kind,
    string RelativePath,
    string ContentHash,
    string Provenance,
    DateTimeOffset CreatedAtUtc)
{
    public static ArtifactRecord Register(
        Guid projectId,
        Guid? stageRunId,
        string kind,
        string relativePath,
        string contentHash,
        string provenance,
        DateTimeOffset createdAtUtc)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Artifact kind is required.", nameof(kind));
        }

        string normalizedKind = kind.Trim();
        string normalizedRelativePath = relativePath.Trim();
        string normalizedContentHash = contentHash.Trim();
        string normalizedProvenance = provenance.Trim();

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Artifact relative path is required.", nameof(relativePath));
        }

        if (IsAbsoluteArtifactPath(normalizedRelativePath))
        {
            throw new ArgumentException("Artifact paths must be project-relative.", nameof(relativePath));
        }

        if (string.IsNullOrWhiteSpace(contentHash))
        {
            throw new ArgumentException("Artifact hash is required.", nameof(contentHash));
        }

        if (string.IsNullOrWhiteSpace(provenance))
        {
            throw new ArgumentException("Artifact provenance is required.", nameof(provenance));
        }

        return new ArtifactRecord(
            Guid.NewGuid(),
            projectId,
            stageRunId,
            normalizedKind,
            normalizedRelativePath,
            normalizedContentHash,
            normalizedProvenance,
            createdAtUtc);
    }

    private static bool IsAbsoluteArtifactPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return true;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return path.Length >= 2 &&
               IsAsciiLetter(path[0]) &&
               path[1] == ':';
    }

    private static bool IsAsciiLetter(char value) =>
        (value >= 'A' && value <= 'Z') ||
        (value >= 'a' && value <= 'z');
}
