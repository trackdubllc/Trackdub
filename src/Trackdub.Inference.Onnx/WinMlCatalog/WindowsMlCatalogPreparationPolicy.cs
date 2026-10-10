namespace Trackdub.Inference.Onnx.WinMlCatalog;

/// <summary>
/// Decides whether a Windows ML catalog provider may be acquired, prepared, or registered.
/// <see cref="CatalogReadyState.NotPresent"/> is not installed.
/// <see cref="CatalogReadyState.NotReady"/> is installed and only needs to be added to the
/// process dependency graph. Those are different permissions.
/// </summary>
internal enum CatalogReadyState
{
    NotPresent,
    NotReady,
    Ready,
}

internal enum CatalogEnsureStatus
{
    Success,
    InProgress,
    Failure,
}

internal enum CatalogPreparationPhase
{
    None,
    Acquire,
    PrepareInstalled,
    Register,
}

internal readonly record struct CatalogPreparationPlan(
    CatalogPreparationPhase Phase,
    bool CallEnsureReady,
    bool StopWithoutAcquisition,
    string? StopDetail);

internal enum CatalogPreparationDisposition
{
    Register,
    Pending,
    Failed,
}

internal readonly record struct CatalogPreparationOutcome(
    CatalogPreparationDisposition Disposition,
    string Detail);

internal static class WindowsMlCatalogPreparationPolicy
{
    public static CatalogPreparationPlan Plan(CatalogReadyState state, bool allowDownloads, string providerName) =>
        state switch
        {
            CatalogReadyState.Ready => new(CatalogPreparationPhase.Register, CallEnsureReady: false, StopWithoutAcquisition: false, StopDetail: null),
            CatalogReadyState.NotReady => new(
                CatalogPreparationPhase.PrepareInstalled,
                CallEnsureReady: true,
                StopWithoutAcquisition: false,
                StopDetail: null),
            CatalogReadyState.NotPresent when allowDownloads => new(
                CatalogPreparationPhase.Acquire,
                CallEnsureReady: true,
                StopWithoutAcquisition: false,
                StopDetail: null),
            CatalogReadyState.NotPresent => new(
                CatalogPreparationPhase.None,
                CallEnsureReady: false,
                StopWithoutAcquisition: true,
                StopDetail: $"{providerName} is not installed. Enable provider downloads to acquire it."),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported catalog ready state."),
        };

    public static CatalogPreparationOutcome Classify(
        CatalogEnsureStatus status,
        CatalogPreparationPhase phase,
        string providerName,
        string? hresult,
        string? diagnosticText)
    {
        string code = string.IsNullOrWhiteSpace(hresult) ? "n/a" : hresult.Trim();
        string diagnostic = string.IsNullOrWhiteSpace(diagnosticText) ? "n/a" : diagnosticText.Trim();
        return status switch
        {
            CatalogEnsureStatus.Success => new(
                CatalogPreparationDisposition.Register,
                $"{providerName} phase {phase} completed."),
            CatalogEnsureStatus.InProgress => new(
                CatalogPreparationDisposition.Pending,
                $"{providerName} preparation is still in progress (phase {phase}). Registration was not attempted. HRESULT {code}; diagnostic: {diagnostic}."),
            CatalogEnsureStatus.Failure => new(
                CatalogPreparationDisposition.Failed,
                $"{providerName} phase {phase} failed with status {status}. HRESULT {code}; diagnostic: {diagnostic}."),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported catalog preparation status."),
        };
    }

    public static string TimeoutDetail(string providerName, CatalogPreparationPhase phase) =>
        phase == CatalogPreparationPhase.Acquire
            ? $"{providerName} catalog acquisition timed out."
            : $"{providerName} catalog preparation/registration check timed out (phase {phase}).";
}
