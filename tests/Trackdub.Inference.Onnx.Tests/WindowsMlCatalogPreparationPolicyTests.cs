using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Onnx.WinMlCatalog;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class WindowsMlCatalogPreparationPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotReady_prepares_installed_package_without_acquiring(bool allowDownloads)
    {
        CatalogPreparationPlan plan = WindowsMlCatalogPreparationPolicy.Plan(
            CatalogReadyState.NotReady,
            allowDownloads,
            "MIGraphXExecutionProvider");

        Assert.Equal(CatalogPreparationPhase.PrepareInstalled, plan.Phase);
        Assert.True(plan.CallEnsureReady);
        Assert.False(plan.StopWithoutAcquisition);
    }

    [Fact]
    public void NotPresent_without_downloads_does_not_acquire()
    {
        CatalogPreparationPlan plan = WindowsMlCatalogPreparationPolicy.Plan(
            CatalogReadyState.NotPresent,
            allowDownloads: false,
            "QNNExecutionProvider");

        Assert.True(plan.StopWithoutAcquisition);
        Assert.False(plan.CallEnsureReady);
        Assert.Contains("not installed", plan.StopDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("not ready", plan.StopDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotPresent_with_downloads_acquires()
    {
        CatalogPreparationPlan plan = WindowsMlCatalogPreparationPolicy.Plan(
            CatalogReadyState.NotPresent,
            allowDownloads: true,
            "QNNExecutionProvider");

        Assert.Equal(CatalogPreparationPhase.Acquire, plan.Phase);
        Assert.True(plan.CallEnsureReady);
        Assert.False(plan.StopWithoutAcquisition);
    }

    [Fact]
    public void Ready_registers_without_ensure()
    {
        CatalogPreparationPlan plan = WindowsMlCatalogPreparationPolicy.Plan(
            CatalogReadyState.Ready,
            allowDownloads: false,
            "OpenVINOExecutionProvider");

        Assert.Equal(CatalogPreparationPhase.Register, plan.Phase);
        Assert.False(plan.CallEnsureReady);
    }

    [Fact]
    public void InProgress_is_pending_and_keeps_diagnostics()
    {
        CatalogPreparationOutcome outcome = WindowsMlCatalogPreparationPolicy.Classify(
            CatalogEnsureStatus.InProgress,
            CatalogPreparationPhase.PrepareInstalled,
            "MIGraphXExecutionProvider",
            "0x800704C7",
            "still copying");

        Assert.Equal(CatalogPreparationDisposition.Pending, outcome.Disposition);
        Assert.Contains("in progress", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("0x800704C7", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("still copying", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failure_keeps_hresult_and_diagnostic()
    {
        CatalogPreparationOutcome outcome = WindowsMlCatalogPreparationPolicy.Classify(
            CatalogEnsureStatus.Failure,
            CatalogPreparationPhase.Acquire,
            "QNNExecutionProvider",
            "0x80070005",
            "access denied");

        Assert.Equal(CatalogPreparationDisposition.Failed, outcome.Disposition);
        Assert.Contains("HRESULT 0x80070005", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("access denied", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("Acquire", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_timeout_is_not_described_as_a_download()
    {
        string detail = WindowsMlCatalogPreparationPolicy.TimeoutDetail(
            "MIGraphXExecutionProvider",
            CatalogPreparationPhase.PrepareInstalled);

        Assert.Contains("preparation/registration check", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("acquisition", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiled_model_reuse_requires_optimal_compatibility()
    {
        Assert.True(EpContextCompatibility.ShouldReuse(OrtCompiledModelCompatibility.EP_SUPPORTED_OPTIMAL));
        Assert.False(EpContextCompatibility.ShouldReuse(OrtCompiledModelCompatibility.EP_SUPPORTED_PREFER_RECOMPILATION));
        Assert.False(EpContextCompatibility.ShouldReuse(OrtCompiledModelCompatibility.EP_UNSUPPORTED));
        Assert.False(EpContextCompatibility.ShouldReuse(OrtCompiledModelCompatibility.EP_NOT_APPLICABLE));
        Assert.False(EpContextCompatibility.IsSelectedDeviceGroup([], "NvTensorRTRTXExecutionProvider"));
    }
}
