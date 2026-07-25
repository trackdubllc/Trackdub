using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeFileSystemProbe : IFileSystemProbe
{
    private readonly HashSet<string> existingFiles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> ExistingFiles => existingFiles;

    public bool TreatAllFilesAsExisting { get; set; }

    public void SeedExistingFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        existingFiles.Add(GetFullPath(path));
    }

    public void RemoveExistingFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        existingFiles.Remove(GetFullPath(path));
    }

    public string GetFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return TreatAllFilesAsExisting || existingFiles.Contains(GetFullPath(path));
    }
}
