using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Onnx.FaceAnalysis;

/// <summary>
/// Pure-math head pose estimator from InsightFace 2D106 facial landmarks (no extra ONNX model).
/// Returns IsAvailable=false until <see cref="GeometryLandmarkProvider"/> is available,
/// since pose estimation is meaningless without landmarks to compute from.
/// </summary>
public sealed class PoseFromLandmarksEstimator(IFaceLandmarkProvider landmarkProvider)
    : IFacePoseEstimator
{
    // InsightFace 2D106 landmark index ranges
    private const int LeftEyeStart = 51;
    private const int LeftEyeEnd = 60;
    private const int RightEyeStart = 61;
    private const int RightEyeEnd = 70;
    // Face contour: left side 0-8, right side 16-24 (midpoint ~12)
    private const int ContourLeftStart = 0;
    private const int ContourLeftEnd = 8;
    private const int ContourRightStart = 16;
    private const int ContourRightEnd = 24;

    public bool IsAvailable => landmarkProvider.IsAvailable;

    public async Task<FacePoseEstimate> EstimatePoseAsync(
        FaceAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        FaceLandmarkResult result = await landmarkProvider
            .DetectLandmarksAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!result.LandmarksFound || result.LandmarkPoints is null
            || result.LandmarkPoints.Count < 106)
            return new FacePoseEstimate(YawDegrees: 0d, PitchDegrees: 0d, RollDegrees: 0d);

        IReadOnlyList<(float X, float Y)> pts = result.LandmarkPoints;

        // Roll: angle between left-eye center and right-eye center.
        (float lx, float ly) = EyeCenter(pts, LeftEyeStart, LeftEyeEnd);
        (float rx, float ry) = EyeCenter(pts, RightEyeStart, RightEyeEnd);
        double rollDeg = Math.Atan2(ry - ly, rx - lx) * (180.0 / Math.PI);

        // Yaw: signed deviation of eye-midpoint from face-contour midpoint.
        // Positive = face turned right; negative = face turned left.
        float leftMeanX = MeanX(pts, ContourLeftStart, ContourLeftEnd);
        float rightMeanX = MeanX(pts, ContourRightStart, ContourRightEnd);
        float faceWidth = rightMeanX - leftMeanX;
        double yawDeg = 0d;
        if (faceWidth > 0.01f)
        {
            float eyeMidX = (lx + rx) / 2f;
            float contourMid = leftMeanX + faceWidth / 2f;
            // Scale deviation to ±45° assuming linear relationship.
            yawDeg = (eyeMidX - contourMid) / (faceWidth / 2f) * 45.0;
        }

        // Pitch: rough estimate from vertical eye position relative to face-contour Y center.
        // In 2D106 normalized [-1,1] space, Y increases downward. When the head tilts up the
        // eyes shift toward the top of the crop (smaller Y) relative to the face contour center.
        // Accuracy is limited by 2D-only data; a dedicated 3D pose model would be more reliable.
        float eyeMidY = (ly + ry) / 2f;
        float leftMeanY = MeanY(pts, ContourLeftStart, ContourLeftEnd);
        float rightMeanY = MeanY(pts, ContourRightStart, ContourRightEnd);
        float contourMidY = (leftMeanY + rightMeanY) / 2f;
        double pitchDeg = 0d;
        if (faceWidth > 0.01f)
        {
            // Use faceWidth as the shared scale reference (≈ face height for frontal faces).
            // Negate so that eyes above contour center (eyeMidY < contourMidY) → positive pitch (head up).
            pitchDeg = -(eyeMidY - contourMidY) / (faceWidth / 2f) * 30.0;
        }

        return new FacePoseEstimate(
            YawDegrees: yawDeg,
            PitchDegrees: pitchDeg,
            RollDegrees: rollDeg);
    }

    private static (float X, float Y) EyeCenter(
        IReadOnlyList<(float X, float Y)> pts, int start, int end)
    {
        float x = 0f, y = 0f;
        for (int i = start; i <= end; i++) { x += pts[i].X; y += pts[i].Y; }
        int n = end - start + 1;
        return (x / n, y / n);
    }

    private static float MeanX(IReadOnlyList<(float X, float Y)> pts, int start, int end)
    {
        float x = 0f;
        for (int i = start; i <= end; i++) x += pts[i].X;
        return x / (end - start + 1);
    }

    private static float MeanY(IReadOnlyList<(float X, float Y)> pts, int start, int end)
    {
        float y = 0f;
        for (int i = start; i <= end; i++) y += pts[i].Y;
        return y / (end - start + 1);
    }
}
