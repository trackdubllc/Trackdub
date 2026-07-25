using Trackdub.Contracts.Licensing;

namespace Trackdub.Contracts.StarterPacks;

public interface IStarterPackCatalog
{
    string UserPacksDirectory { get; }

    Task<IReadOnlyList<StarterPackDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken = default);

    Task<StarterPackDefinition> GetAsync(string packId, CancellationToken cancellationToken = default);

    void InvalidateCache();
}

public interface IStarterPackDownloadService
{
    Task<StarterPackDownloadResult> DownloadAsync(
        string packId,
        string profileId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IStarterPackApplyService
{
    Task<StarterPackApplyResult> ApplyAsync(
        string packId,
        string profileId,
        StarterPackHardwareProfile? hardwareProfile = null,
        bool acceptVoiceCloningConsent = false,
        CancellationToken cancellationToken = default);
}
