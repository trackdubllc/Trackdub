namespace Trackdub.Infrastructure.Components.NvidiaAfx;

/// <summary>
/// Honest packaging gates for AFX redistributable packages. Placeholder URLs / zero hashes
/// must never be treated as downloadable shipping packages.
/// </summary>
public static class NvidiaAfxRuntimePackageGates
{
    public const string PlaceholderHostMarker = "example.invalid";

    public static bool IsPlaceholderSha256(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return true;
        }

        string trimmed = sha256.Trim();
        return trimmed.Length != 64 || trimmed.All(ch => ch == '0');
    }

    public static bool IsPlaceholderDownloadUrl(string? downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return true;
        }

        if (!Uri.TryCreate(downloadUrl.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.Host.Contains(PlaceholderHostMarker, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDownloadable(NvidiaAfxRuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return !IsPlaceholderDownloadUrl(package.DownloadUrl)
            && !IsPlaceholderSha256(package.Sha256)
            && package.SizeBytes > 0
            && !string.IsNullOrWhiteSpace(package.RuntimeVersion)
            && !string.IsNullOrWhiteSpace(package.LicenseUrl);
    }

    public static string DescribeBlocker(NvidiaAfxRuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (IsPlaceholderDownloadUrl(package.DownloadUrl))
        {
            return "AFX package download URL is missing or still a placeholder (example.invalid / non-HTTPS).";
        }

        if (IsPlaceholderSha256(package.Sha256))
        {
            return "AFX package SHA-256 is missing or still an all-zero placeholder.";
        }

        if (package.SizeBytes <= 0)
        {
            return "AFX package SizeBytes must be a positive verified archive size.";
        }

        if (string.IsNullOrWhiteSpace(package.LicenseUrl))
        {
            return "AFX package LicenseUrl is required before download.";
        }

        return "AFX package is not downloadable.";
    }

    public static bool ManifestHasAnyDownloadablePackage(NvidiaAfxRuntimeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Packages.Any(IsDownloadable);
    }
}
