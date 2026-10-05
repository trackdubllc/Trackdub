using System.Buffers.Binary;
using Trackdub.Inference.Onnx.DeepFilterNet;

namespace Trackdub.Inference.Tests.DeepFilterNet;

/// <summary>
/// Tests for <see cref="DeepFilterNetOnnxGraphTransform"/>, which rewrites the cached
/// DeepFilterNet3 ONNX exports into stateful graphs: GRU hidden states become explicit
/// graph inputs/outputs and causal input Pad nodes are neutralized (the caller feeds
/// true lookback history instead of zeros).
/// </summary>
public sealed class DeepFilterNetOnnxGraphTransformTests
{
    [Fact]
    public void Transform_NeutralizesCausalPadAndExposesGruState_OnSyntheticModel()
    {
        byte[] model = BuildSyntheticModel();

        (byte[] transformed, IReadOnlyList<string> stateInputs, IReadOnlyList<string> stateOutputs) =
            DeepFilterNetOnnxGraphTransform.Transform(model, "test");

        var graph = ParseModelGraph(transformed);
        var nodes = graph.Nodes;

        // The Pad node is gone and the Conv reads the graph input directly.
        Assert.DoesNotContain(nodes, static n => n.Op == "Pad");
        Node conv = Assert.Single(nodes, static n => n.Op == "Conv");
        Assert.Equal("feat", conv.Inputs[0]);

        // The GRU's initial_h slot now comes from a new graph input.
        Node gru = Assert.Single(nodes, static n => n.Op == "GRU");
        Assert.Equal("state_in_test_0", gru.Inputs[5]);

        // The GRU's existing Y_h output is exposed as a graph output.
        Assert.Equal(["state_in_test_0"], stateInputs);
        Assert.Equal(["gru_h"], stateOutputs);
        Assert.Contains("state_in_test_0", graph.Inputs.Select(static i => i.Name));
        Assert.Contains("gru_h", graph.Outputs.Select(static o => o.Name));

        // The dead zero-initializer chain remains but nothing consumes it: the ConstantOfShape
        // node still exists (ORT prunes it) and is no longer referenced by the GRU.
        Node constantOfShape = Assert.Single(nodes, static n => n.Op == "ConstantOfShape");
        Assert.Equal("zeros", constantOfShape.Outputs[0]);
    }

    [Fact]
    public void Transform_StateValueInfo_DeclaresFloat32HiddenShape()
    {
        byte[] transformed = DeepFilterNetOnnxGraphTransform
            .Transform(BuildSyntheticModel(), "test").Model;

        var graph = ParseModelGraph(transformed);
        ValueInfo stateIn = Assert.Single(graph.Inputs, static i => i.Name == "state_in_test_0");
        Assert.Equal(1, stateIn.ElemType); // float32
        Assert.Equal<object>([1L, 1L, 256L], stateIn.Dims);

        // The state-out shape keeps a symbolic batch dim (matches the GRU's Y_h contract).
        ValueInfo stateOut = Assert.Single(graph.Outputs, static o => o.Name == "gru_h");
        Assert.Equal(1, stateOut.ElemType);
        Assert.Equal(1L, stateOut.Dims[0]);
        Assert.Equal("batch", stateOut.Dims[1]);
        Assert.Equal(256L, stateOut.Dims[2]);
    }

    // ---- Minimal ONNX protobuf writer (test-local, mirrors the transform's wire format) ----

    private static byte[] BuildSyntheticModel()
    {
        // Nodes in dependency order; the Pad output feeds only the Conv.
        byte[] padNode = BuildNode(
            ["feat", "pads", "cv"],
            ["padded"],
            "/pad",
            "Pad",
            []);
        byte[] convNode = BuildNode(
            ["padded", "w"],
            ["conv_out"],
            "/conv",
            "Conv",
            [AttrInts("kernel_shape", [3, 1]), AttrInts("pads", [0, 0, 0, 0])]);
        byte[] zerosNode = BuildNode(
            ["shape"],
            ["zeros"],
            "/zeros",
            "ConstantOfShape",
            []);
        byte[] gruNode = BuildNode(
            ["conv_out", "W", "R", "B", "", "zeros"],
            ["gru_out", "gru_h"],
            "/gru",
            "GRU",
            []);

        byte[] pads = Tensor("pads", [8], [0, 0, 2, 0, 0, 0, 0, 0]);
        byte[] cv = TensorScalar("cv", []);
        byte[] w = Tensor("w", [1, 1, 3, 1], [1f, 1f, 1f]);
        byte[] W = Tensor("W", [1, 3, 3], [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f]);
        byte[] R = Tensor("R", [1, 3, 3], [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f]);
        byte[] B = Tensor("B", [1, 6], [0f, 0f, 0f, 0f, 0f, 0f]);

        byte[] graph = Graph(
            [padNode, convNode, zerosNode, gruNode],
            [w, W, R, B, pads, cv],
            [BuildValueInfo("feat", [1, 1, 4, 32])],
            [BuildValueInfo("gru_out", [1, 1, 4, 1])]);

        return Model(graph);
    }

