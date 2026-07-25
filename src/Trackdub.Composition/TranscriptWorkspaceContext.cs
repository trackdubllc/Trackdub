using Trackdub.Contracts;

namespace Trackdub.Composition;

public sealed class TranscriptWorkspaceContext : ITranscriptWorkspaceContext
{
    private bool initialized;
    private string? projectRootPath;
    private StudioSettings? settings;

    public string ProjectRootPath
    {
        get
        {
            ThrowIfNotInitialized();
            return projectRootPath!;
        }
    }

    public StudioSettings Settings
    {
        get
        {
            ThrowIfNotInitialized();
            return settings!;
        }
    }

    public void Initialize(string projectRootPath, StudioSettings? settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        if (initialized)
        {
            throw new InvalidOperationException("Transcript workspace context has already been initialized.");
        }

        this.projectRootPath = Path.GetFullPath(projectRootPath);
        this.settings = settings ?? StudioSettings.Default;
        initialized = true;
    }

    private void ThrowIfNotInitialized()
    {
        if (!initialized)
        {
            throw new InvalidOperationException("Transcript workspace context has not been initialized.");
        }
    }
}
