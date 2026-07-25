namespace Trackdub.Contracts;

public interface IFileSystemProbe
{
    string GetFullPath(string path);

    bool FileExists(string path);
}
