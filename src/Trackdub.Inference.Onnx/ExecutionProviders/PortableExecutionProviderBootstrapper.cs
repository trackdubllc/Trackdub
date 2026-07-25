using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.ExecutionProviders;

public sealed class PortableExecutionProviderBootstrapper : IExecutionProviderBootstrapper
{
    public Task<ExecutionProviderBootstrapResult> BootstrapAsync(
        ExecutionProviderKind provider,
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateResult(provider));
    }

    public Task<ExecutionProviderBootstrapResult> CheckReadinessAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateResult(provider));
    }

    private static ExecutionProviderBootstrapResult CreateResult(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu =>
                new(provider, provider, Succeeded: true,
                    Detail: "CPU provider is available in the portable ONNX runtime build."),

            ExecutionProviderKind.DirectMl or
            ExecutionProviderKind.Dnnl or
            ExecutionProviderKind.TensorRTRtx or
            ExecutionProviderKind.OpenVino or
            ExecutionProviderKind.OpenVinoCatalog or
            ExecutionProviderKind.CoreMl or
            ExecutionProviderKind.Cuda or
            ExecutionProviderKind.TensorRt or
            ExecutionProviderKind.Migraphx or
            ExecutionProviderKind.Qnn or
            ExecutionProviderKind.VitisAi =>
                new(provider, ExecutionProviderKind.Cpu, Succeeded: false,
                    Detail: $"{provider} is not available in the portable net10.0 build. Falling back to CPU.",
                    FailureReason: $"{provider} unavailable in portable build; CPU fallback activated."),

            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported execution provider kind."),
        };
}
