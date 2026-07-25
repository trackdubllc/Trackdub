using Trackdub.Application.Dubbing;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Transcripts;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Composition.Headless;

/// <summary>
/// Headless per-project session implementing <see cref="IDubbingSession"/>.
/// </summary>
internal sealed class HeadlessDubbingSession : IDubbingSession
{
    private readonly IServiceScope _scope;

    public HeadlessDubbingSession(IServiceScope scope, string projectRootPath, StudioSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        _scope = scope;

        var context = _scope.ServiceProvider.GetRequiredService<TranscriptWorkspaceContext>();
        context.Initialize(projectRootPath, settings);

        ProjectRootPath = context.ProjectRootPath;
        Workspace = _scope.ServiceProvider.GetRequiredService<TranscriptWorkspace>();
    }

    public string ProjectRootPath { get; }

    public TranscriptWorkspace Workspace { get; }

    public IServiceProvider Services => _scope.ServiceProvider;

    public void Dispose()
    {
        _scope.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_scope is IAsyncDisposable asyncScope)
        {
            await asyncScope.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _scope.Dispose();
        }
    }
}
