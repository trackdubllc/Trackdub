using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackApplyServiceTests
{
    [Fact]
    public async Task ApplyAsync_does_not_mutate_settings_when_install_incomplete()
    {
        (StarterPackApplyService service, FakeStudioSettingsService settings, _) = CreateService(
            inventoryEntries:
            [
                CreateEntry("onnx-community/silero-vad", ModelCacheState.Ready),
            ]);

        StudioSettings before = settings.CurrentSettings;

        StarterPackApplyResult result = await service.ApplyAsync("basic", "default");

        Assert.False(result.Success);
        Assert.Contains("Download pack first", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(before, settings.CurrentSettings);
    }

    [Fact]
    public async Task ApplyAsync_blocks_unverified_commercial_models()
    {
        const string unverifiedModelId = "cgus/diar_streaming_sortformer_4spk-v2.1-onnx";
        BundledModelManifestRegistry testRegistry = LoadDefaultRegistryWithCommercialUseVerified(
            unverifiedModelId,
            commercialUseVerified: false);
        (StarterPackApplyService service, FakeStudioSettingsService settings, _) =
            CreateService(allInstalled: true, registryOverride: testRegistry);

        StarterPackApplyResult result = await service.ApplyAsync("balanced", "default");

        Assert.False(result.Success);
        Assert.Contains("License review needed", result.FailureReason, StringComparison.Ordinal);
        Assert.Null(settings.CurrentSettings.AppliedStarterPackId);
    }

    [Fact]
    public async Task BuildUpdatedSettings_merges_hardware_and_variant_overrides()
    {
        StarterPackCatalog catalog = new();
        StarterPackDefinition pack = await catalog.GetAsync("basic");
        StarterPackProfileDefinition profile = StarterPackResolver.ResolveProfile(pack, "default");
        StarterPackApplySettings applySettings = StarterPackApplyContract.Resolve(pack.Id, profile.Id);

        StudioSettings current = StudioSettings.Default with
        {
            HardwareOverrides = new Dictionary<string, ExecutionProviderKind>
            {
                ["OverlapRescue"] = ExecutionProviderKind.Cpu,
            },
            ModelVariantOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["overlaprescue:overlap"] = "legacy",
            },
        };

        StudioSettings updated = StarterPackApplyService.BuildUpdatedSettings(
            current,
            pack,
            profile,
            applySettings,
            StarterPackHardwareProfile.BalancedGpu);

        Assert.Equal(ExecutionProviderKind.Cpu, updated.HardwareOverrides!["OverlapRescue"]);
        Assert.Equal("legacy", updated.ModelVariantOverrides!["overlaprescue:overlap"]);
        Assert.Equal("gpu-int4", updated.ModelVariantOverrides!["translation"]);
        Assert.Equal(
            "gpu-int4",
            updated.ModelVariantOverrides![ModelVariantOverrideKeys.Build("translation", "phi-4-mini")]);
        Assert.Equal("fast", updated.ModelTierPreference);
        Assert.Equal("basic", updated.AppliedStarterPackId);
        Assert.Equal("default", updated.AppliedStarterPackProfileId);
        Assert.Equal("whisper-tiny", updated.StageModelAliases!["asr"]);
    }

    private static (StarterPackApplyService Service, FakeStudioSettingsService Settings, BundledModelManifestRegistry Registry)
        CreateService(
            IEnumerable<ModelInventoryEntry>? inventoryEntries = null,
            bool allInstalled = false,
            BundledModelManifestRegistry? registryOverride = null)
    {
        BundledModelManifestRegistry? registry = registryOverride;
        if (registry is null)
        {
            Assert.True(
                BundledModelManifestRegistry.TryLoadDefault(out registry, out string? error),
                error);
        }

        StarterPackCatalog catalog = new();
        var settings = new FakeStudioSettingsService();
        FakeModelInventoryService inventory = new(inventoryEntries);

        if (allInstalled)
        {
            StarterPackDefinition balanced = catalog.GetAsync("balanced").GetAwaiter().GetResult();
            IReadOnlyList<string> required = StarterPackResolver.GetRequiredModelIds(balanced, "default");
            inventory.SetEntries(required.Select(modelId => CreateEntry(modelId, ModelCacheState.Ready)));
        }

        var hardwareProfiler = new FakeHardwareProfilerService();
        var consent = new FakeConsentService();
        var compatibility = new PermissiveStarterPackCompatibilityService();
        var cloudReadiness = new PermissiveCloudCredentialReadinessService();
        var service = new StarterPackApplyService(
            catalog,
            settings,
            inventory,
            registry!,
            hardwareProfiler,
            consent,
            compatibility,
            cloudReadiness);

        return (service, settings, registry!);
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

    private sealed class PermissiveCloudCredentialReadinessService : ICloudCredentialReadiness
    {
        public Task<CloudCredentialReadinessReport> EvaluateAsync(
            StarterPackCloudDefaults cloudDefaults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudCredentialReadinessReport(true, [], null));
    }

    private sealed class DenyCloudCredentialReadinessService : ICloudCredentialReadiness
    {
        public Task<CloudCredentialReadinessReport> EvaluateAsync(
            StarterPackCloudDefaults cloudDefaults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudCredentialReadinessReport(
                false,
                ["openai"],
                "Configure OpenAI API keys in Cloud Models before applying this pack."));
    }

    private static ModelInventoryEntry CreateEntry(string modelId, ModelCacheState state) =>
        new(
            modelId,
            modelId,
            "task",
            "onnx",
            "MIT",
            CommercialAllowed: true,
            CommercialUseVerified: true,
            state,
            FileSizeBytes: 1,
            CachedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: null);

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
}
