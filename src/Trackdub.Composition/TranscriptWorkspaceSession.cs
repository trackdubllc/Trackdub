using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Transcripts;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Composition;

public sealed class TranscriptWorkspaceSession : ITranscriptWorkspaceSession
{
    private readonly IServiceScope scope;
    private bool disposed;

    internal TranscriptWorkspaceSession(
        string projectRootPath,
        IServiceScope scope,
        TranscriptWorkspace workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        ProjectRootPath = projectRootPath;
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public string ProjectRootPath { get; }

    public TranscriptWorkspace Workspace { get; }

    public IServiceProvider Services => scope.ServiceProvider;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        scope.Dispose();
        disposed = true;
    }
}
