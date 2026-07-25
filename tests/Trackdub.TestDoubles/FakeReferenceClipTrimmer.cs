using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeReferenceClipTrimmer : IReferenceClipTrimmer
{
    public ReferenceClipTrimResult Result { get; set; } = new(
        Trimmed: false,
        OriginalDurationSeconds: 5d,
        TrimmedDurationSeconds: 5d,
        TrimmedLeadingSeconds: 0d,
        TrimmedTrailingSeconds: 0d);

    public int TrimCallCount { get; private set; }

    public string? LastWavePath { get; private set; }

    public Task<ReferenceClipTrimResult> TrimAsync(string wavePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TrimCallCount++;
        LastWavePath = wavePath;
        return Task.FromResult(Result);
    }
}
