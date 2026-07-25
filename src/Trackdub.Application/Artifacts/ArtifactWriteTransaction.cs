using Trackdub.Contracts;

namespace Trackdub.Application.Artifacts;

/// <summary>
/// Wraps an <see cref="ArtifactWriteHandle"/> and ensures the temporary file is deleted
/// if the write is not committed before disposal.
/// </summary>
internal sealed class ArtifactWriteTransaction(ArtifactWriteHandle handle) : IAsyncDisposable
{
    private bool _committed;

    public string TemporaryPath => handle.TemporaryPath;
    public string FinalPath => handle.FinalPath;

    public async Task CommitAsync(IArtifactStore store, CancellationToken cancellationToken)
    {
        await store.CommitAsync(handle, cancellationToken).ConfigureAwait(false);
        _committed = true;
    }

    public ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            try
            {
                if (File.Exists(handle.TemporaryPath))
                {
                    File.Delete(handle.TemporaryPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Dispose must not throw. Temp file cleanup is best-effort;
                // if deletion fails (e.g., file locked) the OS will reclaim it on next startup.
                System.Diagnostics.Debug.WriteLine(
                    $"ArtifactWriteTransaction: failed to delete temp file '{handle.TemporaryPath}': {ex.Message}");
            }
        }
        return ValueTask.CompletedTask;
    }
}