    private static byte[] Model(byte[] graph)
    {
        return Concat(
            VarintField(1, 12),          // ir_version = 12
            BytesField(7, graph));       // graph
    }

    private static byte[] Graph(byte[][] nodes, byte[][] initializers, byte[][] inputs, byte[][] outputs)
    {
        var parts = new List<byte[]>();
        foreach (byte[] n in nodes)
        {
            parts.Add(BytesField(1, n));
        }

        foreach (byte[] init in initializers)
        {
            parts.Add(BytesField(5, init));
        }

        foreach (byte[] input in inputs)
        {
            parts.Add(BytesField(11, input));
        }

        foreach (byte[] output in outputs)
        {
            parts.Add(BytesField(12, output));
        }

        return Concat(parts.ToArray());
    }

    private static byte[] BuildNode(string[] inputs, string[] outputs, string name, string op, byte[][] attrs)
    {
        var parts = new List<byte[]>();
        foreach (string input in inputs)
        {
            parts.Add(StringField(1, input));
        }

        foreach (string output in outputs)
        {
            parts.Add(StringField(2, output));
        }

        parts.Add(StringField(3, name));
        parts.Add(StringField(4, op));
        foreach (byte[] attr in attrs)
        {
            parts.Add(BytesField(5, attr));
        }

        return Concat(parts.ToArray());
    }

    private static byte[] AttrInts(string name, int[] values)
    {
        var parts = new List<byte[]>
        {
            StringField(1, name),
            VarintField(20, 8), // AttributeProto.ints = INT type 8
        };
        foreach (int v in values)
        {
            parts.Add(VarintField(8, (long)v));
        }

        return Concat(parts.ToArray());
    }

    private static byte[] Tensor(string name, int[] dims, int[] ints)
    {
        var parts = new List<byte[]>();
        foreach (int d in dims)
        {
            parts.Add(VarintField(1, d)); // dims (int64)
        }

        parts.Add(VarintField(2, 7));      // data_type int64
        parts.Add(StringField(8, name));   // name
        foreach (int v in ints)
        {
            parts.Add(VarintField(7, v));  // int64_data
        }

        return Concat(parts.ToArray());
    }

