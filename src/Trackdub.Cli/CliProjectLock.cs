using Trackdub.Sdk;

namespace Trackdub.Cli;

/// <summary>
/// Acquires the cross-process project lock for CLI runs, so a CLI run and a desktop run
/// against the same project are mutually excluded.
/// </summary>
internal static class CliProjectLock
{
    /// <summary>
    /// Acquires the lock on <paramref name="projectDirectory"/>. On conflict, reports the holder
    /// to stderr and returns <see langword="null"/>.
    /// </summary>
    public static ProjectLock? TryAcquire(string projectDirectory)
    {
        try
        {
            return ProjectLock.Acquire(projectDirectory);
        }
        catch (ProjectLockedException ex)
        {
            CliErrorReporter.ReportError(ErrorCode.ProjectLocked, DescribeProjectLocked(ex));
            return null;
        }
    }

    /// <summary>
    /// Mirrors the desktop shell's wording (<c>PipelineStageExecutor.DescribeProjectLocked</c>)
    /// so both hosts describe a lock conflict the same way.
    /// </summary>
    internal static string DescribeProjectLocked(ProjectLockedException exception) =>
        exception.HoldingProcessId switch
        {
            int processId when processId == Environment.ProcessId =>
                "A run is already in progress for this project in this process. Wait for it to finish, then try again.",
            int processId =>
                $"This project is in use by another Trackdub process (PID {processId}). Close it or wait for its run to finish, then try again.",
            _ => "This project is in use by another Trackdub process. Close it or wait for its run to finish, then try again."
        };
}
