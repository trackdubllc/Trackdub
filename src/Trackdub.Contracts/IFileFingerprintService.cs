namespace Trackdub.Contracts;

public interface IFileFingerprintService
{
    Task<FileFingerprint> ComputeAsync(string path, CancellationToken cancellationToken);
}

public sealed record FileFingerprint(
    string Sha256,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc);
