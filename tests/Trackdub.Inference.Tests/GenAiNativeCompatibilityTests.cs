using Trackdub.Inference.Onnx.Runtime;

namespace Trackdub.Inference.Tests;

public sealed class GenAiNativeCompatibilityTests
{
    [Fact]
    public void RejectsOlderNativeRuntimeThatWouldFailGenAiInitialization()
    {
        Assert.False(GenAiNativeCompatibility.IsCompatible(new Version(1, 30, 0, 0), 1, 24));
        Assert.True(GenAiNativeCompatibility.IsCompatible(new Version(1, 30, 0, 0), 1, 30));
    }
}
