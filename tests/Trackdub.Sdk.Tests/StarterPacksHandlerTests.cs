using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Trackdub.Cli.Handlers;
using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Sdk;
using Trackdub.Sdk.Composition;
using Trackdub.TestDoubles;

namespace Trackdub.Sdk.Tests;

public sealed class StarterPacksHandlerTests
{
    [Fact]
    public async Task DownloadPackAsync_does_not_change_studio_settings()
    {
        FakeStudioSettingsService? fakeSettings = null;
        using TrackdubSessionFactory factory = CreateFactory(out fakeSettings);
        Assert.NotNull(fakeSettings);
        StudioSettings before = fakeSettings!.CurrentSettings with
        {
            AppliedStarterPackId = null,
            ModelTierPreference = "balanced",
            StageModelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
        await fakeSettings.SaveAsync(before, CancellationToken.None);

        StarterPackDownloadResult result = await StarterPacksHandler.DownloadPackAsync(
            factory,
            "basic",
            "default",
            progress: null,
            CancellationToken.None);

        Assert.Equal(before, fakeSettings!.CurrentSettings);
        _ = result;
    }

    [Fact]
    public async Task ListSummariesAsync_marks_balanced_license_review_when_sortformer_unverified()
    {
        BundledModelManifestRegistry registry = LoadDefaultRegistryWithCommercialUseVerified(
            "cgus/diar_streaming_sortformer_4spk-v2.1-onnx",
            commercialUseVerified: false);
        using TrackdubSessionFactory factory = CreateFactory(out _, manifestRegistry: registry);
        IReadOnlyList<StarterPackSummary> summaries = await StarterPacksHandler
            .ListSummariesAsync(factory, CancellationToken.None);

        StarterPackSummary? balanced = summaries.FirstOrDefault(summary =>
            string.Equals(summary.Id, "balanced", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(balanced);
        Assert.True(balanced!.HasCommercialVerificationGap);
        Assert.False(balanced.CanApply);
        Assert.Equal("license review needed", balanced.StatusLabel);
    }

    [Fact]
    public async Task GetSummaryAsync_counts_required_models_for_default_profile()
    {
        StarterPackCatalog catalog = new();
        StarterPackDefinition balanced = await catalog.GetAsync("balanced");
        IReadOnlyList<string> requiredModelIds = StarterPackResolver.GetRequiredModelIds(balanced, "default");
        List<ModelInventoryEntry> inventory = requiredModelIds
            .Select(CreateInventoryEntry)
            .ToList();

        using TrackdubSessionFactory factory = CreateFactory(out _, inventoryEntries: inventory);

        StarterPackSummary summary = await StarterPacksHandler.GetSummaryAsync(
            factory,
            "balanced",
            "default",
            CancellationToken.None);

        Assert.Equal(requiredModelIds.Count, summary.RequiredCount);
        Assert.Equal(requiredModelIds.Count, summary.InstalledCount);
        Assert.True(summary.CanApply);
    }

    [Fact]
    public async Task NormalizeLegacyProfileId_maps_removed_multilingual_profiles_to_default()
    {
        StarterPackCatalog catalog = new();
        StarterPackDefinition balanced = await catalog.GetAsync("balanced");
        StarterPackDefinition premium = await catalog.GetAsync("premium");

        Assert.Equal("default", StarterPackResolver.NormalizeLegacyProfileId(balanced, "balanced-multilingual"));
        Assert.Equal("default", StarterPackResolver.NormalizeLegacyProfileId(premium, "premium-multilingual"));
        Assert.Equal("default", StarterPackResolver.NormalizeLegacyProfileId(balanced, "default"));
    }

    [Fact]
    public async Task BuildSummary_marks_consent_required_without_blocking_download_complete_packs()
    {
        StarterPackCatalog catalog = new();
        StarterPackDefinition premium = await catalog.GetAsync("premium");
        IReadOnlyList<string> requiredModelIds = StarterPackResolver.GetRequiredModelIds(premium, "default");
        var inventory = new List<ModelInventoryEntry>();
        foreach (string modelId in requiredModelIds)
        {
            inventory.Add(CreateInventoryEntry(modelId));
        }

        BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error);
        Assert.NotNull(registry);
        Assert.True(string.IsNullOrEmpty(error), error);

        StarterPackSummary summary = StarterPackPresentationService.BuildSummary(
            premium,
            "default",
            StudioSettings.Default,
            recommendedPackId: null,
            inventory,
            registry,
            voiceCloningConsentGranted: false);

        Assert.True(summary.RequiresVoiceCloningConsent);
        Assert.False(summary.CanApply);
        Assert.Equal(requiredModelIds.Count, summary.InstalledCount);
        Assert.Equal(requiredModelIds.Count, summary.RequiredCount);
    }

    private static ModelInventoryEntry CreateInventoryEntry(string modelId) =>
        new(
            modelId,
            modelId,
            "task",
            "onnx",
            "MIT",
            CommercialAllowed: true,
            CommercialUseVerified: true,
            ModelCacheState.Ready,
            FileSizeBytes: 1,
            CachedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: null);

    [Fact]
    public void MapTierToPackId_maps_fast_to_basic()
    {
        Assert.Equal("basic", StarterPackPresentationService.MapTierToPackId("fast"));
        Assert.Equal("balanced", StarterPackPresentationService.MapTierToPackId("balanced"));
        Assert.Equal("premium", StarterPackPresentationService.MapTierToPackId("quality"));
    }

    private static TrackdubSessionFactory CreateFactory(out FakeStudioSettingsService? fakeSettings) =>
        CreateFactory(out fakeSettings, inventoryEntries: null, manifestRegistry: null);

    private static TrackdubSessionFactory CreateFactory(
        out FakeStudioSettingsService? fakeSettings,
        IReadOnlyList<ModelInventoryEntry>? inventoryEntries = null,
        BundledModelManifestRegistry? manifestRegistry = null)
    {
        FakeStudioSettingsService? captured = null;
        var options = new TrackdubOptions
        {
            ServiceConfigurator = services =>
            {
                captured = new FakeStudioSettingsService();
                services.Replace(ServiceDescriptor.Singleton<IStudioSettingsService>(captured));
                services.Replace(ServiceDescriptor.Singleton<IStarterPackDownloadService>(
                    new FakeStarterPackDownloadService()));
                services.Replace(ServiceDescriptor.Singleton<IStarterPackCompatibilityService>(
                    new PermissiveStarterPackCompatibilityService()));
                if (inventoryEntries is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<IModelInventoryService>(
                        new FakeModelInventoryService(inventoryEntries)));
                }

                if (manifestRegistry is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(manifestRegistry));
                }
            },
        };

        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(options);
        ServiceProvider provider = services.BuildServiceProvider();
        fakeSettings = captured ?? provider.GetRequiredService<IStudioSettingsService>() as FakeStudioSettingsService;
        return new TrackdubSessionFactory(provider);
    }

