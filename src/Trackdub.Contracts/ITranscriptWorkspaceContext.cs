namespace Trackdub.Contracts;

public interface ITranscriptWorkspaceContext
{
    string ProjectRootPath { get; }

    StudioSettings Settings { get; }
}
