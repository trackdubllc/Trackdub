using System.Text;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Tests for <see cref="SessionPoolKey.EstimateVramMb"/>: external-data sidecars are counted
/// and the resident factor follows the execution provider.
/// </summary>
public sealed class SessionPoolKeyVramEstimateTests : IDisposable
{
    private const int MiB = 1024 * 1024;

    private readonly string directory = Path.Join(Path.GetTempPath(), $"vram-est-{Guid.NewGuid():N}");

    public SessionPoolKeyVramEstimateTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        OnnxExternalDataSidecars.ResetCache();
        Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData(".onnx.data")]
    [InlineData(".onnx_data")]
    public void EstimateVramMb_IncludesReferencedSidecar(string sidecarSuffix)
    {
        string model = WriteModel("encoder_model.onnx", Initializer("w0", "encoder_model" + sidecarSuffix));
        WriteBytes("encoder_model" + sidecarSuffix, 10 * MiB);

        // 10 MB weights → 10 * 2 + 128.
        Assert.Equal(148, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void EstimateVramMb_SidecarSharedByManyInitializers_CountsFileOnce()
    {
        string model = WriteModel(
            "model.onnx",
            Initializer("w0", "weights/shared.bin"),
            Initializer("w1", "weights/shared.bin"),
            Initializer("w2", "weights/shared.bin"));
        WriteBytes(Path.Join("weights", "shared.bin"), 10 * MiB);

        Assert.Equal(148, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void EstimateVramMb_SumsDistinctSidecars()
    {
        string model = WriteModel(
            "model.onnx",
            Initializer("w0", "part0.bin"),
            Initializer("w1", "part1.bin"),
            Initializer("w2", "missing.bin"));
        WriteBytes("part0.bin", 6 * MiB);
        WriteBytes("part1.bin", 4 * MiB);

        Assert.Equal(148, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void EstimateVramMb_IncludesSidecarReferencedFromSubgraph()
    {
        byte[] thenBranch = Graph(Initializer("then_w", "branch.bin"));
        byte[] ifNode = Concat(
            StringField(4, "If"),
            MessageField(5, Concat(StringField(1, "then_branch"), MessageField(6, thenBranch))));
        string model = WriteModel("model.onnx", MessageField(1, ifNode));
        WriteBytes("branch.bin", 10 * MiB);

        Assert.Equal(148, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void EstimateVramMb_IgnoresLocationsOutsideModelDirectory()
    {
        string modelDirectory = Path.Join(directory, "model");
        Directory.CreateDirectory(modelDirectory);
        string model = WriteModel(
            Path.Join("model", "model.onnx"),
            Initializer("w0", "../outside.bin"),
            Initializer("w1", Path.Join(directory, "rooted.bin")));
        WriteBytes("outside.bin", 10 * MiB);
        WriteBytes("rooted.bin", 10 * MiB);

        Assert.Equal(128, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void EstimateVramMb_InlineGraph_IgnoresUnreferencedAdjacentDataFile()
    {
        string model = WriteModel("model.onnx", Initializer("w0", location: null));
        WriteBytes("model.onnx.data", 10 * MiB);

        Assert.Equal(128, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void EstimateVramMb_UnparseableGraph_FallsBackToAdjacentDataFiles()
    {
        string model = WriteBytes("model.onnx", 1 * MiB); // all zeros: field tag 0 is invalid
        WriteBytes("model.onnx.data", 6 * MiB);
        WriteBytes("model.onnx_data", 3 * MiB);

        // 1 + 6 + 3 MB → 10 * 2 + 128.
        Assert.Equal(148, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void EstimateVramMb_ReplacedSidecar_IsRemeasuredWithUnchangedGraph()
    {
        string model = WriteModel("model.onnx", Initializer("w0", "model.onnx.data"));
        WriteBytes("model.onnx.data", 10 * MiB);
        Assert.Equal(148, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));

        WriteBytes("model.onnx.data", 20 * MiB);

        Assert.Equal(168, SessionPoolKey.EstimateVramMb(model, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void EstimateVramMb_MissingFile_ReturnsDefault()
    {
        Assert.Equal(
            SessionPoolKey.DefaultEstimatedVramMb,
            SessionPoolKey.EstimateVramMb(Path.Join(directory, "absent.onnx"), ExecutionProviderKind.TensorRTRtx));
    }

    [Theory]
    [InlineData(ExecutionProviderKind.TensorRTRtx, 253)] // 100 * 1.25 + 128
    [InlineData(ExecutionProviderKind.Cpu, 328)]         // 100 * 2 + 128
    [InlineData(ExecutionProviderKind.Dnnl, 328)]
    [InlineData(ExecutionProviderKind.DirectMl, 328)]
    [InlineData(ExecutionProviderKind.Cuda, 328)]
    [InlineData(ExecutionProviderKind.TensorRt, 328)]
    [InlineData(ExecutionProviderKind.OpenVino, 328)]
    public void EstimateFromWeightBytes_ScalesByProvider(ExecutionProviderKind provider, long expectedMb)
    {
        Assert.Equal(expectedMb, SessionPoolKey.EstimateFromWeightBytes(100L * MiB, provider));
    }

    [Fact]
    public void EstimateFromWeightBytes_SubMegabyteWeights_KeepPerGraphAllowance()
    {
        Assert.Equal(128, SessionPoolKey.EstimateFromWeightBytes(0, ExecutionProviderKind.TensorRTRtx));
        Assert.Equal(128, SessionPoolKey.EstimateFromWeightBytes(MiB - 1, ExecutionProviderKind.Cpu));
    }

    [Fact]
    public void KeyHelpers_UseKeyProviderForSidecarInclusiveEstimate()
    {
        string encoder = WriteModel("encoder_model.onnx", Initializer("w0", "encoder_model.onnx_data"));
        WriteBytes("encoder_model.onnx_data", 100 * MiB);

        SessionPoolKey trt = SessionPoolKey.ForEncoder("madlad", encoder, ExecutionProviderKind.TensorRTRtx);
        SessionPoolKey dml = SessionPoolKey.ForEncoder("madlad", encoder, ExecutionProviderKind.DirectMl);

        Assert.Equal(253, trt.EstimatedVramMb);
        Assert.Equal(328, dml.EstimatedVramMb);
    }

    [Fact]
    public async Task CreateAsync_UsesSidecarInclusiveEstimate_AndContentHashCoversGraphOnly()
    {
        string decoder = WriteModel("decoder_model.onnx", Initializer("w0", "decoder_model.onnx_data"));
        WriteBytes("decoder_model.onnx_data", 100 * MiB);

        SessionPoolKey key = await SessionPoolKey.CreateAsync(
            "madlad", null, null, ExecutionProviderKind.TensorRTRtx, decoder, deviceId: null,
            graphRole: "decoder", optionsFingerprint: null, CancellationToken.None);

        Assert.Equal(253, key.EstimatedVramMb);
        Assert.Equal(SessionPoolKey.HashModelContent(decoder), key.ModelContentHash);
    }

    /// <summary>
    /// MADLAD-400 3B <c>trt_rtx_mixed_fp16_fp32</c> encoder + decoder, sized from the exported
    /// files. On TensorRT RTX the bundle fits the default budget of a 12 GB card (it runs there,
    /// using ~8 GB); the generic 2× factor would refuse it.
    /// </summary>
    [Fact]
    public void MadladFp16Bundle_FitsTwelveGigabyteBudgetOnTensorRtRtxOnly()
    {
        const long encoderBytes = 1_433_988L + 2_671_905_792L;
        const long decoderBytes = 2_594_639L + 3_879_930_880L;
        long budgetMb = InferenceSessionPool.ScaleAcceleratorBudgetMb(12_227);

        long trtMb = SessionPoolKey.EstimateFromWeightBytes(encoderBytes, ExecutionProviderKind.TensorRTRtx)
            + SessionPoolKey.EstimateFromWeightBytes(decoderBytes, ExecutionProviderKind.TensorRTRtx);
        long genericMb = SessionPoolKey.EstimateFromWeightBytes(encoderBytes, ExecutionProviderKind.DirectMl)
            + SessionPoolKey.EstimateFromWeightBytes(decoderBytes, ExecutionProviderKind.DirectMl);

        Assert.InRange(trtMb, 7_600, budgetMb);
        Assert.True(genericMb > budgetMb, $"2x estimate {genericMb} MB should exceed {budgetMb} MB.");
    }

    // ── Minimal ONNX protobuf writer ─────────────────────────────────────────

    private string WriteModel(string relativePath, params byte[][] graphFields)
    {
        // ModelProto: ir_version = 1, graph = 7.
        byte[] model = Concat(VarintField(1, 8), MessageField(7, Graph(graphFields)));
        string path = Path.Join(directory, relativePath);
        File.WriteAllBytes(path, model);
        return path;
    }

    private string WriteBytes(string relativePath, int length)
    {
        string path = Path.Join(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    // GraphProto: name = 2, then the supplied node (1) / initializer (5) fields.
    private static byte[] Graph(params byte[][] fields) =>
        Concat([StringField(2, "g"), .. fields]);

    // GraphProto.initializer = 5 → TensorProto: dims = 1, data_type = 2, name = 8,
    // external_data = 13 (key = 1, value = 2), data_location = 14 (EXTERNAL = 1).
    private static byte[] Initializer(string name, string? location)
    {
        byte[] tensor = location is null
            ? Concat(VarintField(1, 4), VarintField(2, 1), StringField(8, name), MessageField(9, new byte[16]))
            : Concat(
                VarintField(1, 4),
                VarintField(2, 1),
                StringField(8, name),
                MessageField(13, Concat(StringField(1, "location"), StringField(2, location.Replace('\\', '/')))),
                MessageField(13, Concat(StringField(1, "offset"), StringField(2, "0"))),
                MessageField(13, Concat(StringField(1, "length"), StringField(2, "16"))),
                VarintField(14, 1));
        return MessageField(5, tensor);
    }

    private static byte[] StringField(int field, string value) =>
        MessageField(field, Encoding.UTF8.GetBytes(value));

    private static byte[] MessageField(int field, byte[] payload) =>
        Concat(Varint(((ulong)field << 3) | 2), Varint((ulong)payload.Length), payload);

    private static byte[] VarintField(int field, ulong value) =>
        Concat(Varint((ulong)field << 3), Varint(value));

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte next = (byte)(value & 0x7f);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        }
        while (value != 0);

        return [.. bytes];
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static part => part)];
}
