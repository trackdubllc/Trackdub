using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackImportExportTests : IDisposable
{
    private readonly string tempRoot;
    private readonly StarterPackCatalog catalog;
    private readonly StarterPackImportExportService importExport;

    public StarterPackImportExportTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.StarterPackImport.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        catalog = new StarterPackCatalog(new FakeAppStoragePaths(tempRoot));
        Assert.True(
            BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error),
            error);
        importExport = new StarterPackImportExportService(
            catalog,
            new StarterPackValidator(),
            registry!,
            new PermissiveCompatibilityService());
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_writes_user_pack_and_lists_it()
    {
        string sourcePath = Path.Combine(tempRoot, "my-fast-pack.json");
        await File.WriteAllTextAsync(sourcePath, ValidUserPackJson("my-fast-pack"));

        StarterPackImportResult result = await importExport.ImportAsync(sourcePath);

        Assert.True(result.Success);
        Assert.Equal("my-fast-pack", result.PackId);
        Assert.True(File.Exists(Path.Combine(catalog.UserPacksDirectory, "my-fast-pack.json")));

        IReadOnlyList<StarterPackDefinition> packs = await catalog.ListDefinitionsAsync();
        StarterPackDefinition imported = packs.First(p => string.Equals(p.Id, "my-fast-pack", StringComparison.Ordinal));
        Assert.Equal(StarterPackOrigin.User, imported.PackOrigin);
        Assert.NotNull(imported.Apply);
    }

    [Fact]
    public async Task ImportAsync_rejects_invalid_pack_id()
    {
        string sourcePath = Path.Combine(tempRoot, "bad-id.json");
        await File.WriteAllTextAsync(sourcePath, ValidUserPackJson("Bad_ID"));

        StarterPackImportResult result = await importExport.ImportAsync(sourcePath);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ApplyAsync_user_pack_uses_data_driven_apply_block()
    {
        string sourcePath = Path.Combine(tempRoot, "my-studio-pack.json");
        await File.WriteAllTextAsync(sourcePath, ValidUserPackJson("my-studio-pack"));
        await importExport.ImportAsync(sourcePath);

        (StarterPackApplyService service, FakeStudioSettingsService settings) = CreateApplyService(catalog);
        StarterPackApplyResult result = await service.ApplyAsync("my-studio-pack", "default");

        Assert.True(result.Success);
        Assert.Equal("my-studio-pack", settings.CurrentSettings.AppliedStarterPackId);
        Assert.Equal(AsrModelOverride.GenAi, settings.CurrentSettings.AsrModelOverride);
        Assert.Equal("whisper-tiny-genai", settings.CurrentSettings.StageModelAliases!["asr"]);
    }

    [Fact]
    public async Task ApplyAsync_hybrid_user_pack_applies_cloud_stage_aliases_from_cloud_stages()
    {
        string sourcePath = Path.Combine(tempRoot, "hybrid-cloud-pack.json");
        await File.WriteAllTextAsync(sourcePath, HybridCloudStagesPackJson("hybrid-cloud-pack"));
        StarterPackImportResult importResult = await importExport.ImportAsync(sourcePath);
        Assert.True(importResult.Success, importResult.FailureReason);

        (StarterPackApplyService service, FakeStudioSettingsService settings) = CreateApplyService(catalog);
        StarterPackApplyResult result = await service.ApplyAsync("hybrid-cloud-pack", "default");

        Assert.True(result.Success);
        Assert.Equal(TranslationModelOverride.OpenAiGpt, settings.CurrentSettings.TranslationModelOverride);
        Assert.Equal(TtsModelOverride.OpenAiTts, settings.CurrentSettings.TtsModelOverride);
        Assert.Equal(
            TranslationModelOverrideSettings.OpenAiGptCloudAlias,
            settings.CurrentSettings.StageModelAliases!["translation"]);
        Assert.Equal(
            TtsModelOverrideSettings.OpenAiTtsCloudAlias,
            settings.CurrentSettings.StageModelAliases!["tts"]);
    }

    [Fact]
    public async Task DeleteUserPackAsync_rejects_invalid_pack_id()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => importExport.DeleteUserPackAsync("../escape"));
    }

    private static string HybridCloudStagesPackJson(string packId) =>
        $$"""
          {
            "schema_version": 1,
            "id": "{{packId}}",
            "pack_origin": "user",
            "pack_kind": "hybrid",
            "display_name": "Hybrid cloud pack",
            "tier_preference": "balanced",
            "description": "Hybrid test pack",
            "profiles": [{ "id": "default", "display_name": "Default" }],
            "models": [
              {
                "model_id": "onnx-community/silero-vad",
                "stage": "vad",
                "required": true,
                "alias": "silero-vad",
                "runtime_defaults": {
                  "cpu_safe": { "variant": "int8", "execution_provider": "cpu" }
                }
              }
            ],
            "apply": {
              "tier_preference": "balanced",
              "cloud_stages": {
                "translation": "openai-gpt",
                "tts": "openai"
              }
            },
            "olive_auto_run": false
          }
          """;

    private static string ValidUserPackJson(string packId) =>
        $$"""
          {
            "schema_version": 1,
            "id": "{{packId}}",
            "pack_origin": "user",
            "pack_kind": "local",
            "display_name": "Test pack",
            "tier_preference": "fast",
            "description": "Imported test pack",
            "profiles": [{ "id": "default", "display_name": "Default" }],
            "models": [
              {
                "model_id": "onnx-community/silero-vad",
                "stage": "vad",
                "required": true,
                "alias": "silero-vad",
                "runtime_defaults": {
                  "cpu_safe": { "variant": "int8", "execution_provider": "cpu" }
                }
              }
            ],
            "apply": {
              "tier_preference": "fast",
              "stage_aliases": { "asr": "whisper-tiny-genai" },
              "overrides": { "asr": "genai", "translation": "auto", "tts": "kokoro" }
            },
            "olive_auto_run": false
          }
          """;

    private static (StarterPackApplyService Service, FakeStudioSettingsService Settings) CreateApplyService(
        IStarterPackCatalog catalog)
    {
        Assert.True(
            BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error),
            error);
        var settings = new FakeStudioSettingsService();
        var inventory = new FakeModelInventoryService();
        inventory.SetEntries(
        [
            CreateEntry("onnx-community/silero-vad", ModelCacheState.Ready),
        ]);
        var service = new StarterPackApplyService(
            catalog,
            settings,
            inventory,
            registry!,
            new FakeHardwareProfilerService(),
            new FakeConsentService(),
            new PermissiveCompatibilityService(),
            new PermissiveCloudReadiness());
        return (service, settings);
    }

    private static ModelInventoryEntry CreateEntry(string modelId, ModelCacheState state) =>
        new(modelId, modelId, "vad", "onnx", "MIT", true, true, state, null, DateTimeOffset.UtcNow, null);

    private sealed class FakeAppStoragePaths : IAppStoragePaths
    {
        public FakeAppStoragePaths(string root)
        {
            RootDirectory = root;
            UserDataRoot = root;
            UserCacheRoot = root;
        }

        public string RootDirectory { get; }
        public string UserDataRoot { get; }
        public string UserCacheRoot { get; }
        public string? SharedAssetRoot => null;
        public bool IsPortable => false;
        public string ModelCacheDirectory => Path.Combine(UserDataRoot, "model-cache");
        public string ModelCacheIndexPath => Path.Combine(ModelCacheDirectory, "model-cache-records.json");
        public string LogFilePath => Path.Combine(UserDataRoot, "trackdub.log");
        public string SettingsPath => Path.Combine(UserDataRoot, "settings.json");
        public string LayoutPath => Path.Combine(UserDataRoot, "avalonia-layout.json");
        public string ToolCacheDirectory => Path.Combine(UserDataRoot, "tools");
        public string FfmpegToolCacheDirectory => Path.Combine(ToolCacheDirectory, "ffmpeg");
        public string EngineCacheDirectory => Path.Combine(UserDataRoot, "EngineCache");
        public string ComponentCacheDirectory => Path.Combine(UserDataRoot, "components");
    }

    private sealed class PermissiveCompatibilityService : IStarterPackCompatibilityService
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

    private sealed class PermissiveCloudReadiness : ICloudCredentialReadiness
    {
        public Task<CloudCredentialReadinessReport> EvaluateAsync(
            StarterPackCloudDefaults cloudDefaults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudCredentialReadinessReport(true, [], null));
    }
}
