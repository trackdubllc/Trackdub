namespace Trackdub.Contracts.Pipeline;

public interface IVideoFrameExtractor
{
    /// <summary>
    /// Extracts video frames between the given timestamps as raw RGBA binary files.
    /// Output files are named frame_000001.rgba ... and each contains exactly
    /// <see cref="FrameExtractionResult.FrameWidth"/> × <see cref="FrameExtractionResult.FrameHeight"/> × 4 bytes.
    /// </summary>
    Task<FrameExtractionResult> ExtractTurnFramesAsync(
        string videoPath,
        double startSeconds,
        double endSeconds,
        string outputDirectory,
        CancellationToken cancellationToken);
}

public interface IVideoFrameAssembler
{
    /// <summary>
    /// Assembles raw RGBA binary frames from a directory into a video file.
    /// Frame files must be named frame_000001.rgba, frame_000002.rgba, etc.
    /// </summary>
    Task AssembleFramesAsync(
        string framesDirectory,
        string outputVideoPath,
        int width,
        int height,
        double frameRate,
        CancellationToken cancellationToken);
}

public sealed record FrameExtractionResult(
    string FramesDirectory,
    int FrameWidth,
    int FrameHeight,
    int FrameCount,
    double FrameRate);
