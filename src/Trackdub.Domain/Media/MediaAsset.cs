namespace Trackdub.Domain.Media;

public sealed record MediaAsset(
    Guid Id,
    Guid ProjectId,
    string SourceFilePath,
    string SourceFileName,
    string FingerprintSha256,
    long SourceSizeBytes,
    DateTimeOffset SourceLastWriteTimeUtc,
    string FormatName,
    double DurationSeconds,
    bool HasAudio,
    bool HasVideo,
    DateTimeOffset CreatedAtUtc);
