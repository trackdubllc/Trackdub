using Microsoft.Extensions.Logging;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.ExecutionProviders;

public static class OnnxExecutionProviderBootstrapperRegistry
{
    public static void Initialize(
        IExecutionProviderBootstrapper bootstrapper,
        IWindowsMlEpDevicePolicyProvider? devicePolicyProvider = null,
        ILogger? logger = null) =>
        OnnxExecutionSessionFactory.Initialize(bootstrapper, devicePolicyProvider, logger);

    public static void ResetForTests() =>
        OnnxExecutionSessionFactory.ResetForTests();
}
