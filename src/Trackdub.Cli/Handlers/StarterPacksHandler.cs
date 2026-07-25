using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

internal static class StarterPacksHandler
{
    private static IStarterPackCoordinator Coordinator(TrackdubSessionFactory factory) =>
        factory.GetRequiredService<IStarterPackCoordinator>();

    public static Task<IReadOnlyList<StarterPackSummary>> ListSummariesAsync(
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken) =>
        Coordinator(factory).ListAsync(cancellationToken);

    public static Task<string?> GetRecommendedPackIdAsync(
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken) =>
        Coordinator(factory).GetRecommendedPackIdAsync(cancellationToken);

    public static Task<bool> RequiresVoiceCloningConsentAsync(
        TrackdubSessionFactory factory,
        string packId,
        string profileId,
        CancellationToken cancellationToken) =>
        Coordinator(factory).RequiresVoiceCloningConsentAsync(packId, profileId, cancellationToken);

    public static Task<StarterPackSummary> GetSummaryAsync(
        TrackdubSessionFactory factory,
        string packId,
        string profileId,
        CancellationToken cancellationToken) =>
        Coordinator(factory).GetSummaryAsync(packId, profileId, cancellationToken);

    public static Task<StarterPackDownloadResult> DownloadPackAsync(
        TrackdubSessionFactory factory,
        string packId,
        string profileId,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        Coordinator(factory).DownloadAsync(packId, profileId, progress, cancellationToken);

    public static Task<StarterPackApplyResult> ApplyPackAsync(
        TrackdubSessionFactory factory,
        string packId,
        string profileId,
        bool acceptVoiceCloningConsent,
        CancellationToken cancellationToken) =>
        Coordinator(factory).ApplyAsync(packId, profileId, acceptVoiceCloningConsent, cancellationToken);
}
