using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Finds the external-data sidecar files an ONNX model's tensors reference, so memory
/// admission can size models whose weights live outside the <c>.onnx</c> container
/// (MADLAD-400's 1.4 MB <c>encoder_model.onnx</c> carries a 2.67 GB <c>encoder_model.onnx_data</c>).
/// </summary>
/// <remarks>
/// <para>
/// The graph is walked at the protobuf wire level without loading weights: every
/// <c>TensorProto.external_data</c> entry keyed <c>location</c> under the top-level graph's
/// initializers, sparse initializers, and node attributes (including subgraphs) is resolved
/// relative to the model's directory. Locations that are rooted or escape that directory
/// are ignored, matching ONNX Runtime's own external-data path validation.
/// </para>
/// <para>
/// When the graph cannot be parsed, the conventional siblings <c>&lt;model&gt;.data</c> and
/// <c>&lt;model&gt;_data</c> are reported instead. Results are cached per
/// (full path, length, last-write ticks) of the <c>.onnx</c> file; sidecar sizes are read by
/// the caller on each use, so a replaced sidecar is re-measured without re-walking the graph.
/// </para>
/// </remarks>
internal static class OnnxExternalDataSidecars
{
    private const int MaxGraphDepth = 32;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, (long Length, long WriteTicks, string[] Sidecars)> Cache =
        new(PathComparer);

