namespace Trackdub.Sdk;

/// <summary>
/// Thrown when a project directory is already locked by another process or session,
/// indicating a concurrent run conflict.
/// </summary>
public sealed class ProjectLockedException : InvalidOperationException
{
    /// <summary>
    /// The structured error code for this exception.
    /// </summary>
    public ErrorCode ErrorCode => ErrorCode.ProjectLocked;

    /// <summary>
    /// The project directory that is locked.
    /// </summary>
    public string ProjectDirectory { get; }

    /// <summary>
    /// The process ID that holds the lock, if known.
    /// </summary>
    public int? HoldingProcessId { get; }

    public ProjectLockedException(string projectDirectory)
        : base($"Project directory '{projectDirectory}' is locked by another process.")
    {
        ProjectDirectory = projectDirectory;
    }

    public ProjectLockedException(string projectDirectory, int holdingProcessId)
        : base($"Project directory '{projectDirectory}' is locked by process {holdingProcessId}.")
    {
        ProjectDirectory = projectDirectory;
        HoldingProcessId = holdingProcessId;
    }

    public ProjectLockedException(string projectDirectory, string message, Exception innerException)
        : base(message, innerException)
    {
        ProjectDirectory = projectDirectory;
    }
}
