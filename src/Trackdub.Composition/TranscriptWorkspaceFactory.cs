using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Transcripts;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Composition;

public sealed class TranscriptWorkspaceFactory(
    IServiceScopeFactory scopeFactory,
    IApplicationLogger? logger = null) : ITranscriptWorkspaceSessionFactory
{
    private readonly IServiceScopeFactory scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IApplicationLogger? _logger = logger;

    public TranscriptWorkspaceSession Create(string projectRootPath, StudioSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        var overall = System.Diagnostics.Stopwatch.StartNew();
        _logger?.LogInformation($"Workspace create start: root='{projectRootPath}'.");
        IServiceScope scope = scopeFactory.CreateScope();
        try
        {
            _logger?.LogInformation("Workspace DI scope created.");
            var context = scope.ServiceProvider.GetRequiredService<TranscriptWorkspaceContext>();
            _logger?.LogInformation($"Workspace context resolve complete after {overall.ElapsedMilliseconds} ms.");
            context.Initialize(projectRootPath, settings);
            _logger?.LogInformation("Workspace context initialized.");

            TranscriptWorkspace workspace = scope.ServiceProvider.GetRequiredService<TranscriptWorkspace>();
            _logger?.LogInformation($"Workspace resolve complete after {overall.ElapsedMilliseconds} ms.");
            _logger?.LogInformation($"Workspace create finished after {overall.ElapsedMilliseconds} ms.");
            return new TranscriptWorkspaceSession(context.ProjectRootPath, scope, workspace);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    ITranscriptWorkspaceSession ITranscriptWorkspaceSessionFactory.Create(string projectRootPath, StudioSettings? settings) =>
        Create(projectRootPath, settings);
}
