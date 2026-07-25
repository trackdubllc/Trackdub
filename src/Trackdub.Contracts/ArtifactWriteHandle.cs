namespace Trackdub.Contracts;

/// <summary>
/// Represents a write handle for artifacts with automatic cleanup of temporary files.
/// </summary>
public sealed class ArtifactWriteHandle : IDisposable, IAsyncDisposable
{
    private bool disposed;

    public string RelativePath { get; }
    public string FinalPath { get; }
    public string TemporaryPath { get; }

    public ArtifactWriteHandle(string relativePath, string finalPath, string temporaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);

        RelativePath = relativePath;
        FinalPath = finalPath;
        TemporaryPath = temporaryPath;
    }

    /// <summary>
    /// Cleans up the temporary file if it still exists.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CleanupTemporaryFile();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously cleans up the temporary file if it still exists.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        CleanupTemporaryFile();
        disposed = true;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void CleanupTemporaryFile()
    {
        try
        {
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to cleanup temporary file '{TemporaryPath}': {ex.Message}");
        }
    }
}