    private static byte[] Tensor(string name, int[] dims, float[] floats)
    {
        var parts = new List<byte[]>();
        foreach (int d in dims)
        {
            parts.Add(VarintField(1, d));
        }

        parts.Add(VarintField(2, 1));      // data_type float32
        parts.Add(StringField(8, name));
        var raw = new byte[floats.Length * 4];
        foreach (int i in Enumerable.Range(0, floats.Length))
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4), floats[i]);
        }

        parts.Add(BytesField(9, raw));     // raw_data
        return Concat(parts.ToArray());
    }

    private static byte[] TensorScalar(string name, int[] dims)
    {
        var parts = new List<byte[]>
        {
            VarintField(2, 1),
            StringField(8, name),
        };
        var raw = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(raw, 0f);
        parts.Add(BytesField(9, raw));
        return Concat(parts.ToArray());
    }

    private static byte[] BuildValueInfo(string name, int[] dims)
    {
        // ValueInfoProto { name:1, type:2{ tensor_type:1{ elem_type:1, shape:2{ dim:1{ dim_value:1 } } } } }
        var shape = Concat(dims.Select(static d => BytesField(1, VarintField(1, d))).ToArray());
        var tensor = Concat(VarintField(1, 1), BytesField(2, shape));
        var type = BytesField(1, tensor);
        return Concat(StringField(1, name), BytesField(2, type));
    }

    private static byte[] StringField(int field, string value) =>
        BytesField(field, System.Text.Encoding.UTF8.GetBytes(value));

    private static byte[] VarintField(int field, long value)
    {
        ulong v = unchecked((ulong)value);
        return Concat(Varint((ulong)((field << 3) | 0)), Varint(v));
    }

    private static byte[] BytesField(int field, byte[] value)
    {
        return Concat(Varint((ulong)((field << 3) | 2)), Varint((ulong)value.Length), value);
    }

    private static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        while (v >= 128)
        {
            bytes.Add((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }

        bytes.Add((byte)v);
        return [.. bytes];
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = parts.Sum(static p => p.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    // ---- Minimal ONNX protobuf reader for assertions ----

    private static GraphView ParseModelGraph(byte[] model)
    {
        List<ProtoField> modelFields = Parse(model);
        byte[] graphBytes = modelFields.Single(static f => f.Field == 7).Value;
        List<ProtoField> graphFields = Parse(graphBytes);

        var nodes = new List<Node>();
        foreach (ProtoField field in graphFields.Where(static f => f.Field == 1))
        {
            List<ProtoField> nodeFields = Parse(field.Value);
            string? op = DecodeString(nodeFields.Where(static f => f.Field == 4).Select(static f => f.Value).FirstOrDefault());
            nodes.Add(new Node(
                nodeFields.Where(static f => f.Field == 1).Select(static f => DecodeString(f.Value)).ToArray(),
                nodeFields.Where(static f => f.Field == 2).Select(static f => DecodeString(f.Value)).ToArray(),
                op));
        }

        var inputs = graphFields.Where(static f => f.Field == 11).Select(static f => ParseValueInfo(f.Value)).ToArray();
        var outputs = graphFields.Where(static f => f.Field == 12).Select(static f => ParseValueInfo(f.Value)).ToArray();
        return new GraphView(nodes, inputs, outputs);
    }

    private static ValueInfo ParseValueInfo(byte[] raw)
    {
        List<ProtoField> fields = Parse(raw);
        if (fields.Count(static f => f.Field == 1) != 1)
        {
            throw new InvalidOperationException(
                $"ValueInfo has {fields.Count(static f => f.Field == 1)} name fields; raw={Convert.ToHexString(raw)}");
        }

        string name = DecodeString(fields.Single(static f => f.Field == 1).Value);
        byte[] typeProto = fields.Single(static f => f.Field == 2).Value;
        List<ProtoField> typeFields = Parse(typeProto);
        byte[] tensor = typeFields.Single(static f => f.Field == 1).Value;
        List<ProtoField> tensorFields = Parse(tensor);
        int elemType = (int)DecodeVarint(tensorFields.Single(static f => f.Field == 1).Value);
        var dims = new List<object>();
        if (tensorFields.FirstOrDefault(static f => f.Field == 2) is { } shapeField)
        {
            foreach (ProtoField dim in Parse(shapeField.Value).Where(static f => f.Field == 1))
            {
                List<ProtoField> dimFields = Parse(dim.Value);
                ProtoField? valueField = dimFields.FirstOrDefault(static f => f.Field == 1);
                if (valueField is { Wire: 0 } vf && vf.Value is not null)
                {
                    dims.Add((long)DecodeVarint(vf.Value));
                }
                else if (valueField is { Wire: 2 } vf2 && vf2.Value is { Length: > 0 })
                {
                    dims.Add(DecodeString(vf2.Value));
                }
                else if (dimFields.FirstOrDefault(static f => f.Field == 2) is { } paramField)
                {
                    dims.Add(DecodeString(paramField.Value));
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Unrecognized Dimension message; dimRaw={Convert.ToHexString(dim.Value)}");
                }
            }
        }

        return new ValueInfo(name, elemType, dims);
    }

    private static List<ProtoField> Parse(ReadOnlySpan<byte> data)
    {
        var fields = new List<ProtoField>();
        int offset = 0;
        while (offset < data.Length)
        {
            (ulong key, offset) = ReadVarint(data, offset);
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    (ulong value, offset) = ReadVarint(data, offset);
                    fields.Add(new ProtoField(field, wire, Varint(value)));
                    break;
                case 2:
                    (ulong length, offset) = ReadVarint(data, offset);
                    byte[] valueBytes = data.Slice(offset, (int)length).ToArray();
                    offset += (int)length;
                    fields.Add(new ProtoField(field, wire, valueBytes));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected protobuf wire type {wire}.");
            }
        }

        return fields;
    }

    private static (ulong Value, int NextOffset) ReadVarint(ReadOnlySpan<byte> data, int offset)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return (value, offset);
            }

            shift += 7;
        }
    }

    private static ulong DecodeVarint(byte[] raw)
    {
        (ulong value, _) = ReadVarint(raw, 0);
        return value;
    }

    private static string DecodeString(byte[]? raw) =>
        raw is null ? string.Empty : System.Text.Encoding.UTF8.GetString(raw);

    private readonly record struct ProtoField(int Field, int Wire, byte[] Value);

    private sealed record Node(string[] Inputs, string[] Outputs, string Op);

    private sealed record ValueInfo(string Name, int ElemType, IReadOnlyList<object> Dims);

    private sealed record GraphView(IReadOnlyList<Node> Nodes, IReadOnlyList<ValueInfo> Inputs, IReadOnlyList<ValueInfo> Outputs);
}