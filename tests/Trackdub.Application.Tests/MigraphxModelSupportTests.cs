using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Application.Tests;

public sealed class MigraphxModelSupportTests
{
    [Theory]
    [InlineData("whisper-tiny-onnx", "whisper", true)]
    [InlineData("whisper-tiny-genai", "whisper", false)]
    [InlineData("kokoro-onnx", "kokoro", true)]
    public void SupportsModel_rejects_genai_aliases(string alias, string engineFamily, bool expected) =>
        Assert.Equal(expected, MigraphxModelSupport.SupportsModel(alias, engineFamily));
}
