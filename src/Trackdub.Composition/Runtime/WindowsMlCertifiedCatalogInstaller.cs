using Trackdub.Application.Runtime;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif

namespace Trackdub.Composition.Runtime;

/// <summary>
/// Delegates bulk certified-catalog registration to
/// <see cref="WindowsMlProviderRegistrationPolicy.EnsureAllCertifiedCatalogAsync"/>.
/// </summary>
internal sealed class WindowsMlCertifiedCatalogInstaller : IWindowsMlCertifiedCatalogInstaller
{
#if WINDOWS
    public async Task<WindowsMlCertifiedCatalogInstallResult> EnsureAllCertifiedAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        progress.Report("Contacting Windows ML catalog to ensure all certified providers...");

        WindowsMlProviderRegistrationResult result =
            await WindowsMlProviderRegistrationPolicy.Shared
                .EnsureAllCertifiedCatalogAsync(cancellationToken)
                .ConfigureAwait(false);

        string detail = string.IsNullOrWhiteSpace(result.Detail)
            ? "Windows ML catalog ensure-and-register completed."
            : result.Detail;

        if (result.RegistrationSucceeded)
        {
            progress.Report(detail);
            return new WindowsMlCertifiedCatalogInstallResult(Succeeded: true, Detail: detail);
        }

        progress.Report($"Catalog ensure-and-register failed: {detail}");
        return new WindowsMlCertifiedCatalogInstallResult(
            Succeeded: false,
            Detail: detail,
            FailureDetail: detail);
    }
#else
    public Task<WindowsMlCertifiedCatalogInstallResult> EnsureAllCertifiedAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();

        const string failureDetail =
            "Install all certified providers is only available in the Windows-specific build.";
        progress.Report(failureDetail);
        return Task.FromResult(
            new WindowsMlCertifiedCatalogInstallResult(
                Succeeded: false,
                Detail: failureDetail,
                FailureDetail: failureDetail));
    }
#endif
}
