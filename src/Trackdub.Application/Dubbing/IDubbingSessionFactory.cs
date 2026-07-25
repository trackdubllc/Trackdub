using Trackdub.Application.Projects;
using Trackdub.Contracts;

using Trackdub.Contracts.Dubbing;

namespace Trackdub.Application.Dubbing;

/// <summary>
/// Creates per-project dubbing sessions that expose <see cref="Transcripts.TranscriptWorkspace"/>
/// and scoped DI services for pipeline execution.
/// </summary>
public interface IDubbingSessionFactory : IDisposable
{
    /// <summary>
    /// Creates a new session scoped to the specified project root path.
    /// </summary>
    IDubbingSession CreateSession(string projectRootPath, StudioSettings? settings = null);
}

/// <summary>
/// Per-project session owning a DI scope and workspace for pipeline stages.
/// </summary>
public interface IDubbingSession : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// The root path of the .trackdub project directory.
    /// </summary>
    string ProjectRootPath { get; }

    /// <summary>
    /// Workspace workflows scoped to this session.
    /// </summary>
    Transcripts.TranscriptWorkspace Workspace { get; }

    /// <summary>
    /// Scoped service provider for resolving pipeline services.
    /// </summary>
    IServiceProvider Services { get; }
}
