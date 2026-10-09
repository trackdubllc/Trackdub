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

    [Fact]
    public void TreatsTheWindowsMlRuntimeAsGenAiWinMlsOwnPairing()
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-genai-winml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Join(directory, "onnxruntime.dll"), [0]);
            Assert.False(GenAiNativeCompatibility.IsWindowsMlRuntime(directory));

            File.WriteAllBytes(Path.Join(directory, "Microsoft.Windows.AI.MachineLearning.dll"), [0]);
            Assert.True(GenAiNativeCompatibility.IsWindowsMlRuntime(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
