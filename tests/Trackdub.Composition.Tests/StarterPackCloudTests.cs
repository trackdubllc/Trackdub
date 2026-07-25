using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackCloudTests
{
    [Fact]
    public async Task DownloadAsync_cloud_pack_is_no_op_when_api_keys_present()
    {
        StarterPackDownloadService service = CreateDownloadService(ready: true);

        StarterPackDownloadResult result = await service.DownloadAsync("cloud", "default");

        Assert.True(result.Success);
        Assert.Empty(result.Outcomes);
    }

    [Fact]
    public async Task DownloadAsync_cloud_pack_fails_when_api_keys_missing()
    {
        StarterPackDownloadService service = CreateDownloadService(ready: false);

        StarterPackDownloadResult result = await service.DownloadAsync("cloud", "default");

        Assert.False(result.Success);
        Assert.Contains("Cloud Models", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_cloud_pack_sets_cloud_overrides_when_keys_present()
    {
        (StarterPackApplyService service, FakeStudioSettingsService settings) = CreateApplyService(ready: true);

        StarterPackApplyResult result = await service.ApplyAsync("cloud", "default");

        Assert.True(result.Success);
        Assert.Equal("cloud", settings.CurrentSettings.AppliedStarterPackId);
        Assert.Equal(AsrModelOverride.OpenAiWhisper, settings.CurrentSettings.AsrModelOverride);
        Assert.Equal(TtsModelOverride.OpenAiTts, settings.CurrentSettings.TtsModelOverride);
        Assert.Equal(TranslationModelOverride.OpenAiGpt, settings.CurrentSettings.TranslationModelOverride);
        Assert.Equal(
            AsrModelOverrideSettings.OpenAiWhisperCloudAlias,
            settings.CurrentSettings.StageModelAliases!["asr"]);
        Assert.Equal(
            TtsModelOverrideSettings.OpenAiTtsCloudAlias,
            settings.CurrentSettings.StageModelAliases!["tts"]);
        Assert.Equal(
            TranslationModelOverrideSettings.OpenAiGptCloudAlias,
            settings.CurrentSettings.StageModelAliases!["translation"]);
    }

    [Fact]
    public async Task ApplyAsync_cloud_pack_clears_stale_local_stage_aliases_and_variants()
    {
        (StarterPackApplyService service, FakeStudioSettingsService settings) = CreateApplyService(ready: true);
        await settings.SaveAsync(
            StudioSettings.Default with
            {
                AppliedStarterPackId = "balanced",
                StageModelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["asr"] = "whisper-tiny",
                    ["translation"] = "phi-4-mini",
                    ["tts"] = "kokoro-onnx",
                },
                ModelVariantOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["translation"] = "gpu-int4",
                    [ModelVariantOverrideKeys.Build("translation", "phi-4-mini")] = "gpu-int4",
                    ["tts"] = "cpu-fp32",
                },
            },
            TestContext.Current.CancellationToken);

        StarterPackApplyResult result = await service.ApplyAsync("cloud", "default");

        Assert.True(result.Success);
        Assert.Equal(
            AsrModelOverrideSettings.OpenAiWhisperCloudAlias,
            settings.CurrentSettings.StageModelAliases!["asr"]);
        Assert.Equal(
            TranslationModelOverrideSettings.OpenAiGptCloudAlias,
            settings.CurrentSettings.StageModelAliases!["translation"]);
        Assert.Equal(
            TtsModelOverrideSettings.OpenAiTtsCloudAlias,
            settings.CurrentSettings.StageModelAliases!["tts"]);
        Assert.False(settings.CurrentSettings.ModelVariantOverrides!.ContainsKey("translation"));
        Assert.False(settings.CurrentSettings.ModelVariantOverrides.ContainsKey(
            ModelVariantOverrideKeys.Build("translation", "phi-4-mini")));
        Assert.False(settings.CurrentSettings.ModelVariantOverrides.ContainsKey("tts"));
    }

    [Fact]
    public async Task ApplyAsync_cloud_pack_clears_stale_hardware_overrides()
    {
        (StarterPackApplyService service, FakeStudioSettingsService settings) = CreateApplyService(ready: true);
        await settings.SaveAsync(
            StudioSettings.Default with
            {
                HardwareOverrides = new Dictionary<string, ExecutionProviderKind>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Vad"] = ExecutionProviderKind.DirectMl,
                    ["Asr"] = ExecutionProviderKind.Migraphx,
                },
            },
            TestContext.Current.CancellationToken);

        StarterPackApplyResult result = await service.ApplyAsync("cloud", "default");

        Assert.True(result.Success);
        Assert.True(
            settings.CurrentSettings.HardwareOverrides is null
            || settings.CurrentSettings.HardwareOverrides.Count == 0);
    }

    [Fact]
    public async Task ApplyAsync_cloud_pack_reports_missing_keys_without_hardware_profiler()
    {
        var hardwareProfiler = new FailingHardwareProfilerService();
        (StarterPackApplyService service, FakeStudioSettingsService settings) = CreateApplyService(
            ready: false,
            hardwareProfiler: hardwareProfiler);

        StarterPackApplyResult result = await service.ApplyAsync("cloud", "default");

        Assert.False(result.Success);
        Assert.Contains("Cloud Models", result.FailureReason, StringComparison.Ordinal);
        Assert.False(hardwareProfiler.WasCalled);
        Assert.Null(settings.CurrentSettings.AppliedStarterPackId);
    }

    [Fact]
    public async Task ApplyAsync_cloud_pack_blocks_when_api_keys_missing()
    {
        (StarterPackApplyService service, FakeStudioSettingsService settings) = CreateApplyService(ready: false);

        StarterPackApplyResult result = await service.ApplyAsync("cloud", "default");

        Assert.False(result.Success);
        Assert.Contains("Cloud Models", result.FailureReason, StringComparison.Ordinal);
        Assert.Null(settings.CurrentSettings.AppliedStarterPackId);
    }

    [Fact]
    public async Task Cloud_pack_json_loads_with_cloud_kind_and_defaults()
    {
        StarterPackCatalog catalog = new();
        StarterPackDefinition pack = await catalog.GetAsync("cloud");

        Assert.Equal(StarterPackKind.Cloud, pack.PackKind);
        Assert.NotNull(pack.CloudDefaults);
        Assert.Equal("openai-whisper", pack.CloudDefaults!.Asr);
        Assert.Equal("openai-gpt", pack.CloudDefaults.Translation);
        Assert.Empty(pack.Models);
    }

    private static StarterPackDownloadService CreateDownloadService(bool ready)
    {
        StarterPackCatalog catalog = new();
        return new StarterPackDownloadService(
            catalog,
            new FakeModelDownloadOrchestrator(),
            new FakeCloudCredentialReadinessService(ready),
            new FakeHardwareProfilerService());
    }

    private static (StarterPackApplyService Service, FakeStudioSettingsService Settings) CreateApplyService(
        bool ready,
        IHardwareProfilerService? hardwareProfiler = null)
    {
        StarterPackCatalog catalog = new();
        var settings = new FakeStudioSettingsService();
        var inventory = new FakeModelInventoryService();
        hardwareProfiler ??= new FakeHardwareProfilerService();
        var consent = new FakeConsentService();
        var compatibility = new PermissiveStarterPackCompatibilityService();
        var service = new StarterPackApplyService(
            catalog,
            settings,
            inventory,
            CreateManifestRegistry(),
            hardwareProfiler,
            consent,
            compatibility,
            new FakeCloudCredentialReadinessService(ready));

        return (service, settings);
    }

    private static BundledModelManifestRegistry CreateManifestRegistry()
    {
        Assert.True(
            BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error),
            error);
        return registry!;
    }

    private sealed class FakeModelDownloadOrchestrator : IModelDownloadOrchestrator
    {
        public IObservable<ModelStateChange> StateChanges { get; } = new EmptyObservable<ModelStateChange>();

        public Task<ModelDownloadResult> DownloadAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            DownloadAsync(modelId, variantAlias: null, progress, cancellationToken);

        public Task<ModelDownloadResult> DownloadAsync(
            string modelId,
            string? variantAlias,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelDownloadResult(modelId, true, ModelCacheState.Ready, null));

        public Task<ModelDownloadResult> RepairAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelDownloadResult(modelId, true, ModelCacheState.Ready, null));

        public Task<bool> UninstallAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class EmptyObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnCompleted();
            return new NoopDisposable();
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeCloudCredentialReadinessService(bool ready) : ICloudCredentialReadiness
    {
        public Task<CloudCredentialReadinessReport> EvaluateAsync(
            StarterPackCloudDefaults cloudDefaults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ready
                ? new CloudCredentialReadinessReport(true, [], null)
                : new CloudCredentialReadinessReport(
                    false,
                    ["openai"],
                    "Configure OpenAI API keys in Cloud Models before applying this pack."));
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

    private sealed class FailingHardwareProfilerService : IHardwareProfilerService
    {
        public bool WasCalled { get; private set; }

        public Task<HardwareProfilerViewState> GetViewStateAsync(CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Hardware profiler should not run for cloud packs.");
        }

        public Task<HardwareProfilerRunResult> RunBenchmarkSuiteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(HardwareProfilerRunResult.Failure("Profiler not configured."));

        public string ResolveEffectiveModelTierPreference(StudioSettings settings) => "balanced";
    }
}
