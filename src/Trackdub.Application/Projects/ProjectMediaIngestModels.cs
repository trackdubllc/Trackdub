using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;

namespace Trackdub.Application.Projects;

public sealed record CreateProjectFromMediaRequest(
    string ProjectName,
    string SourceMediaPath);

public sealed record CreateProjectFromMediaResult(
    TrackdubProject Project,
    MediaAsset MediaAsset,
    SourceMediaReference SourceReference,
    ProjectArtifact AudioArtifact,
    ProjectArtifact WaveformArtifact,
    ProjectArtifact StemSeparationSourceAudioArtifact);

public sealed record RelocateSourceMediaRequest(
    string NewSourceMediaPath);

public sealed record RenameProjectRequest(
    string ProjectName,
    string? SelectedTranslationTargetLanguage = null);

public sealed record OpenProjectResult(
    TrackdubProject Project,
    MediaAsset? MediaAsset,
    SourceMediaReference? SourceReference,
    SourceMediaStatus SourceStatus,
    string? SourceStatusMessage,
    IReadOnlyList<ProjectArtifact> Artifacts,
    string? TranscriptLanguage,
    ProjectUiSettings? UiSettings = null);
