using Microsoft.Extensions.Logging;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.ExecutionProviders;

public static class OnnxExecutionProviderBootstrapperRegistry
{
    public static void Initialize(
        IExecutionProviderBootstrapper bootstrapper,
        IWindowsMlEpDevicePolicyProvider? devicePolicyProvider = null,
        ILogger? logger = null,
        IOpenVinoAvailabilityProvider? openVinoAvailabilityProvider = null) =>
        OnnxExecutionSessionFactory.Initialize(bootstrapper, devicePolicyProvider, logger, openVinoAvailabilityProvider);

    public static void ResetForTests() =>
        OnnxExecutionSessionFactory.ResetForTests();
}
