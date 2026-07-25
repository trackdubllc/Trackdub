using Trackdub.Application.Transcripts;
using Trackdub.Domain.Transcript;

namespace Trackdub.Application.Tests;

public sealed class DurationAnalysisServiceTests
{
    [Fact]
    public void Analyze_calculates_overrun_ratio()
    {
        var service = new DurationAnalysisService();
        TranscriptSegment segment = TranscriptSegment.Create(
            Guid.NewGuid(),
            segmentIndex: 0,
            startSeconds: 10.0d,
            endSeconds: 12.0d,
            text: "Hello");

        DurationAnalysisResult result = service.Analyze(segment, 2.3d);

        Assert.Equal(2.0d, result.OriginalDurationSeconds);
        Assert.Equal(0.15d, result.OverrunRatio!.Value, precision: 6);
        Assert.Equal(TtsDurationSeverity.Yellow, result.Severity);
        Assert.True(result.AutoStretchEligible);
    }

    [Fact]
    public void Analyze_flags_large_overrun_without_auto_stretch()
    {
        var service = new DurationAnalysisService();
        TranscriptSegment segment = TranscriptSegment.Create(
            Guid.NewGuid(),
            segmentIndex: 0,
            startSeconds: 0.0d,
            endSeconds: 2.0d,
            text: "Hello");

        DurationAnalysisResult result = service.Analyze(segment, 2.5d);

        Assert.Equal(0.25d, result.OverrunRatio!.Value, precision: 6);
        Assert.False(result.AutoStretchEligible);
        Assert.Equal(TtsDurationSeverity.Yellow, result.Severity);
    }

    [Fact]
    public void Analyze_flags_speed_limit_warning()
    {
        var service = new DurationAnalysisService();
        TranscriptSegment segment = TranscriptSegment.Create(
            Guid.NewGuid(),
            segmentIndex: 0,
            startSeconds: 0.0d,
            endSeconds: 2.0d,
            text: "Hello");

        DurationAnalysisResult result = service.Analyze(segment, 3.2d);

        Assert.True(result.HasSpeedLimitWarning);
        Assert.Equal(TtsDurationSeverity.Red, result.Severity);
    }
}