    /// <summary>
    /// Total size in bytes of the distinct sidecar files <paramref name="modelPath"/> references
    /// that exist on disk. Returns 0 for inline-weight models and unreadable files.
    /// </summary>
    internal static long GetTotalBytes(string modelPath)
    {
        long total = 0;
        foreach (string sidecar in GetSidecarPaths(modelPath))
        {
            try
            {
                var info = new FileInfo(sidecar);
                if (info.Exists)
                {
                    total = checked(total + info.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"OnnxExternalDataSidecars: failed to size sidecar '{sidecar}': {ex.Message}");
            }
        }

        return total;
    }

    /// <summary>
    /// Full paths of the distinct sidecar files <paramref name="modelPath"/> references, whether
    /// or not they exist. Empty when the model keeps its weights inline or cannot be read.
    /// </summary>
    internal static IReadOnlyList<string> GetSidecarPaths(string modelPath)
    {
        string fullPath;
        long length;
        long writeTicks;
        try
        {
            fullPath = Path.GetFullPath(modelPath);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return [];
            }

            length = info.Length;
            writeTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return [];
        }

        if (Cache.TryGetValue(fullPath, out var cached) &&
            cached.Length == length && cached.WriteTicks == writeTicks)
        {
            return cached.Sidecars;
        }

        string[] sidecars;
        try
        {
            sidecars = ReadSidecarPaths(fullPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or EndOfStreamException)
        {
            // Not a parseable ONNX graph: fall back to the naming conventions exporters use.
            sidecars = [fullPath + ".data", fullPath + "_data"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient read failure: do not cache, so the next key build retries the walk.
            System.Diagnostics.Trace.TraceWarning(
                $"OnnxExternalDataSidecars: failed to read '{fullPath}': {ex.Message}");
            return [fullPath + ".data", fullPath + "_data"];
        }

        Cache[fullPath] = (length, writeTicks, sidecars);
        return sidecars;
    }

    /// <summary>Process-local cache clear (tests / model hot-swap).</summary>
    internal static void ResetCache() => Cache.Clear();

    private static string[] ReadSidecarPaths(string fullPath)
    {
        string modelDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var locations = new HashSet<string>(StringComparer.Ordinal);
        using (var stream = new FileStream(
                   fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024))
        {
            // ModelProto.graph = 7.
            ForEachMessageField(stream, stream.Length, (field, end) =>
            {
                if (field == 7)
                {
                    WalkGraph(stream, end, locations, depth: 0);
                }
            });
        }

        var sidecars = new HashSet<string>(PathComparer);
        foreach (string location in locations)
        {
            if (TryResolveLocation(modelDirectory, location, out string? sidecar) &&
                !PathComparer.Equals(sidecar, fullPath))
            {
                sidecars.Add(sidecar);
            }
        }

        return sidecars.Order(PathComparer).ToArray();
    }

    private static bool TryResolveLocation(
        string modelDirectory,
        string location,
        [NotNullWhen(true)] out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(location) || Path.IsPathRooted(location))
        {
            return false;
        }

        try
        {
            string candidate = Path.GetFullPath(Path.Join(modelDirectory, location));
            string root = Path.EndsInDirectorySeparator(modelDirectory)
                ? modelDirectory
                : modelDirectory + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                return false;
            }

            resolved = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // GraphProto: node = 1, initializer = 5, sparse_initializer = 15.
    private static void WalkGraph(Stream stream, long end, HashSet<string> locations, int depth)
    {
        if (depth > MaxGraphDepth)
        {
            throw new InvalidDataException("ONNX subgraph nesting is too deep.");
        }

        ForEachMessageField(stream, end, (field, fieldEnd) =>
        {
            switch (field)
            {
                case 1:
                    WalkNode(stream, fieldEnd, locations, depth);
                    break;
                case 5:
                    WalkTensor(stream, fieldEnd, locations);
                    break;
                case 15:
                    WalkSparseTensor(stream, fieldEnd, locations);
                    break;
            }
        });
    }

    // NodeProto: attribute = 5.
    private static void WalkNode(Stream stream, long end, HashSet<string> locations, int depth) =>
        ForEachMessageField(stream, end, (field, fieldEnd) =>
        {
            if (field == 5)
            {
                WalkAttribute(stream, fieldEnd, locations, depth);
            }
        });

    // AttributeProto: t = 5, g = 6, tensors = 10, graphs = 11, sparse_tensor = 22, sparse_tensors = 23.
    private static void WalkAttribute(Stream stream, long end, HashSet<string> locations, int depth) =>
        ForEachMessageField(stream, end, (field, fieldEnd) =>
        {
            switch (field)
            {
                case 5 or 10:
                    WalkTensor(stream, fieldEnd, locations);
                    break;
                case 6 or 11:
                    WalkGraph(stream, fieldEnd, locations, depth + 1);
                    break;
                case 22 or 23:
                    WalkSparseTensor(stream, fieldEnd, locations);
                    break;
            }
        });

    // SparseTensorProto: values = 1, indices = 2.
    private static void WalkSparseTensor(Stream stream, long end, HashSet<string> locations) =>
        ForEachMessageField(stream, end, (field, fieldEnd) =>
        {
            if (field is 1 or 2)
            {
                WalkTensor(stream, fieldEnd, locations);
            }
        });

    // TensorProto: external_data = 13 (StringStringEntryProto: key = 1, value = 2).
    private static void WalkTensor(Stream stream, long end, HashSet<string> locations) =>
        ForEachMessageField(stream, end, (field, fieldEnd) =>
        {
            if (field != 13)
            {
                return;
            }

            string? key = null;
            string? value = null;
            ForEachMessageField(stream, fieldEnd, (entryField, entryEnd) =>
            {
                if (entryField is 1 or 2)
                {
                    string text = ReadUtf8(stream, entryEnd);
                    if (entryField == 1)
                    {
                        key = text;
                    }
                    else
                    {
                        value = text;
                    }
                }
            });

            if (key == "location" && value is not null)
            {
                locations.Add(value);
            }
        });

    /// <summary>
    /// Iterates the fields of one protobuf message spanning up to <paramref name="end"/>, invoking
    /// <paramref name="onLengthDelimited"/> for each length-delimited field with the field's end
    /// offset. The stream is repositioned past every field afterwards, so callbacks may stop early.
    /// </summary>
    private static void ForEachMessageField(Stream stream, long end, Action<int, long> onLengthDelimited)
    {
        while (stream.Position < end)
        {
            ulong tag = ReadVarint(stream, end);
            ulong field = tag >> 3;
            int wireType = (int)(tag & 7);
            if (field is 0 or > (ulong)int.MaxValue)
            {
                throw new InvalidDataException("Invalid ONNX field tag.");
            }

            switch (wireType)
            {
                case 0:
                    ReadVarint(stream, end);
                    break;
                case 1:
                case 5:
                    Skip(stream, wireType == 1 ? 8 : 4, end);
                    break;
                case 2:
                    long length = checked((long)ReadVarint(stream, end));
                    long fieldEnd = checked(stream.Position + length);
                    if (fieldEnd > end)
                    {
                        throw new InvalidDataException("Truncated ONNX field.");
                    }

                    onLengthDelimited((int)field, fieldEnd);
                    stream.Position = fieldEnd;
                    break;
                default:
                    throw new InvalidDataException("Unsupported ONNX wire type.");
            }
        }
    }

    private static void Skip(Stream stream, long count, long end)
    {
        long next = checked(stream.Position + count);
        if (next > end)
        {
            throw new InvalidDataException("Truncated ONNX field.");
        }

        stream.Position = next;
    }

    private static string ReadUtf8(Stream stream, long end)
    {
        long length = end - stream.Position;
        if (length > 4096)
        {
            // External-data keys and locations are short relative paths; anything larger is not one.
            return string.Empty;
        }

        Span<byte> buffer = stackalloc byte[(int)length];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    private static ulong ReadVarint(Stream stream, long end)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70 && stream.Position < end; shift += 7)
        {
            int next = stream.ReadByte();
            if (next < 0)
            {
                break;
            }

            if (shift == 63 && next > 1)
            {
                break;
            }

            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Invalid ONNX varint.");
    }
}
