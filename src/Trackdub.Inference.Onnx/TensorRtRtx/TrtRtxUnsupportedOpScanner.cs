using System.Collections.Concurrent;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>
/// Detects ONNX graphs that TensorRT-RTX cannot import.
/// </summary>
/// <remarks>
/// <para>
/// TensorRT-RTX's ONNX parser is all-or-nothing: every op must be in its standard ONNX
/// catalog. ORT contrib ops under the <c>com.microsoft</c> domain (fused
/// <c>SkipLayerNormalization</c>, <c>BiasGelu</c>, <c>MultiHeadAttention</c>, …) are either
/// baked into stock Olive/HF exports or re-fused at session load when
/// <see cref="Microsoft.ML.OnnxRuntime.GraphOptimizationLevel"/> is <c>ORT_ENABLE_ALL</c>.
/// Attempting TRT-RTX on those graphs produces a long ModelImporter error storm and then
/// falls back to DirectML/CPU — paying a full failed session init every launch.
/// </para>
/// <para>
/// This scanner reads a cheap byte-level marker set (no ONNX protobuf dependency) so the
/// factory can skip the TRT attempt entirely and go straight to the fallback provider.
/// Results are cached per (path, size, mtime) for the process.
/// </para>
/// </remarks>
public static class TrtRtxUnsupportedOpScanner
{
    /// <summary>
    /// Contrib / fused op names TensorRT-RTX rejects. A hit means "do not open this graph
    /// with the TRT-RTX EP". Keep this list aligned with
    /// <c>resources/olive-recipes/*/NvTensorRtRtx/README.md</c> ("no <c>com.microsoft::</c> op,
    /// so no graph surgery is needed") and <c>docs/reference/gpu-execution-providers.md</c>.
    /// </summary>
    /// <remarks>
    /// Matches are protobuf <c>Node.op_type</c> field tags (field 4, wire type 2 = 0x22) plus
    /// a one-byte length and the ASCII name — not raw substrings. Tensor names such as
    /// <c>.../MultiHeadAttention_output_0</c> survive graph surgery and must not count as ops.
    /// </remarks>
    private static readonly byte[][] UnsupportedOpMarkers = BuildOpTypeMarkers(
        "SkipLayerNormalization",
        "BiasGelu",
        "EmbedLayerNormalization",
        "MultiHeadAttention",
        "GroupQueryAttention",
        "SkipGroupNorm",
        "FastGelu",
        "QuickGelu",
        "NhwcMaxPool",
        "QAttention",
        "Attention");

    private static byte[][] BuildOpTypeMarkers(params string[] opTypes)
    {
        var markers = new List<byte[]>(opTypes.Length);
        foreach (string opType in opTypes)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes(opType);
            if (name.Length is < 1 or > 127)
            {
                continue;
            }

            // NodeProto.op_type = field 4, length-delimited: tag 0x22, len, bytes.
            byte[] marker = new byte[name.Length + 2];
            marker[0] = 0x22;
            marker[1] = (byte)name.Length;
            name.CopyTo(marker, 2);
            markers.Add(marker);
        }

        return markers.ToArray();
    }

    private static readonly ConcurrentDictionary<string, (long Size, long MTime, string[] Ops)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the unsupported op names found in <paramref name="modelPath"/>, or an empty
    /// array when the graph looks importable by TensorRT-RTX.
    /// </summary>
    public static IReadOnlyList<string> FindUnsupportedOps(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        string fullPath = Path.GetFullPath(modelPath);

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                // Let the session factory surface the real FileNotFoundException.
                return [];
            }

            string key = $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (Cache.TryGetValue(key, out (long Size, long MTime, string[] Ops) cached))
            {
                return cached.Ops;
            }

            // Drop stale entries for this path when the file changed.
            foreach (string stale in Cache.Keys
                         .Where(k => k.StartsWith(fullPath + "|", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                if (!string.Equals(stale, key, StringComparison.Ordinal))
                {
                    Cache.TryRemove(stale, out _);
                }
            }

            string[] ops = Scan(fullPath);
            Cache[key] = (info.Length, info.LastWriteTimeUtc.Ticks, ops);
            return ops;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable model: do not block a fallback attempt.
            return [];
        }
    }

    public static bool ContainsUnsupportedOps(string modelPath) =>
        FindUnsupportedOps(modelPath).Count > 0;

    /// <summary>Process-local cache clear (tests / model hot-swap).</summary>
    public static void ResetCache() => Cache.Clear();

    private static string[] Scan(string fullPath)
    {
        // Stream the file in windows so multi-GB models do not load entirely into memory.
        // Op type names are short ASCII identifiers; a windowed search is sufficient.
        var hits = new HashSet<string>(StringComparer.Ordinal);
        byte[] buffer = new byte[4 * 1024 * 1024];
        int carry = 0; // overlap so markers spanning window boundaries are not missed

        using FileStream stream = File.OpenRead(fullPath);
        int read;
        while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            int length = carry + read;
            foreach (byte[] marker in UnsupportedOpMarkers)
            {
                if (ContainsMarker(buffer.AsSpan(0, length), marker))
                {
                    // marker = 0x22, len, ASCII op_type
                    hits.Add(System.Text.Encoding.ASCII.GetString(marker, 2, marker.Length - 2));
                }
            }

            // Keep a tail overlap equal to the longest marker so a split marker is rechecked.
            carry = Math.Min(64, length);
            Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
        }

        return hits.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool ContainsMarker(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
