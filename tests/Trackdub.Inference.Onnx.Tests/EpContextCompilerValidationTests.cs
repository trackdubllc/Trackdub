using System.Text;
using Trackdub.Inference.Onnx.EpContext;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class EpContextCompilerValidationTests
{
    [Theory]
    [InlineData("EPContext", "com.microsoft", true)]
    [InlineData("EPContext", "other.domain", false)]
    [InlineData("Conv", "com.microsoft", false)]
    public void Requires_matching_node_type_and_domain(string opType, string domain, bool expected)
    {
        byte[] node = Message(Field(4, opType), Field(7, domain));
        AssertModel(Message(Field(7, Message(Field(1, node)))), expected);
    }

    [Fact]
    public void Ignores_markers_outside_nodes_and_across_different_nodes()
    {
        byte[] graph = Message(
            Field(1, Message(Field(4, "EPContext"), Field(7, "other"))),
            Field(1, Message(Field(4, "Conv"), Field(7, "com.microsoft"))),
            Field(2, "EPContext com.microsoft ep_cache_context engine_data"));
        AssertModel(Message(Field(7, graph)), false);
    }

    [Fact]
    public void Rejects_plain_text_and_truncated_models()
    {
        AssertModel(Encoding.ASCII.GetBytes("EPContext com.microsoft ep_cache_context"), false);
        AssertModel([0x3a, 0x05, 0x0a], false);
    }

    private static void AssertModel(byte[] model, bool expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"trackdub-epc-{Guid.NewGuid():N}.onnx");
        try
        {
            File.WriteAllBytes(path, model);
            Assert.Equal(expected, EpContextCompiler.TryContainsEpContextNodes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Field(byte number, string value) => Field(number, Encoding.UTF8.GetBytes(value));

    private static byte[] Field(byte number, byte[] value) => Message([(byte)((number << 3) | 2), (byte)value.Length], value);

    private static byte[] Message(params byte[][] parts) => [.. parts.SelectMany(static part => part)];
}
