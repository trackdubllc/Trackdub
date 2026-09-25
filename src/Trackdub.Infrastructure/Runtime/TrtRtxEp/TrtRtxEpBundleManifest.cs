namespace Trackdub.Infrastructure.Runtime.TrtRtxEp;

/// <param name="Version">
/// EP ABI release this package comes from when it differs from the manifest-level version
/// (NVIDIA does not publish every platform on every tag); <see langword="null"/> inherits it.
/// </param>
public sealed record TrtRtxEpBundlePackage(
    string ArchiveUrl,
    string ArchiveKind,
    string Sha256,
    long SizeBytes,
    string? Version = null);

public sealed record TrtRtxEpBundleManifest(
    int SchemaVersion,
    string Version,
    string CudaVariant,
    string LicenseUrl,
    Dictionary<string, TrtRtxEpBundlePackage> Packages)
{
    public string ResolveVersion(string runtimeIdentifier) =>
        Packages.TryGetValue(runtimeIdentifier, out TrtRtxEpBundlePackage? package) &&
        !string.IsNullOrWhiteSpace(package.Version)
            ? package.Version
            : Version;
}
