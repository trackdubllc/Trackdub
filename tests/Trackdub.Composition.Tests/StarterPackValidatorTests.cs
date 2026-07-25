using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackValidatorTests
{
    [Fact]
    public void Validate_rejects_kokoro_directml_starter_pack_default()
    {
        StarterPackValidator validator = new();
        BundledModelManifestRegistry registry = LoadDefaultRegistry();
        StarterPackDefinition pack = CreatePack(
            variant: "fp16",
            executionProvider: "directml");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => validator.Validate(pack, registry));

        Assert.Contains("does not support execution_provider 'directml'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_unknown_provider_token()
    {
        StarterPackValidator validator = new();
        BundledModelManifestRegistry registry = LoadDefaultRegistry();
        StarterPackDefinition pack = CreatePack(
            variant: "fp16",
            executionProvider: "warp-drive");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => validator.Validate(pack, registry));

        Assert.Contains("unknown execution_provider 'warp-drive'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_accepts_implicit_default_variant_when_manifest_uses_benchmark_entry_default()
    {
        StarterPackValidator validator = new();
        BundledModelManifestRegistry registry = LoadDefaultRegistry();
        StarterPackDefinition pack = new(
            SchemaVersion: 1,
            Id: "test-silero-pack",
            DisplayName: "Test Silero Pack",
            TierPreference: "balanced",
            Description: "Test pack.",
            Profiles:
            [
                new StarterPackProfileDefinition("default", "Default", "onnx-community/whisper-tiny")
            ],
            Models:
            [
                new StarterPackModelDefinition(
                    "onnx-community/silero-vad",
                    "vad",
                    Required: true,
                    Alias: "silero-vad",
                    RuntimeDefaults: new Dictionary<string, StarterPackRuntimeDefaults>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["cpu_safe"] = new("default", "cpu")
                    }),
                new StarterPackModelDefinition(
                    "onnx-community/whisper-tiny",
                    "asr",
                    Required: false,
                    Alias: "whisper-tiny",
                    RuntimeDefaults: new Dictionary<string, StarterPackRuntimeDefaults>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["cpu_safe"] = new("default", "cpu")
                    })
            ]);

        validator.Validate(pack, registry);
    }

    [Fact]
    public async Task Catalog_validates_bundled_packs_when_manifest_registry_is_supplied()
    {
        var catalog = new StarterPackCatalog(
            storagePaths: null,
            validator: new StarterPackValidator(),
            manifestRegistry: LoadDefaultRegistry());

        IReadOnlyList<StarterPackDefinition> packs = await catalog.ListDefinitionsAsync();

        Assert.Contains(packs, pack => pack.Id == "basic");
        Assert.Contains(packs, pack => pack.Id == "balanced");
    }

    private static StarterPackDefinition CreatePack(string variant, string executionProvider) =>
        new(
            SchemaVersion: 1,
            Id: "test-kokoro-pack",
            DisplayName: "Test Kokoro Pack",
            TierPreference: "balanced",
            Description: "Test pack.",
            Profiles:
            [
                new StarterPackProfileDefinition(
                    "default",
                    "Default",
                    "onnx-community/Kokoro-82M-v1.0-ONNX")
            ],
            Models:
            [
                new StarterPackModelDefinition(
                    "onnx-community/Kokoro-82M-v1.0-ONNX",
                    "tts",
                    Required: true,
                    Alias: "kokoro-onnx",
                    RuntimeDefaults: new Dictionary<string, StarterPackRuntimeDefaults>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["balanced_gpu"] = new(variant, executionProvider)
                    })
            ]);

    private static BundledModelManifestRegistry LoadDefaultRegistry()
    {
        Assert.True(
            BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error),
            error);
        return registry!;
    }
}
