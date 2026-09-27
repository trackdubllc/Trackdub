using Trackdub.Domain;
using Trackdub.Inference.Onnx.LipSynthesis;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class LatentSyncOnnxLipSynthesisEngineTests
{
    [Fact]
    public void Repair_pipeline_remains_unavailable_until_temporal_and_face_validation()
    {
        Assert.False(LatentSyncOnnxLipSynthesisEngine.HasValidatedRepairPipeline());
    }

    [Fact]
    public void IsExperimentalFromManifest_resolves_latentsync_alias_and_is_commercial_when_verified()
    {
        Assert.True(BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error), error);
        Assert.NotNull(registry);

        Assert.True(registry.TryResolve(LatentSyncModelPaths.ManifestAlias, out BundledModelManifestResolution? resolution));
        Assert.NotNull(resolution);
        Assert.Equal(LatentSyncModelPaths.ModelId, resolution.Entry.ModelId);
        Assert.True(registry.TryResolve(LatentSyncModelPaths.ModelId, out _));

        Assert.False(LatentSyncOnnxLipSynthesisEngine.IsExperimentalFromManifest(registry));
    }

    [Fact]
    public void IsExperimentalFromManifest_returns_false_only_when_commercial_lane_and_flags_are_true()
    {
        BundledModelManifestEntry entry = CreateLatentSyncManifestEntry(
            lane: ModelLane.Commercial,
            commercialAllowed: true,
            commercialUseVerified: true);

        Assert.False(LatentSyncOnnxLipSynthesisEngine.IsExperimentalFromEntry(entry));
    }

    [Fact]
    public void IsExperimentalFromEntry_stays_true_when_commercial_allowed_is_false()
    {
        BundledModelManifestEntry entry = CreateLatentSyncManifestEntry(
            lane: ModelLane.Commercial,
            commercialAllowed: false,
            commercialUseVerified: true);

        Assert.True(LatentSyncOnnxLipSynthesisEngine.IsExperimentalFromEntry(entry));
    }

    private static BundledModelManifestEntry CreateLatentSyncManifestEntry(
        ModelLane lane,
        bool commercialAllowed,
        bool commercialUseVerified) =>
        new(
            ModelId: LatentSyncModelPaths.ModelId,
            Task: "lip-synthesis",
            EngineFamily: LatentSyncModelPaths.EngineFamily,
            Capabilities: [],
            LanguageCoverage: ModelLanguageCoverage.Empty,
            Tier: "quality",
            Lane: lane,
            License: "openrail++",
            CommercialAllowed: commercialAllowed,
            RedistributionAllowed: true,
            RequiresAttribution: true,
            RequiresUserConsent: false,
            VoiceCloning: false,
            CommercialUseVerified: commercialUseVerified,
            SourceUrl: "https://example.com",
            Revision: "main",
            Sha256: string.Empty,
            DownloadFiles: [],
            DownloadFileSources: new Dictionary<string, string>(),
            DownloadFileHashes: new Dictionary<string, string>(),
            Aliases: [LatentSyncModelPaths.ManifestAlias],
            RootDirectory: Path.GetTempPath(),
            DefaultBenchmarkEntryPath: Path.Join(Path.GetTempPath(), "unet.onnx"),
            Variants: []);

    [Fact]
    public void SliceWhisperContext_centers_ten_feature_steps_on_frame_time()
    {
        const int hiddenDimension = 1;
        float[] embeddings = Enumerable.Range(0, 1500).Select(index => (float)index).ToArray();

        float[] context = LatentSyncOnnxLipSynthesisEngine.SliceWhisperContextForTest(
            embeddings, sequenceLength: 1500, hiddenDimension: hiddenDimension, frameIndex: 10, frameRate: 25,
            framesBefore: 2, framesAfter: 2);

        Assert.Equal(10, context.Length);
        Assert.Equal(Enumerable.Range(16, 10).Select(index => (float)index), context);
    }

    [Fact]
    public void SliceWhisperContext_repeats_edge_features_to_keep_a_full_context()
    {
        float[] context = LatentSyncOnnxLipSynthesisEngine.SliceWhisperContextForTest(
            [10f, 20f, 30f], sequenceLength: 3, hiddenDimension: 1, frameIndex: 0, frameRate: 25,
            framesBefore: 2, framesAfter: 2);

        Assert.Equal(new[] { 10f, 10f, 10f, 10f, 10f, 20f, 30f, 30f, 30f, 30f }, context);
    }

    [Fact]
    public void SliceWhisperContext_rejects_invalid_feature_shapes_and_frame_rates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LatentSyncOnnxLipSynthesisEngine.SliceWhisperContextForTest(
                [1f], sequenceLength: 2, hiddenDimension: 1, frameIndex: 0, frameRate: 25,
                framesBefore: 2, framesAfter: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LatentSyncOnnxLipSynthesisEngine.SliceWhisperContextForTest(
                [1f], sequenceLength: 1, hiddenDimension: 1, frameIndex: 0, frameRate: 0,
                framesBefore: 2, framesAfter: 2));
    }
}
