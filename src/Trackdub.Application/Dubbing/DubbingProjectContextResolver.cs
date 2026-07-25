using Trackdub.Application.Transcripts;

using Trackdub.Contracts.Dubbing;

namespace Trackdub.Application.Dubbing;

/// <summary>
/// Opens an existing project and reads persisted media/language settings from SQLite.
/// </summary>
public static class DubbingProjectContextResolver
{
    /// <summary>
    /// Opens the project at <paramref name="projectRootPath"/> when a database is present.
    /// </summary>
    public static async Task<DubbingProjectContext?> TryOpenAsync(
        IDubbingSessionFactory factory,
        string projectRootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        if (!DubbingProjectPaths.ContainsDatabase(projectRootPath))
        {
            return null;
        }

        try
        {
            await using IDubbingSession session = factory.CreateSession(projectRootPath);
            TranscriptProjectState state = await session.Workspace.Project
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            string? sourceMediaPath = state.ProjectState.SourceReference?.OriginalPath
                ?? state.ProjectState.MediaAsset?.SourceFilePath;

            string? targetLanguageCode = state.SelectedTranslationTargetLanguage
                ?? state.CurrentTranslationRevision?.TargetLanguage;

            return new DubbingProjectContext(sourceMediaPath, targetLanguageCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
