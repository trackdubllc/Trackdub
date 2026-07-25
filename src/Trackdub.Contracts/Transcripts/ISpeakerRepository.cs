using Trackdub.Domain.Speakers;

namespace Trackdub.Contracts.Transcripts;

public interface ISpeakerRepository
{
    Task<IReadOnlyList<ProjectSpeaker>> ListSpeakersAsync(Guid projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SpeakerTurn>> ListTurnsAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ProjectSpeaker> EnsureDefaultSpeakerAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ProjectSpeaker> CreateSpeakerAsync(Guid projectId, CancellationToken cancellationToken);

    Task ReplaceDiarizationAsync(
        Guid projectId,
        IReadOnlyList<ProjectSpeaker> speakers,
        IReadOnlyList<SpeakerTurn> turns,
        CancellationToken cancellationToken);

    Task RenameSpeakerAsync(
        Guid projectId,
        Guid speakerId,
        string displayName,
        CancellationToken cancellationToken);

    Task SplitTurnAsync(
        Guid projectId,
        Guid speakerTurnId,
        double splitSeconds,
        CancellationToken cancellationToken);
}
