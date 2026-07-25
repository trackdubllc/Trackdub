namespace Trackdub.Contracts.ApplicationContracts;

public static class DnnlProviderIds
{
    public const string NativeOrt = "onnxruntime-dnnl";
}

public enum DnnlReadinessBlocker
{
    None = 0,
    UnsupportedRid = 1,
    OrtProviderUnavailable = 2,
    AppendFailed = 3,
    SmokeSessionProviderMismatch = 4,
    SmokeSessionFailed = 5
}

public sealed record DnnlReadinessReport(
    string ProviderId,
    DnnlReadinessBlocker Blocker,
    bool IsSupportedRid,
    bool IsOrtProviderListed,
    bool CanAppendSessionOptions,
    bool SmokeTestPassed,
    string Detail)
{
    public bool IsReady =>
        Blocker == DnnlReadinessBlocker.None &&
        IsSupportedRid &&
        IsOrtProviderListed &&
        CanAppendSessionOptions &&
        SmokeTestPassed;
}

public interface IDnnlReadinessProbe
{
    Task<DnnlReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}
