namespace Trackdub.Contracts.ModelOptimization;

public interface IOliveEnvironmentService
{
    string GetManagedPythonPath(OliveExecutionProvider provider);

    /// <summary>
    /// Returns the olive executable to invoke for <paramref name="provider"/>.
    /// Prefers a system-wide olive on PATH; falls back to the managed venv olive.
    /// </summary>
    string GetOliveExecutablePath(OliveExecutionProvider provider);

    Task<OliveEnvironmentStatus> GetStatusAsync(OliveExecutionProvider provider, CancellationToken cancellationToken);

    IAsyncEnumerable<string> BootstrapAsync(OliveExecutionProvider provider, CancellationToken cancellationToken);
}
