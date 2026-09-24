using System.Text;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Xunit;

namespace Trackdub.Inference.Onnx.Tests;

/// <summary>Unit tests for <see cref="TrtRtxUnsupportedOpScanner"/>.</summary>
public sealed class TrtRtxUnsupportedOpScannerTests
{
    /// <summary>Minimal protobuf Node.op_type encoding (field 4, wire type 2).</summary>
    private static byte[] OpType(string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] marker = new byte[nameBytes.Length + 2];
        marker[0] = 0x22;
        marker[1] = (byte)nameBytes.Length;
        nameBytes.CopyTo(marker, 2);
        return marker;
    }

    [Fact]
    public void FindUnsupportedOps_EmptyFile_ReturnsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"trt-scan-empty-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, []);
        try
        {
            TrtRtxUnsupportedOpScanner.ResetCache();
            Assert.Empty(TrtRtxUnsupportedOpScanner.FindUnsupportedOps(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindUnsupportedOps_StandardOpsOnly_ReturnsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"trt-scan-clean-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("LayerNormalization Gelu Add MatMul Softmax"));
        try
        {
            TrtRtxUnsupportedOpScanner.ResetCache();
            Assert.Empty(TrtRtxUnsupportedOpScanner.FindUnsupportedOps(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindUnsupportedOps_TensorNameContainingOpType_IsNotAHit()
    {
        // Surgery leaves tensor ids like ".../MultiHeadAttention_output_0"; those are not ops.
        string path = Path.Combine(Path.GetTempPath(), $"trt-scan-name-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(
            path,
            Encoding.ASCII.GetBytes("/encoder/encoders.0/self_attn/MultiHeadAttention_output_0 LayerNormalization"));
        try
        {
            TrtRtxUnsupportedOpScanner.ResetCache();
            Assert.Empty(TrtRtxUnsupportedOpScanner.FindUnsupportedOps(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindUnsupportedOps_SkipLayerNormalizationAndBiasGelu_AreReported()
    {
        string path = Path.Combine(Path.GetTempPath(), $"trt-scan-contrib-{Guid.NewGuid():N}.onnx");
        using var stream = new MemoryStream();
        stream.Write("prefix "u8);
        stream.Write(OpType("SkipLayerNormalization"));
        stream.Write(" mid "u8);
        stream.Write(OpType("BiasGelu"));
        stream.Write(" suffix com.microsoft"u8);
        File.WriteAllBytes(path, stream.ToArray());
        try
        {
            TrtRtxUnsupportedOpScanner.ResetCache();
            IReadOnlyList<string> ops = TrtRtxUnsupportedOpScanner.FindUnsupportedOps(path);
            Assert.Contains("SkipLayerNormalization", ops);
            Assert.Contains("BiasGelu", ops);
            Assert.True(TrtRtxUnsupportedOpScanner.ContainsUnsupportedOps(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindUnsupportedOps_MissingFile_ReturnsEmpty()
    {
        TrtRtxUnsupportedOpScanner.ResetCache();
        Assert.Empty(TrtRtxUnsupportedOpScanner.FindUnsupportedOps(
            Path.Combine(Path.GetTempPath(), $"trt-scan-missing-{Guid.NewGuid():N}.onnx")));
    }
}