    private static BundledModelManifestRegistry LoadDefaultRegistryWithCommercialUseVerified(
        string modelId,
        bool commercialUseVerified)
    {
        Assert.True(
            BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error),
            error);

        BundledModelManifestEntry[] entries = registry!.Entries
            .Select(entry => entry.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase)
                ? entry with { CommercialUseVerified = commercialUseVerified }
                : entry)
            .ToArray();

        var aliasIndex = new Dictionary<string, BundledModelManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (BundledModelManifestEntry entry in entries)
        {
            foreach (string alias in entry.Aliases)
            {
                aliasIndex.Add(alias, entry);
            }
        }

        ConstructorInfo constructor = typeof(BundledModelManifestRegistry).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(IReadOnlyList<BundledModelManifestEntry>), typeof(IReadOnlyDictionary<string, BundledModelManifestEntry>)],
            modifiers: null)
            ?? throw new InvalidOperationException("BundledModelManifestRegistry constructor shape changed.");

        return (BundledModelManifestRegistry)constructor.Invoke(
            [registry.ManifestPath, entries, aliasIndex]);
    }

    private sealed class PermissiveStarterPackCompatibilityService : IStarterPackCompatibilityService
    {
        public Task<StarterPackCompatibilityReport> EvaluateAsync(
            string packId,
            string profileId,
            StarterPackHardwareProfile? hardwareProfile = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StarterPackCompatibilityReport(
                packId,
                profileId,
                "balanced_gpu",
                [],
                AllStagesRunnable: true,
                AnyFallbackApplied: false));
    }

    private sealed class FakeStarterPackDownloadService : IStarterPackDownloadService
    {
        public Task<StarterPackDownloadResult> DownloadAsync(
            string packId,
            string profileId,
            IProgress<Trackdub.Contracts.Licensing.ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StarterPackDownloadResult(
                packId,
                profileId,
                Success: true,
                Outcomes: []));
    }

    private sealed class FakeModelInventoryService : IModelInventoryService
    {
        private readonly IReadOnlyList<ModelInventoryEntry> entries;

        public FakeModelInventoryService(IEnumerable<ModelInventoryEntry> entries)
        {
            this.entries = entries.ToList();
        }

        public Task<IReadOnlyList<ModelInventoryEntry>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(entries);

        public Task<ModelInventoryEntry?> GetByModelIdAsync(
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(entries.FirstOrDefault(entry =>
                string.Equals(entry.ModelId, modelId, StringComparison.OrdinalIgnoreCase)));
    }
}
