using Trackdub.Contracts;

namespace Trackdub.Infrastructure.FileSystem;

public sealed class PhysicalFileSystemProbe : IFileSystemProbe
{
    public string GetFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetFullPath(path);
    }

    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.Exists(path);
    }
}
