using Trackdub.Application.Runtime;
using Trackdub.Domain;

namespace Trackdub.Composition.Runtime;

internal sealed class OpenVinoCatalogEpInstaller : IOpenVinoCatalogEpInstaller
{
#if WINDOWS
    public async Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return await WinMlCatalogEpInstallerCore.EnsureCoreAsync(
            ExecutionProviderKind.OpenVinoCatalog,
            "OpenVINO (WinML catalog)",
            progress,
            cancellationToken).ConfigureAwait(false);
    }
#else
    public Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default) =>
        WinMlCatalogEpInstallerCore.Unsupported(progress, cancellationToken);
#endif
}

internal sealed class QnnCatalogEpInstaller : IQnnCatalogEpInstaller
{
#if WINDOWS
    public async Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return await WinMlCatalogEpInstallerCore.EnsureCoreAsync(
            ExecutionProviderKind.Qnn,
            "QNN (WinML catalog)",
            progress,
            cancellationToken).ConfigureAwait(false);
    }
#else
    public Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default) =>
        WinMlCatalogEpInstallerCore.Unsupported(progress, cancellationToken);
#endif
}

internal sealed class VitisAiCatalogEpInstaller : IVitisAiCatalogEpInstaller
{
#if WINDOWS
    public async Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return await WinMlCatalogEpInstallerCore.EnsureCoreAsync(
            ExecutionProviderKind.VitisAi,
            "Vitis AI (WinML catalog)",
            progress,
            cancellationToken).ConfigureAwait(false);
    }
#else
    public Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default) =>
        WinMlCatalogEpInstallerCore.Unsupported(progress, cancellationToken);
#endif
}
