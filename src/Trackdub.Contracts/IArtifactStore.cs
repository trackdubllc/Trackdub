using Trackdub.Contracts.Transcripts;

namespace Trackdub.Contracts;

public interface IArtifactStore
{
    Task EnsureLayoutAsync(CancellationToken cancellationToken);

    ArtifactWriteHandle CreateWriteHandle(string relativePath);

    Task CommitAsync(ArtifactWriteHandle handle, CancellationToken cancellationToken);

    /// <summary>Create-only promotion. Implementations must mark the receipt immediately after
    /// the move succeeds, including when a later operation throws. Existing files are never replaced.</summary>
    Task CommitNewAsync(ArtifactWriteHandle handle, ArtifactPromotionReceipt receipt, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This artifact store does not support create-only publication.");

    Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken);

    Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken);

    string GetPath(string relativePath);

    bool Exists(string relativePath);
}
