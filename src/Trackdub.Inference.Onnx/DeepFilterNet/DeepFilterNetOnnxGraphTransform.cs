namespace Trackdub.Inference.Onnx.DeepFilterNet;

/// <summary>
/// Rewrites the Rikorose/DeepFilterNet3 ONNX exports into stateful, caller-padded graphs so
/// that a streaming engine can preserve exact neural state across bounded inference windows
/// (matching libDF's tract <c>SimpleState</c> semantics with plain ORT runs):
/// <list type="bullet">
/// <item>Every GRU node's internal zero-initial <c>initial_h</c> (ConstantOfShape/Slice chain)
/// is replaced by a new graph input <c>state_in_&lt;prefix&gt;_&lt;i&gt;</c>, and the GRU's existing
/// <c>Y_h</c> output tensor is added to the graph outputs as <c>state_out_&lt;prefix&gt;_&lt;i&gt;</c>.</item>
/// <item>Causal input <c>Pad</c> nodes (the exported conv1d_causal zero-pads on the time axis)
/// are removed and their consumers read the graph input directly, so the caller supplies the
/// true lookback frames instead of zeros. The only such pads in these models sit on the graph
/// inputs (feat_erb / feat_spec in enc, c0 in df_dec); erb_dec has none.</item>
/// </list>
/// The transformation is a pure protobuf wire rewrite (no model packages): everything other than
/// the edited nodes/inputs/outputs is copied byte-for-byte, so weights and metadata are untouched.
/// </summary>
internal static class DeepFilterNetOnnxGraphTransform
{
    /// <summary>Hidden size of every GRU in the DeepFilterNet3 export (W is [1, 3*hidden, hidden]).</summary>
    public const int GruHiddenSize = 256;

    private const int GraphField = 7;
    private const int NodeField = 1;
    private const int NodeInputField = 1;
    private const int NodeOutputField = 2;
    private const int NodeNameField = 3;
    private const int NodeOpTypeField = 4;
    private const int NodeAttributeField = 5;
    private const int GraphInitializerField = 5;
    private const int GraphInputField = 11;
    private const int GraphOutputField = 12;

