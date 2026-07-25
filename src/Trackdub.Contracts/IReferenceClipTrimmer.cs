namespace Trackdub.Contracts;

public interface IReferenceClipTrimmer
{
    Task<ReferenceClipTrimResult> TrimAsync(string wavePath, CancellationToken cancellationToken);
}

public sealed record ReferenceClipTrimResult(
    bool Trimmed,
    double OriginalDurationSeconds,
    double TrimmedDurationSeconds,
    double TrimmedLeadingSeconds,
    double TrimmedTrailingSeconds);
