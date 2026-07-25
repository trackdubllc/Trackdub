using Trackdub.Domain;
using Trackdub.Inference.Onnx.LipSynthesis;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class LatentSyncOnnxLipSynthesisEngineTests
{
    [Fact]
    public void IsExperimentalFromManifest_resolves_latentsync_alias_and_is_commercial_when_verified()
    {
        Assert.True(BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error), error);
        Assert.NotNull(registry);

        Assert.True(registry.TryResolve(LatentSyncModelPaths.ManifestAlias, out BundledModelManifestResolution? resolution));
        Assert.NotNull(resolution);
        Assert.Equal(LatentSyncModelPaths.ModelId, resolution.Entry.ModelId);
        Assert.False(registry.TryResolve(LatentSyncModelPaths.ModelId, out _));

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
            DefaultBenchmarkEntryPath: Path.Combine(Path.GetTempPath(), "unet.onnx"),
            Variants: []);

    [Fact]
    public void SliceFrameAudioWindow_uses_frame_index_to_select_time_aligned_audio()
    {
        float[] pcm = Enumerable.Range(0, 16000).Select(sample => (float)sample).ToArray();

        float[] firstFrame = LatentSyncOnnxLipSynthesisEngine.SliceFrameAudioWindowForTest(
            pcm,
            frameIndex: 0,
            frameRate: 10,
            windowSeconds: 0.1);
        float[] laterFrame = LatentSyncOnnxLipSynthesisEngine.SliceFrameAudioWindowForTest(
            pcm,
            frameIndex: 5,
            frameRate: 10,
            windowSeconds: 0.1);

        Assert.Equal(1600, firstFrame.Length);
        Assert.Equal(1600, laterFrame.Length);
        Assert.Equal(0f, firstFrame[0]);
        Assert.Equal(8000f, laterFrame[0]);
        Assert.NotEqual(firstFrame[0], laterFrame[0]);
    }
}
