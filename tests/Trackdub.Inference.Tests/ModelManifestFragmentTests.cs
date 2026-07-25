using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Tests;

public sealed class ModelManifestFragmentTests
{
    [Fact]
    public void LoadWithFragments_MergesGeneratedVariantsIntoExistingModelEntry()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "trackdub-manifest-fragments", Guid.NewGuid().ToString("N"));
        string manifestDirectory = Path.Combine(tempRoot, "src", "Trackdub.Inference", "Runtime", "ModelManifest");
        string modelsRoot = Path.Combine(tempRoot, "models", "whisper-tiny-genai");
        string fragmentDirectory = Path.Combine(tempRoot, "models", "manifest-fragments");
        string manifestPath = Path.Combine(manifestDirectory, "bundled-models.manifest.json");
        string fragmentPath = Path.Combine(fragmentDirectory, "trackdub-model-lab.manifest.json");

        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(modelsRoot);
        Directory.CreateDirectory(fragmentDirectory);
        File.WriteAllText(Path.Combine(modelsRoot, "encoder.onnx"), "base");
        Directory.CreateDirectory(Path.Combine(modelsRoot, "directml-fp16"));
        File.WriteAllText(Path.Combine(modelsRoot, "directml-fp16", "encoder.onnx"), "directml");

        File.WriteAllText(
            manifestPath,
            """
            {
              "models": [
                {
                  "model_id": "openai/whisper-tiny",
                  "task": "asr",
                  "engine_family": "whisper-genai",
                  "capabilities": [ "asr", "language-detection" ],
                  "language_coverage": { "source_languages": [ "auto" ] },
                  "tier": "fast",
                  "license": "Apache-2.0",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": false,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": true,
                  "source_url": "https://huggingface.co/openai/whisper-tiny",
                  "revision": "base",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "aliases": [ "whisper-tiny-genai" ],
                  "root_path": "../../../../models/whisper-tiny-genai",
                  "benchmark_entry": "encoder.onnx",
                  "variants": []
                }
              ]
            }
            """);
        File.WriteAllText(
            fragmentPath,
            """
            {
              "models": [
                {
                  "model_id": "openai/whisper-tiny",
                  "task": "asr",
                  "engine_family": "whisper-genai",
                  "capabilities": [ "asr", "language-detection" ],
                  "language_coverage": { "source_languages": [ "auto" ] },
                  "tier": "fast",
                  "license": "Apache-2.0",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": false,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": true,
                  "source_url": "https://huggingface.co/openai/whisper-tiny",
                  "revision": "model-lab",
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "aliases": [ "whisper-tiny-genai", "whisper-tiny-genai-model-lab" ],
                  "root_path": "../whisper-tiny-genai",
                  "benchmark_entry": "directml-fp16/encoder.onnx",
                  "variants": [
                    {
                      "alias": "directml-fp16",
                      "entry_path": "directml-fp16/encoder.onnx",
                      "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                      "download_files": [ "directml-fp16/genai_config.json" ]
                    }
                  ]
                }
              ]
            }
            """);

        try
        {
            BundledModelManifestRegistry registry = BundledModelManifestRegistry.LoadWithFragments(manifestPath, fragmentDirectory);

            Assert.True(registry.TryResolve("whisper-tiny-genai@directml-fp16", out BundledModelManifestResolution? resolution));
            Assert.NotNull(resolution);
            Assert.Equal("directml-fp16", resolution!.VariantAlias);
            Assert.EndsWith(Path.Combine("models", "whisper-tiny-genai", "directml-fp16", "encoder.onnx"), resolution.EntryPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(resolution.Entry.Aliases, alias => alias.Equals("whisper-tiny-genai-model-lab", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_AllowsSameModelIdForDistinctModelRoots()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "trackdub-manifest-roots", Guid.NewGuid().ToString("N"));
        string manifestDirectory = Path.Combine(tempRoot, "src", "Trackdub.Inference", "Runtime", "ModelManifest");
        string firstModelRoot = Path.Combine(tempRoot, "models", "whisper-tiny");
        string secondModelRoot = Path.Combine(tempRoot, "models", "whisper-tiny-onnx");
        string manifestPath = Path.Combine(manifestDirectory, "bundled-models.manifest.json");

        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(firstModelRoot);
        Directory.CreateDirectory(secondModelRoot);

        File.WriteAllText(
            manifestPath,
            """
            {
              "models": [
                {
                  "model_id": "onnx-community/whisper-tiny",
                  "task": "asr",
                  "engine_family": "whisper-onnx",
                  "capabilities": [ "asr" ],
                  "language_coverage": { "source_languages": [ "auto" ] },
                  "tier": "fast",
                  "license": "Apache-2.0",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": false,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": true,
                  "source_url": "https://huggingface.co/onnx-community/whisper-tiny",
                  "revision": "base",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "aliases": [ "whisper-tiny" ],
                  "root_path": "../../../../models/whisper-tiny",
                  "benchmark_entry": "onnx/encoder_model.onnx",
                  "variants": [
                    { "alias": "default", "entry_path": "onnx/encoder_model.onnx" }
                  ]
                },
                {
                  "model_id": "onnx-community/whisper-tiny",
                  "task": "asr",
                  "engine_family": "whisper-onnx",
                  "capabilities": [ "asr" ],
                  "language_coverage": { "source_languages": [ "auto" ] },
                  "tier": "fast",
                  "license": "Apache-2.0",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": false,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": true,
                  "source_url": "https://huggingface.co/onnx-community/whisper-tiny",
                  "revision": "base",
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "aliases": [ "whisper-tiny-onnx" ],
                  "root_path": "../../../../models/whisper-tiny-onnx",
                  "benchmark_entry": "onnx/encoder_model.onnx",
                  "variants": [
                    { "alias": "default", "entry_path": "onnx/encoder_model.onnx" }
                  ]
                }
              ]
            }
            """);

        try
        {
            BundledModelManifestRegistry registry = BundledModelManifestRegistry.Load(manifestPath);

            Assert.True(registry.TryResolve("whisper-tiny", out BundledModelManifestResolution? firstResolution));
            Assert.True(registry.TryResolve("whisper-tiny-onnx", out BundledModelManifestResolution? secondResolution));
            Assert.NotNull(firstResolution);
            Assert.NotNull(secondResolution);
            Assert.Equal("whisper-tiny", firstResolution!.Alias);
            Assert.Equal("whisper-tiny-onnx", secondResolution!.Alias);
            Assert.EndsWith(Path.Combine("models", "whisper-tiny"), firstResolution.Entry.RootDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("models", "whisper-tiny-onnx"), secondResolution.Entry.RootDirectory, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
