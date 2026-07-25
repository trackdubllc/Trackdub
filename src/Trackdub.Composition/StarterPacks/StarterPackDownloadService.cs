using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.StarterPacks;

public sealed class StarterPackDownloadService(
    IStarterPackCatalog catalog,
    IModelDownloadOrchestrator downloadOrchestrator,
    ICloudCredentialReadiness cloudCredentialReadiness,
    IHardwareProfilerService hardwareProfilerService) : IStarterPackDownloadService
{
    private readonly IStarterPackCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IModelDownloadOrchestrator downloadOrchestrator =
        downloadOrchestrator ?? throw new ArgumentNullException(nameof(downloadOrchestrator));
    private readonly ICloudCredentialReadiness cloudCredentialReadiness =
        cloudCredentialReadiness ?? throw new ArgumentNullException(nameof(cloudCredentialReadiness));
    private readonly IHardwareProfilerService hardwareProfilerService =
        hardwareProfilerService ?? throw new ArgumentNullException(nameof(hardwareProfilerService));

    public async Task<StarterPackDownloadResult> DownloadAsync(
        string packId,
        string profileId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        StarterPackDefinition pack = await catalog.GetAsync(packId, cancellationToken).ConfigureAwait(false);
        _ = StarterPackResolver.ResolveProfile(pack, profileId);

        if (pack.PackKind == StarterPackKind.Cloud)
        {
            if (pack.CloudDefaults is null)
            {
                return new StarterPackDownloadResult(
                    packId,
                    profileId,
                    Success: false,
                    [],
                    "Cloud pack is missing cloud_defaults.");
            }

            CloudCredentialReadinessReport readiness = await cloudCredentialReadiness
                .EvaluateAsync(pack.CloudDefaults, cancellationToken)
                .ConfigureAwait(false);
            if (!readiness.IsReady)
            {
                return new StarterPackDownloadResult(
                    packId,
                    profileId,
                    Success: false,
                    [],
                    readiness.BlockedReason);
            }

            return new StarterPackDownloadResult(packId, profileId, Success: true, []);
        }

        StarterPackHardwareProfile hardwareProfile = await StarterPackHardwareResolver
            .ResolveHardwareProfileAsync(hardwareProfilerService, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> modelIds = StarterPackResolver.GetRequiredModelIds(pack, profileId);

        var outcomes = new List<ModelDownloadOutcome>();
        foreach (string modelId in modelIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? variantAlias = StarterPackHardwareResolver.ResolveVariantAlias(pack, modelId, hardwareProfile);
            ModelDownloadResult result = await downloadOrchestrator
                .DownloadAsync(modelId, variantAlias, progress, cancellationToken)
                .ConfigureAwait(false);

            outcomes.Add(new ModelDownloadOutcome(
                modelId,
                result.Success,
                result.FailureReason));

            if (!result.Success && !result.Cancelled)
            {
                return new StarterPackDownloadResult(
                    packId,
                    profileId,
                    Success: false,
                    outcomes,
                    result.FailureReason ?? $"Download failed for {modelId}.");
            }

            if (result.Cancelled)
            {
                return new StarterPackDownloadResult(
                    packId,
                    profileId,
                    Success: false,
                    outcomes,
                    "Download cancelled.");
            }
        }

        return new StarterPackDownloadResult(packId, profileId, Success: true, outcomes);
    }
}
