using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class GpuVariantPreferencePolicyTests
{
    private const string ValidSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    [Fact]
    public void GetPreferredGpuVariantAliases_OnBlackwellTensorRtRtx_WhenVariantHashesPinned_PrefersMxfp8BeforeBaseList()
    {
        var requirements = StageRuntimeRequirementsCatalog.All[RuntimeStage.TextRefinement];
        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 5090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Blackwell);
        var entry = CreateEntry(
            [
                new BundledModelManifestVariant(
                    "default",
                    "genai_config.json",
                    ["genai_config.json", "model.onnx"]),
                new BundledModelManifestVariant(
                    "mxfp8",
                    "mxfp8/genai_config.json",
                    ["mxfp8/genai_config.json", "mxfp8/model.onnx"],
                    SupportedProviders: ["trt-rtx"])
            ],
            downloadFileHashes: new Dictionary<string, string>
            {
                ["genai_config.json"] = ValidSha256,
                ["model.onnx"] = ValidSha256,
                ["mxfp8/genai_config.json"] = ValidSha256,
                ["mxfp8/model.onnx"] = ValidSha256
            });

        IReadOnlyList<string> aliases = GpuVariantPreferencePolicy.GetPreferredGpuVariantAliases(
            requirements,
            hardware,
            ExecutionProviderKind.TensorRTRtx,
            entry);

        Assert.Equal(["mxfp8", "default", "fp16"], aliases);
    }

    [Fact]
    public void GetPreferredGpuVariantAliases_OnBlackwellWithoutMxfp8Hashes_KeepsBaseList()
    {
        var requirements = StageRuntimeRequirementsCatalog.All[RuntimeStage.TextRefinement];
        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 5090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Blackwell);
        var entry = CreateEntry(
            new BundledModelManifestVariant("default", "genai_config.json", []),
            new BundledModelManifestVariant(
                "mxfp8",
                "mxfp8/genai_config.json",
                ["mxfp8/genai_config.json", "mxfp8/model.onnx"],
                SupportedProviders: ["trt-rtx"]));

        IReadOnlyList<string> aliases = GpuVariantPreferencePolicy.GetPreferredGpuVariantAliases(
            requirements,
            hardware,
            ExecutionProviderKind.TensorRTRtx,
            entry);

        Assert.Equal(requirements.PreferredGpuVariants, aliases);
    }

    [Fact]
    public void IsManifestVariantEligibleForPlanning_RequiresPinnedHashesOnlyForMxfp8Lane()
    {
        var entry = CreateEntry(
            [
                new BundledModelManifestVariant("default", "genai_config.json", ["genai_config.json", "model.onnx"]),
                new BundledModelManifestVariant(
                    "mxfp8",
                    "mxfp8/genai_config.json",
                    ["mxfp8/genai_config.json", "mxfp8/model.onnx"],
                    SupportedProviders: ["trt-rtx"])
            ],
            downloadFileHashes: new Dictionary<string, string>
            {
                ["genai_config.json"] = ValidSha256,
                ["model.onnx"] = ValidSha256
            });

        Assert.True(VariantManifestReadiness.IsManifestVariantEligibleForPlanning(
            entry,
            entry.Variants[0],
            ExecutionProviderKind.TensorRTRtx));
        Assert.False(VariantManifestReadiness.IsManifestVariantEligibleForPlanning(
            entry,
            entry.Variants[1],
            ExecutionProviderKind.TensorRTRtx));
    }

    [Fact]
    public void IsManifestVariantEligibleForPlanning_AllowsDefaultWithoutPerFileDownloadHashes()
    {
        var entry = CreateEntry(
            [
                new BundledModelManifestVariant(
                    "default",
                    "onnx/decoder_model.onnx",
                    ["onnx/decoder_model.onnx", "onnx/encoder_model.onnx"])
            ],
            downloadFileHashes: new Dictionary<string, string>());

        Assert.True(VariantManifestReadiness.IsManifestVariantEligibleForPlanning(
            entry,
            entry.Variants[0],
            ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void GetPreferredGpuVariantAliases_OnAdaTensorRtRtx_KeepsBaseList()
    {
        var requirements = StageRuntimeRequirementsCatalog.All[RuntimeStage.TextRefinement];
        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 4090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Ada);
        var entry = CreateEntry(
            new BundledModelManifestVariant("default", "genai_config.json", []),
            new BundledModelManifestVariant("mxfp8", "mxfp8/genai_config.json", [], SupportedProviders: ["trt-rtx"]));

        IReadOnlyList<string> aliases = GpuVariantPreferencePolicy.GetPreferredGpuVariantAliases(
            requirements,
            hardware,
            ExecutionProviderKind.TensorRTRtx,
            entry);

        Assert.Equal(requirements.PreferredGpuVariants, aliases);
    }

    [Fact]
    public void IsVariantSupportedForProvider_HonorsTrtRtxOnlyVariants()
    {
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["trt-rtx"], ExecutionProviderKind.TensorRTRtx));
        Assert.False(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["trt-rtx"], ExecutionProviderKind.DirectMl));
    }

    private static BundledModelManifestEntry CreateEntry(
        params BundledModelManifestVariant[] variants) =>
        CreateEntry(variants, downloadFileHashes: new Dictionary<string, string>());

    private static BundledModelManifestEntry CreateEntry(
        BundledModelManifestVariant[] variants,
        Dictionary<string, string> downloadFileHashes) =>
        new(
            ModelId: "example/model",
            Task: "text-refinement",
            EngineFamily: "qwen-instruct",
            Capabilities: [],
            LanguageCoverage: ModelLanguageCoverage.Empty,
            Tier: "balanced",
            Lane: ModelLane.Commercial,
            License: "Apache-2.0",
            CommercialAllowed: true,
            RedistributionAllowed: true,
            RequiresAttribution: false,
            RequiresUserConsent: false,
            VoiceCloning: false,
            CommercialUseVerified: true,
            SourceUrl: "https://example.invalid/model",
            Revision: "main",
            Sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            DownloadFiles: [],
            DownloadFileSources: new Dictionary<string, string>(),
            DownloadFileHashes: downloadFileHashes,
            Aliases: ["text-refiner"],
            RootDirectory: "/tmp/model",
            DefaultBenchmarkEntryPath: "/tmp/model/genai_config.json",
            Variants: variants);
}
