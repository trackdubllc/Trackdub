using Trackdub.Contracts;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.NvidiaAfx;

/// <summary>
/// Installs a verified AFX runtime package after license acceptance. Refuses while stubbed
/// and refuses placeholder packaging metadata.
/// </summary>
public sealed class NvidiaAfxRuntimeInstaller(
    NvidiaAfxRuntimeDownloader downloader,
    IStudioSettingsService settingsService,
    INvidiaAfxArchitectureDetector architectureDetector,
    string manifestPath)
{
    public async Task<NvidiaAfxRuntimeInstallResult> EnsureInstalledAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (NvidiaAfxIntegration.IsStubbed())
        {
            return NvidiaAfxRuntimeInstallResult.Fail(NvidiaAfxIntegration.StubReason);
        }

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.NvidiaAfxLicenseAccepted)
        {
            return NvidiaAfxRuntimeInstallResult.Fail(
                "NVIDIA AFX license has not been accepted. Review the Maxine AFX license and accept it in settings before install.");
        }

        NvidiaAfxRuntimeManifest manifest;
        try
        {
            manifest = NvidiaAfxRuntimeManifestLoader.Load(manifestPath);
        }
        catch (Exception ex)
        {
            return NvidiaAfxRuntimeInstallResult.Fail($"AFX runtime manifest error: {ex.Message}");
        }

        if (!NvidiaAfxRuntimePackageGates.ManifestHasAnyDownloadablePackage(manifest))
        {
            return NvidiaAfxRuntimeInstallResult.Fail(
                "No downloadable AFX redistributable packages are configured. " +
                "Trackdub-hosted URLs/checksums are required, or use a local Maxine AFX install via " +
                "NvidiaAfxRuntimeDirectory / TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT.");
        }

        string architecture = architectureDetector.DetectArchitectureBucket();
        NvidiaAfxRuntimePackage? package = manifest.Packages
            .FirstOrDefault(candidate => string.Equals(candidate.Architecture, architecture, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            return NvidiaAfxRuntimeInstallResult.Fail(
                $"No AFX runtime package is available for architecture bucket '{architecture}'.");
        }

        if (!NvidiaAfxRuntimePackageGates.IsDownloadable(package))
        {
            return NvidiaAfxRuntimeInstallResult.Fail(NvidiaAfxRuntimePackageGates.DescribeBlocker(package));
        }

        try
        {
            string installPath = await downloader
                .DownloadAndInstallAsync(package, progress, cancellationToken)
                .ConfigureAwait(false);
            return NvidiaAfxRuntimeInstallResult.Ok(installPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NvidiaAfxRuntimeInstallResult.Fail($"AFX runtime download failed: {ex.Message}");
        }
    }
}

public sealed record NvidiaAfxRuntimeInstallResult(
    bool Succeeded,
    string? InstallDirectory,
    string? FailureDetail)
{
    public static NvidiaAfxRuntimeInstallResult Ok(string installDirectory) =>
        new(true, installDirectory, null);

    public static NvidiaAfxRuntimeInstallResult Fail(string detail) =>
        new(false, null, detail);
}
