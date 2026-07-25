namespace Trackdub.Infrastructure.Components.NvidiaAfx;

public sealed record NvidiaAfxRuntimePackage(
    string Architecture,
    string DownloadUrl,
    string Sha256,
    long SizeBytes,
    string RuntimeVersion,
    string LicenseUrl,
    string[] ModelRelativePaths);

public sealed record NvidiaAfxRuntimeManifest(
    string ManifestVersion,
    NvidiaAfxRuntimePackage[] Packages);
