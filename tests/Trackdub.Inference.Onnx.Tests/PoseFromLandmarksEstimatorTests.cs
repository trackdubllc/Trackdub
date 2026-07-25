using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.FaceAnalysis;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class PoseFromLandmarksEstimatorTests
{
    [Fact]
    public async Task EstimatePoseAsync_when_eye_band_shifts_vertically_reports_pitch()
    {
        var landmarks = CreateLandmarks(eyeY: 0.85f);
        var estimator = new PoseFromLandmarksEstimator(new FakeLandmarkProvider(landmarks));

        FacePoseEstimate pose = await estimator.EstimatePoseAsync(
            new FaceAnalysisRequest("video.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(1), SpeakerId: null),
            CancellationToken.None);

        Assert.True(Math.Abs(pose.PitchDegrees) > 30d);
    }

    private static IReadOnlyList<(float X, float Y)> CreateLandmarks(float eyeY)
    {
        var points = new (float X, float Y)[106];
        for (int i = 0; i < points.Length; i++)
        {
            float y = -1f + 2f * i / (points.Length - 1);
            points[i] = (0f, y);
        }

        for (int i = 0; i <= 8; i++)
        {
            points[i] = (-0.8f, -0.2f + i * 0.05f);
        }

        for (int i = 16; i <= 24; i++)
        {
            points[i] = (0.8f, -0.2f + (i - 16) * 0.05f);
        }

        for (int i = 51; i <= 60; i++)
        {
            points[i] = (-0.25f, eyeY);
        }

        for (int i = 61; i <= 70; i++)
        {
            points[i] = (0.25f, eyeY);
        }

        return points;
    }

    private sealed class FakeLandmarkProvider(IReadOnlyList<(float X, float Y)> landmarks) : IFaceLandmarkProvider
    {
        public bool IsAvailable => true;

        public Task<FaceLandmarkResult> DetectLandmarksAsync(
            FaceAnalysisRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FaceLandmarkResult(
                LandmarksFound: true,
                IsStable: true,
                MouthOccluded: false,
                LandmarkCount: landmarks.Count,
                LandmarkPoints: landmarks));
    }
}
