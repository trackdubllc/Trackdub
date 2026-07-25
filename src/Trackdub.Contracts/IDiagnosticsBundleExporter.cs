using Trackdub.Contracts.Pipeline;

namespace Trackdub.Contracts;

public interface IDiagnosticsBundleExporter
{
    Task ExportBundleAsync(DiagnosticsBundleExportRequest request, CancellationToken cancellationToken = default);
}

public sealed record DiagnosticsBundleExportRequest(
    string DestinationZipPath,
    string? ProjectRootPath = null,
    string? MediaPath = null,
    Trackdub.Contracts.Diagnostics.FailureCategory? FailureCategory = null,
    string? FailureExplanation = null,
    string? FailureContext = null,
    TransientFaultSummary? Transient = null);
