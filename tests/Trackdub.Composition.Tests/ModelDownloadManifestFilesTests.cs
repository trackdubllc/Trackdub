using Trackdub.Composition.Runtime;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Composition.Tests;

public sealed class ModelDownloadManifestFilesTests
{
    [Fact]
    public void ResolveRequiredFiles_without_variant_uses_default_variant_only()
    {
        BundledModelManifestEntry entry = CreateEntry(
            downloadFiles: ["tokenizer.json"],
            benchmarkEntry: "onnx/model.onnx",
            variants:
            [
                new BundledModelManifestVariant("default", "onnx/model_default.onnx", ["onnx/model_default.onnx"], IsDefault: false),
                new BundledModelManifestVariant("gpu-int4", "gpu/model.onnx", ["gpu/model.onnx", "gpu/model.onnx.data"], IsDefault: false),
            ]);

        IReadOnlyList<string> files = ModelDownloadManifestFiles.ResolveRequiredFiles(entry);

        Assert.Contains("tokenizer.json", files, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("onnx/model_default.onnx", files, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("gpu/model.onnx", files, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveRequiredFiles_with_variant_pulls_matching_variant_files()
    {
        BundledModelManifestEntry entry = CreateEntry(
            downloadFiles: ["tokenizer.json"],
            benchmarkEntry: "cpu/model.onnx",
            variants:
            [
                new BundledModelManifestVariant("default", "cpu/model.onnx", ["cpu/model.onnx"], IsDefault: true),
                new BundledModelManifestVariant("gpu-int4", "gpu/model.onnx", ["gpu/model.onnx", "gpu/model.onnx.data"], IsDefault: false),
            ]);

        IReadOnlyList<string> files = ModelDownloadManifestFiles.ResolveRequiredFiles(entry, "gpu-int4");

        Assert.Contains("tokenizer.json", files, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("gpu/model.onnx", files, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("gpu/model.onnx.data", files, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("cpu/model.onnx", files, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveRequiredFiles_with_default_alias_and_no_variants_uses_benchmark_entry()
    {
        // Models with no declared variants (e.g. the ASR engines) rely solely on the
        // benchmark entry. RuntimePlanFactory still emits a synthetic "default" variant
        // for them, so resolving the "default" alias must fall back to the benchmark
        // entry instead of throwing. Regression guard for the manifest-variant migration.
        BundledModelManifestEntry entry = CreateEntry(
            downloadFiles: ["tokenizer.json"],
            benchmarkEntry: "onnx/model.onnx",
            variants: []);

        IReadOnlyList<string> files = ModelDownloadManifestFiles.ResolveRequiredFilesForVariant(entry, "default");

        Assert.Contains("tokenizer.json", files, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("onnx/model.onnx", files, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveRequiredFiles_with_default_alias_honors_entry_path_override()
    {
        BundledModelManifestEntry entry = CreateEntry(
            downloadFiles: ["tokenizer.json"],
            benchmarkEntry: "onnx/model.onnx",
            variants: []);

        IReadOnlyList<string> files = ModelDownloadManifestFiles.ResolveRequiredFilesForVariant(
            entry, "default", "onnx/selected.onnx");

        Assert.Contains("tokenizer.json", files, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("onnx/selected.onnx", files, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("onnx/model.onnx", files, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveRequiredFiles_with_unknown_variant_throws()
    {
        BundledModelManifestEntry entry = CreateEntry(
            downloadFiles: ["tokenizer.json"],
            benchmarkEntry: "cpu/model.onnx",
            variants:
            [
                new BundledModelManifestVariant("default", "cpu/model.onnx", ["cpu/model.onnx"], IsDefault: true),
            ]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ModelDownloadManifestFiles.ResolveRequiredFiles(entry, "gpu-int4"));

        Assert.Contains("gpu-int4", exception.Message, StringComparison.Ordinal);
        Assert.Contains(entry.ModelId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveExpectedSha256_uses_entry_sha_for_anchor_and_skips_unhashed_sidecars()
    {
        BundledModelManifestEntry entry = CreateEntry(
            downloadFiles: ["tokenizer.json"],
            benchmarkEntry: "onnx/model.onnx",
            variants:
            [
                new BundledModelManifestVariant("default", "onnx/model.onnx", [], IsDefault: true),
            ],
            sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            downloadFileHashes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["onnx/model_fp16.onnx"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            });

        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ModelDownloadManifestFiles.ResolveExpectedSha256(entry, "onnx/model.onnx", "onnx/model.onnx"));
        Assert.Equal(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ModelDownloadManifestFiles.ResolveExpectedSha256(entry, "onnx/model_fp16.onnx", "onnx/model.onnx"));
        Assert.Null(ModelDownloadManifestFiles.ResolveExpectedSha256(entry, "tokenizer.json", "onnx/model.onnx"));
        Assert.Null(ModelDownloadManifestFiles.ResolveExpectedSha256(entry, "voices/af.bin", "onnx/model.onnx"));
    }

    [Fact]
    public void ResolveCacheIdentitySha256_prefers_manifest_identity_over_sidecar_digest()
    {
        BundledModelManifestEntry entry = CreateEntry(
            downloadFiles: ["tokenizer.json"],
            benchmarkEntry: "onnx/model.onnx",
            variants: [],
            sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ModelDownloadManifestFiles.ResolveCacheIdentitySha256(
                entry,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
    }

    [Fact]
    public void ResolveCacheIdentitySha256_falls_back_to_verified_digest_when_manifest_has_no_identity_hash()
    {
        BundledModelManifestEntry entry = CreateEntry(
            downloadFiles: ["tokenizer.json"],
            benchmarkEntry: "onnx/model.onnx",
            variants: [],
            sha256: "");

        Assert.Equal(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ModelDownloadManifestFiles.ResolveCacheIdentitySha256(
                entry,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.Equal(string.Empty, ModelDownloadManifestFiles.ResolveCacheIdentitySha256(entry));
    }

    private static BundledModelManifestEntry CreateEntry(
        IReadOnlyList<string> downloadFiles,
        string benchmarkEntry,
        IReadOnlyList<BundledModelManifestVariant> variants,
        string sha256 = "abc",
        IReadOnlyDictionary<string, string>? downloadFileHashes = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "manifest-files-test", Guid.NewGuid().ToString("N"));
        BundledModelManifestVariant[] resolvedVariants = variants
            .Select(variant => variant with
            {
                EntryPath = Path.GetFullPath(Path.Combine(root, variant.EntryPath)),
            })
            .ToArray();

        return new BundledModelManifestEntry(
            ModelId: "example/model",
            Task: "test",
            EngineFamily: "test",
            Capabilities: [],
            LanguageCoverage: ModelLanguageCoverage.Empty,
            Tier: "fast",
            Lane: ModelLane.Commercial,
            License: "MIT",
            CommercialAllowed: true,
            RedistributionAllowed: true,
            RequiresAttribution: false,
            RequiresUserConsent: false,
            VoiceCloning: false,
            CommercialUseVerified: true,
            SourceUrl: "https://huggingface.co/example/model",
            Revision: "main",
            Sha256: sha256,
            DownloadFiles: downloadFiles,
            DownloadFileSources: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DownloadFileHashes: downloadFileHashes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Aliases: ["example"],
            RootDirectory: root,
            DefaultBenchmarkEntryPath: Path.GetFullPath(Path.Combine(root, benchmarkEntry)),
            Variants: resolvedVariants);
    }
}
