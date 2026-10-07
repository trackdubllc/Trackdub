namespace Trackdub.Contracts;

public interface IArtifactStore
{
    Task EnsureLayoutAsync(CancellationToken cancellationToken);

    ArtifactWriteHandle CreateWriteHandle(string relativePath);

    Task CommitAsync(ArtifactWriteHandle handle, CancellationToken cancellationToken);

    Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken);

    Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken);

    string GetPath(string relativePath);

    bool Exists(string relativePath);
}
