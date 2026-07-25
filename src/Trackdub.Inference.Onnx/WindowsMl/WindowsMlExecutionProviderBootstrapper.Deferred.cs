namespace Trackdub.Inference.Onnx.WindowsMl;

public sealed class WindowsMlExecutionProviderBootstrapper
{
    private const string WindowsMlBootstrapDeferredReason =
        "Windows ML bootstrap is deferred in this build. Execution uses Microsoft.ML.OnnxRuntime directly; DirectML routes rely on the OnnxRuntime DirectML provider package.";

    public Task<WindowsMlBootstrapResult> RegisterInstalledCertifiedAsync(CancellationToken cancellationToken) =>
        CreateDeferredResultAsync(WindowsMlBootstrapMode.RegisterInstalledCertified, cancellationToken);

    public Task<WindowsMlBootstrapResult> EnsureAndRegisterCertifiedAsync(CancellationToken cancellationToken) =>
        CreateDeferredResultAsync(WindowsMlBootstrapMode.EnsureAndRegisterCertified, cancellationToken);

    private static Task<WindowsMlBootstrapResult> CreateDeferredResultAsync(
        WindowsMlBootstrapMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WindowsMlBootstrapResult(mode, false, WindowsMlBootstrapDeferredReason));
    }
}
