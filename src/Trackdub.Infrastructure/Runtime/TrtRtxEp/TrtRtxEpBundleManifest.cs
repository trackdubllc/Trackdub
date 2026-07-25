namespace Trackdub.Infrastructure.Runtime.TrtRtxEp;

public sealed record TrtRtxEpBundlePackage(
    string ArchiveUrl,
    string ArchiveKind,
    string Sha256,
    long SizeBytes);

public sealed record TrtRtxEpBundleManifest(
    int SchemaVersion,
    string Version,
    string CudaVariant,
    string LicenseUrl,
    Dictionary<string, TrtRtxEpBundlePackage> Packages);
