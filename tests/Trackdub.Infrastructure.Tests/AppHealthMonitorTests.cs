using Trackdub.Contracts.Diagnostics;
using Trackdub.Infrastructure.Diagnostics;

namespace Trackdub.Infrastructure.Tests;

public sealed class AppHealthMonitorTests
{
    [Fact]
    public void Initially_returns_empty_summary()
    {
        var monitor = new AppHealthMonitor();
        AppHealthSummary summary = monitor.GetHealthSummary();

        Assert.Empty(summary.CompletedStages);
        Assert.Empty(summary.FailedStages);
        Assert.True(summary.IsHealthy);
    }

    [Fact]
    public void Records_stage_completed()
    {
        var monitor = new AppHealthMonitor();
        monitor.RecordStageCompleted("Vad");
        monitor.RecordStageCompleted("Asr");

        AppHealthSummary summary = monitor.GetHealthSummary();
        Assert.Contains("Vad", summary.CompletedStages);
        Assert.Contains("Asr", summary.CompletedStages);
        Assert.Empty(summary.FailedStages);
        Assert.True(summary.IsHealthy);
    }

    [Fact]
    public void Records_stage_failed()
    {
        var monitor = new AppHealthMonitor();
        monitor.RecordStageFailed("Translation", FailureCategory.InferenceFailure, "Tensor shape mismatch");

        AppHealthSummary summary = monitor.GetHealthSummary();
        Assert.Empty(summary.CompletedStages);
        Assert.Single(summary.FailedStages);
        Assert.False(summary.IsHealthy);

        StageFailureRecord failure = summary.FailedStages[0];
        Assert.Equal("Translation", failure.StageName);
        Assert.Equal(FailureCategory.InferenceFailure, failure.Category);
        Assert.Equal("Tensor shape mismatch", failure.Reason);
    }

    [Fact]
    public void Records_mixed_completed_and_failed_stages()
    {
        var monitor = new AppHealthMonitor();
        monitor.RecordStageCompleted("Vad");
        monitor.RecordStageCompleted("Asr");
        monitor.RecordStageFailed("Translation", FailureCategory.ModelLoadFailure);
        monitor.RecordStageFailed("Tts", FailureCategory.InferenceFailure, "ONNX session error");

        AppHealthSummary summary = monitor.GetHealthSummary();
        Assert.Equal(2, summary.CompletedStages.Count);
        Assert.Equal(2, summary.FailedStages.Count);
        Assert.False(summary.IsHealthy);
    }

    [Fact]
    public void Trims_whitespace_from_stage_name_on_completed()
    {
        var monitor = new AppHealthMonitor();
        monitor.RecordStageCompleted("  Vad  ");

        AppHealthSummary summary = monitor.GetHealthSummary();
        Assert.Contains("Vad", summary.CompletedStages);
    }

    [Fact]
    public void Trims_whitespace_from_stage_name_on_failed()
    {
        var monitor = new AppHealthMonitor();
        monitor.RecordStageFailed("  Asr  ", FailureCategory.InferenceFailure);

        AppHealthSummary summary = monitor.GetHealthSummary();
        Assert.Equal("Asr", summary.FailedStages[0].StageName);
    }

    [Fact]
    public void Throws_on_null_or_empty_stage_name_completed()
    {
        var monitor = new AppHealthMonitor();
        Assert.Throws<ArgumentException>(() => monitor.RecordStageCompleted(""));
        Assert.Throws<ArgumentException>(() => monitor.RecordStageCompleted("  "));
    }

    [Fact]
    public void Throws_on_null_or_empty_stage_name_failed()
    {
        var monitor = new AppHealthMonitor();
        Assert.Throws<ArgumentException>(() => monitor.RecordStageFailed("", FailureCategory.UnknownError));
        Assert.Throws<ArgumentException>(() => monitor.RecordStageFailed("  ", FailureCategory.UnknownError));
    }

    [Fact]
    public void Allows_failed_reason_to_be_null()
    {
        var monitor = new AppHealthMonitor();
        monitor.RecordStageFailed("Vad", FailureCategory.ModelLoadFailure, reason: null);

        AppHealthSummary summary = monitor.GetHealthSummary();
        Assert.Null(summary.FailedStages[0].Reason);
    }
}