    public static (byte[] Model, IReadOnlyList<string> StateInputs, IReadOnlyList<string> StateOutputs) Transform(
        byte[] modelBytes,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(modelBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        List<ProtoField> modelFields = Parse(modelBytes);

        ProtoField graphField = modelFields.Single(static f => f.Field == GraphField);
        List<ProtoField> graphFields = Parse(graphField.Value);

        var stateInputs = new List<string>();
        var stateOutputs = new List<string>();
        var padRewire = new Dictionary<string, string>(StringComparer.Ordinal);
        var rebuiltNodes = new List<byte[]>();

        foreach (ProtoField nodeField in graphFields.Where(static f => f.Field == NodeField))
        {
            List<ProtoField> nodeFields = Parse(nodeField.Value);
            string op = GetNodeOpType(nodeFields);
            string[] inputs = GetNodeInputs(nodeFields);
            string[] outputs = GetNodeOutputs(nodeFields);

            if (string.Equals(op, "Pad", StringComparison.Ordinal))
            {
                // Causal input pad: its output feeds only the next Conv; rewire consumers and drop it.
                padRewire[outputs[0]] = inputs[0];
                continue;
            }

            if (string.Equals(op, "GRU", StringComparison.Ordinal))
            {
                int index = stateInputs.Count;
                string stateIn = $"state_in_{prefix}_{index}";
                if (inputs.Length < 6 || inputs[5].Length == 0)
                {
                    throw new InvalidOperationException(
                        $"GRU node '{outputs[0]}' has no initial_h input slot to expose.");
                }

                stateInputs.Add(stateIn);
                stateOutputs.Add(outputs[1]);
                inputs[5] = stateIn;
            }

            // Rewire any consumer of a neutralized pad's output (the following Conv reads the input).
            for (int i = 0; i < inputs.Length; i++)
            {
                if (padRewire.TryGetValue(inputs[i], out string? replacement))
                {
                    inputs[i] = replacement;
                }
            }

            rebuiltNodes.Add(RebuildNode(nodeFields, inputs, outputs));
        }

        List<ProtoField> rebuiltGraph = RebuildGraph(graphFields, rebuiltNodes, stateInputs, stateOutputs);
        byte[] graphBytes = Serialize(rebuiltGraph);

        byte[] transformed = RebuildModel(modelFields, graphBytes);
        return (transformed, stateInputs, stateOutputs);
    }

    private static byte[] RebuildNode(List<ProtoField> nodeFields, string[] inputs, string[] outputs)
    {
        var parts = new List<byte[]>();
        foreach (string input in inputs)
        {
            parts.Add(SerializeString(NodeInputField, input));
        }

        foreach (string output in outputs)
        {
            parts.Add(SerializeString(NodeOutputField, output));
        }

        foreach (ProtoField field in nodeFields)
                {
                    if (field.Field is NodeInputField or NodeOutputField)
                    {
                        continue;
                    }

                    parts.Add(SerializeField(field));
                }

                return Serialize(parts);
            }

    private static List<ProtoField> RebuildGraph(
        List<ProtoField> graphFields,
        IReadOnlyList<byte[]> rebuiltNodes,
        IReadOnlyList<string> stateInputs,
        IReadOnlyList<string> stateOutputs)
    {
        var rebuilt = new List<ProtoField>();
        bool nodesEmitted = false;
        bool inputsEmitted = false;
        bool outputsEmitted = false;
        foreach (ProtoField field in graphFields)
        {
            if (field.Field == NodeField)
            {
                if (!nodesEmitted)
                {
                    foreach (byte[] node in rebuiltNodes)
                    {
                        rebuilt.Add(new ProtoField(NodeField, 2, node));
                    }

                    nodesEmitted = true;
                }

                continue;
            }

            if (field.Field == GraphInputField)
            {
                rebuilt.Add(field);
                if (!inputsEmitted)
                {
                    foreach (string name in stateInputs)
                    {
                        rebuilt.Add(new ProtoField(GraphInputField, 2, BuildStateValueInfo(name, isOutput: false)));
                    }

                    inputsEmitted = true;
                }

                continue;
            }

            if (field.Field == GraphOutputField)
            {
                rebuilt.Add(field);
                if (!outputsEmitted)
                {
                    foreach (string name in stateOutputs)
                    {
                        rebuilt.Add(new ProtoField(GraphOutputField, 2, BuildStateValueInfo(name, isOutput: true)));
                    }

                    outputsEmitted = true;
                }

                continue;
            }

            rebuilt.Add(field);
        }

        return rebuilt;
    }

    private static byte[] RebuildModel(List<ProtoField> modelFields, byte[] graphBytes)
    {
        var parts = new List<byte[]>();
        foreach (ProtoField field in modelFields)
        {
            if (field.Field == GraphField)
            {
                continue;
            }

            parts.Add(SerializeField(field));
        }

        parts.Add(SerializeBytes(GraphField, graphBytes));
        return Serialize(parts);
    }

    private static byte[] BuildStateValueInfo(string name, bool isOutput)
    {
        // ValueInfoProto { name:1, type:2{ tensor_type:1{ elem_type:1(float32), shape:2{
        //   dim:1{ dim_value:1 } | dim:1{ dim_param:2 } } } } }.
        // Input states are [1,1,hidden]; Y_h outputs keep a symbolic batch dim (matches the GRU).
        var shape = Concat(
            SerializeBytes(1, SerializeVarint(1, 1)),                 // num_directions = 1
            isOutput
                ? SerializeBytes(1, SerializeString(2, "batch"))      // batch dim (symbolic)
                : SerializeBytes(1, SerializeVarint(1, 1)),           // batch = 1
            SerializeBytes(1, SerializeVarint(1, GruHiddenSize)));    // hidden
        byte[] tensor = Concat(SerializeVarint(1, 1), SerializeBytes(2, shape));
        byte[] type = SerializeBytes(1, tensor);
        return Concat(SerializeString(1, name), SerializeBytes(2, type));
    }

    private static string GetNodeOpType(List<ProtoField> nodeFields)
    {
        ProtoField? opField = nodeFields.FirstOrDefault(static f => f.Field == NodeOpTypeField);
        return opField is null ? string.Empty : DecodeString(opField.Value.Value);
    }

    private static string[] GetNodeInputs(List<ProtoField> nodeFields) =>
        nodeFields
            .Where(static f => f.Field == NodeInputField)
            .Select(static f => DecodeString(f.Value))
            .ToArray();

    private static string[] GetNodeOutputs(List<ProtoField> nodeFields) =>
        nodeFields
            .Where(static f => f.Field == NodeOutputField)
            .Select(static f => DecodeString(f.Value))
            .ToArray();

    // ---- protobuf wire format ----

    private readonly record struct ProtoField(int Field, int Wire, byte[] Value);

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
                    fields.Add(new ProtoField(field, wire, data.Slice(offset, checked((int)length)).ToArray()));
                    offset += (int)length;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported protobuf wire type {wire} in ONNX model.");
            }
        }

        return fields;
    }

    private static byte[] Serialize(IReadOnlyList<byte[]> parts)
    {
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] Serialize(IReadOnlyList<ProtoField> fields)
    {
        var parts = new List<byte[]>(fields.Count);
        foreach (ProtoField field in fields)
        {
            parts.Add(SerializeField(field));
        }

        return Serialize(parts);
    }

    private static byte[] SerializeField(ProtoField field) =>
        field.Wire == 0
            ? SerializeVarint(field.Field, unchecked((long)DecodeVarint(field.Value)))
            : SerializeBytes(field.Field, field.Value);

    private static byte[] SerializeBytes(int field, byte[] value)
    {
        return Concat(Varint((ulong)((field << 3) | 2)), Varint((ulong)value.Length), value);
    }

    private static byte[] SerializeString(int field, string value) =>
        SerializeBytes(field, System.Text.Encoding.UTF8.GetBytes(value));

    private static byte[] SerializeVarint(int field, long value) =>
        Concat(Varint((ulong)((field << 3) | 0)), Varint(unchecked((ulong)value)));

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>(10);
        while (value >= 128)
        {
            bytes.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return [.. bytes];
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

    private static string DecodeString(byte[] raw) => System.Text.Encoding.UTF8.GetString(raw);

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
}