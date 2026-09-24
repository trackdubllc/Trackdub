using System.Text;
using Trackdub.Inference.Onnx.EpContext;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class EpContextCompilerValidationTests
{
    [Theory]
    // ORT EP-context format: op_type "EPContext" (domain com.microsoft), engine in "ep_cache_context".
    [InlineData("op_type EPContext domain com.microsoft attribute ep_cache_context", true)]
    [InlineData("EPContext only", false)]
    [InlineData("ep_cache_context only", false)]
    // The previous (wrong) marker pair must not count as an EP-context graph.
    [InlineData("com.microsoft.ep.context engine_data", false)]
    [InlineData("Conv Squeeze Reshape", false)]
    public void TryContainsEpContextNodes_requires_the_ort_ep_context_signature(string content, bool expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"trackdub-epc-{Guid.NewGuid():N}.onnx");
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
            Assert.Equal(expected, EpContextCompiler.TryContainsEpContextNodes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
