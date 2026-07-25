using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.ExecutionProviders;

/// <summary>
/// Result of an execution provider bootstrap attempt.
/// Contains the selected provider, success status, and diagnostic details.
/// </summary>
public sealed record ExecutionProviderBootstrapResult(
    ExecutionProviderKind RequestedProvider,
    ExecutionProviderKind SelectedProvider,
    bool Succeeded,
    string Detail,
    string? FailureReason = null)
{
    /// <summary>
    /// True if the requested and selected providers match (no fallback occurred).
    /// </summary>
    public bool IsRequestFulfilled => RequestedProvider == SelectedProvider;

    /// <summary>
    /// Indicates whether fallback to CPU occurred.
    /// </summary>
    public bool FallbackToCpu => SelectedProvider == ExecutionProviderKind.Cpu && RequestedProvider != ExecutionProviderKind.Cpu;
}
