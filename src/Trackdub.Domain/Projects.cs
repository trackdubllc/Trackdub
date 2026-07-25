namespace Trackdub.Domain;

public sealed record ProjectRecord(
    Guid Id,
    string Name,
    string RootPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static ProjectRecord CreateNew(string name, string rootPath, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Project name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Project root path is required.", nameof(rootPath));
        }

        string normalizedName = name.Trim();
        string normalizedRootPath = Path.GetFullPath(rootPath.Trim());
        return new ProjectRecord(Guid.NewGuid(), normalizedName, normalizedRootPath, createdAtUtc, createdAtUtc);
    }
}
