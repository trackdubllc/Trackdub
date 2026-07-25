using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Dnnl;
using System.Runtime.Versioning;

namespace Trackdub.Inference.Onnx.ExecutionProviders.Mac;

/// <summary>
/// macOS-specific execution provider bootstrapper.
/// Currently supports CPU execution. CoreML support can be added in the future.
/// </summary>
[SupportedOSPlatform("macos10.15")]
public sealed class MacExecutionProviderBootstrapper : IExecutionProviderBootstrapper
{
    private readonly IDnnlReadinessProbe _dnnlReadinessProbe = new DnnlReadinessProbe();

    public Task<ExecutionProviderBootstrapResult> BootstrapAsync(
        ExecutionProviderKind provider,
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (provider is ExecutionProviderKind.Dnnl)
        {
            return ResolveDnnlAsync(provider, cancellationToken);
        }

        return Task.FromResult(provider switch
        {
            ExecutionProviderKind.Cpu =>
                new ExecutionProviderBootstrapResult(
                    provider, provider, Succeeded: true,
                    Detail: "CPU always available on macOS."),

            ExecutionProviderKind.CoreMl =>
                new ExecutionProviderBootstrapResult(
                    provider, provider, Succeeded: true,
                    Detail: "CoreML framework is always present on macOS 10.15+."),

            _ =>
                new ExecutionProviderBootstrapResult(
                    provider, ExecutionProviderKind.Cpu, Succeeded: false,
                    Detail: $"{provider} is not available on macOS. Falling back to CPU.",
                    FailureReason: $"{provider} not supported on macOS."),
        });
    }

    public Task<ExecutionProviderBootstrapResult> CheckReadinessAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken) =>
        BootstrapAsync(provider, allowDownloads: false, cancellationToken);

    private async Task<ExecutionProviderBootstrapResult> ResolveDnnlAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        DnnlReadinessReport report = await _dnnlReadinessProbe
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        return report.IsReady
            ? new(provider, provider, Succeeded: true, Detail: report.Detail)
            : new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                Detail: $"{report.Detail} Falling back to CPU.",
                FailureReason: report.Detail);
    }
}
