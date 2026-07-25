using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.LipSynthesis;

namespace Trackdub.Composition.Tests.LipSynthesis;

/// <summary>
/// Engine-level real-model smoke for M23. Proves the mechanical chain:
/// ffmpeg frame/audio extract → pooled LatentSync ONNX sessions → patched turn clip on disk.
/// Skips unless models and fixtures are present. Never downloads.
/// </summary>
public sealed class LatentSyncOnnxLipSynthesisEngineIntegrationTests
{
    [LipSynthesisRealModelFact]
    [Trait("Category", "Integration")]
    public void IsAvailable_WhenModelsCached_ReturnsTrue()
    {
        LipSynthesisIntegrationSupport.RealLipSynthesisStack stack = LipSynthesisIntegrationSupport.CreateRealStack();
        Assert.True(stack.Engine.IsAvailable, "LatentSync files exist on disk but IsAvailable returned false.");
        Assert.True(stack.FaceDetector.IsAvailable, "SCRFD files exist on disk but IsAvailable returned false.");
        Assert.True(stack.FaceLandmarkProvider.IsAvailable, "2d106 landmark files exist but IsAvailable returned false.");
    }

    [LipSynthesisRealModelFact]
    [Trait("Category", "Integration")]
    public async Task SynthesizeTurnAsync_RealModel_ShortTurn_ReturnsSynthesizedOrHonestSkip()
    {
        string videoPath = Environment.GetEnvironmentVariable(LipSynthesisIntegrationSupport.VideoFixtureEnvVar)!;
        string audioPath = Environment.GetEnvironmentVariable(LipSynthesisIntegrationSupport.AudioFixtureEnvVar)!;
        LipSynthesisIntegrationSupport.RealLipSynthesisStack stack = LipSynthesisIntegrationSupport.CreateRealStack();

        var segmentId = Guid.NewGuid();
        string? patchedPath = null;
        try
        {
            LipSynthesisResult result = await stack.Engine.SynthesizeTurnAsync(
                new LipSynthesisRequest(
                    OriginalVideoPath: videoPath,
                    DubbedAudioPath: audioPath,
                    SegmentId: segmentId,
                    TurnStart: TimeSpan.FromSeconds(0.5),
                    TurnEnd: TimeSpan.FromSeconds(2.5),
                    SpeakerId: "spk-integration",
                    Options: new LipSynthesisOptions()),
                TestContext.Current.CancellationToken);

            Assert.True(
                result.Status is LipSynthesisEngineStatus.Synthesized or LipSynthesisEngineStatus.Skipped,
                $"Unexpected engine status {result.Status}: fail='{result.FailureReason}' skip='{result.SkipReason}'");
            if (LipSynthesisIntegrationSupport.RequiresSynthesizedOutcome())
            {
                Assert.Equal(LipSynthesisEngineStatus.Synthesized, result.Status);
            }

            Assert.NotEqual(LipSynthesisEngineStatus.Failed, result.Status);
            Assert.Equal(LipSynthesisIntegrationSupport.LatentSyncModelId, result.ModelId);
            Assert.Equal("latentsync-onnx", result.ProviderId);

            if (result.Status is LipSynthesisEngineStatus.Synthesized)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.PatchedClipPath));
                patchedPath = result.PatchedClipPath;
                Assert.True(File.Exists(patchedPath), "Engine reported Synthesized but patched clip path is missing.");
                Assert.True(new FileInfo(patchedPath).Length > 0, "Patched clip file is empty.");
            }
            else
            {
                Assert.Null(result.PatchedClipPath);
                Assert.False(string.IsNullOrWhiteSpace(result.SkipReason),
                    "Skipped engine outcome must include a structured skip reason.");
            }
        }
        finally
        {
            if (patchedPath is not null)
            {
                try { File.Delete(patchedPath); } catch { /* best-effort */ }
            }
        }
    }
}
