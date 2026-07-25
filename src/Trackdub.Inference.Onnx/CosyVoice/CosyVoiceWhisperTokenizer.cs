using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Trackdub.Inference.Onnx.CosyVoice;

internal sealed class CosyVoiceWhisperTokenizer
{
    private static readonly Regex TokenPattern = new(
        CosyVoiceConstants.WhisperTokenPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<ReadOnlyMemory<byte>, int> mergeableRanks;
    private readonly Dictionary<string, int> specialTokens;

    private CosyVoiceWhisperTokenizer(
        Dictionary<ReadOnlyMemory<byte>, int> mergeableRanks,
        Dictionary<string, int> specialTokens)
    {
        this.mergeableRanks = mergeableRanks;
        this.specialTokens = specialTokens;
    }

    public static CosyVoiceWhisperTokenizer Load(string modelRootPath)
    {
        string ranksPath = Path.Combine(modelRootPath, "tokenizer", "tiktoken_ranks.bin");
        if (!File.Exists(ranksPath))
        {
            throw new FileNotFoundException("CosyVoice requires tokenizer/tiktoken_ranks.bin.", ranksPath);
        }

        byte[] data = File.ReadAllBytes(ranksPath);
        if (data.Length < 9 || !data.AsSpan(0, 5).SequenceEqual("TKTR\x01"u8))
        {
            throw new InvalidDataException("Invalid tiktoken ranks file (expected TKTR\\x01 magic).");
        }

        int offset = 5;
        int nVocab = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        int numRanks = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        offset += 4;

        var ranks = new Dictionary<ReadOnlyMemory<byte>, int>(numRanks, ByteSequenceComparer.Instance);
        for (int i = 0; i < numRanks; i++)
        {
            int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            offset += 2;
            byte[] key = data[offset..(offset + keyLen)];
            offset += keyLen;
            int rank = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            ranks[key] = rank;
        }

        int numSpecial = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        var special = new Dictionary<string, int>(numSpecial, StringComparer.Ordinal);
        for (int i = 0; i < numSpecial; i++)
        {
            int strLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            offset += 2;
            string text = Encoding.UTF8.GetString(data, offset, strLen);
            offset += strLen;
            int id = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            special[text] = id;
        }

        _ = nVocab;
        return new CosyVoiceWhisperTokenizer(ranks, special);
    }

    public void ValidateSmokeTest(string modelRootPath)
    {
        string smokePath = Path.Combine(modelRootPath, "tokenizer", "encode_smoke.json");
        if (!File.Exists(smokePath))
        {
            return;
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(smokePath));
        string text = document.RootElement.GetProperty("text").GetString() ?? string.Empty;
        int[] expected = document.RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        int[] actual = Encode(text);
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"CosyVoice tokenizer smoke test failed. Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    public int[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ids = new List<int>();
        foreach (string piece in TokenPattern.Matches(text).Select(m => m.Value))
        {
            if (specialTokens.TryGetValue(piece, out int specialId))
            {
                ids.Add(specialId);
                continue;
            }

            foreach (byte[] tokenBytes in Bpe(Encoding.UTF8.GetBytes(piece)))
            {
                if (!mergeableRanks.TryGetValue(tokenBytes, out int rank))
                {
                    throw new InvalidOperationException($"Tokenizer could not encode piece '{piece}'.");
                }

                ids.Add(rank);
            }
        }

        return ids.ToArray();
    }

    private List<byte[]> Bpe(byte[] tokenBytes)
    {
        if (tokenBytes.Length == 0)
        {
            return [];
        }

        if (tokenBytes.Length == 1)
        {
            return [tokenBytes];
        }

        var word = tokenBytes.Select(b => new[] { b }).ToList();
        HashSet<(byte[], byte[])> pairs = GetPairs(word);
        while (pairs.Count > 0)
        {
            (byte[] first, byte[] second) = pairs
                .OrderBy(pair => mergeableRanks.TryGetValue(Concat(pair.Item1, pair.Item2), out int rank) ? rank : int.MaxValue)
                .First();
            byte[] merged = Concat(first, second);
            if (!mergeableRanks.ContainsKey(merged))
            {
                break;
            }

            var newWord = new List<byte[]>();
            int index = 0;
            while (index < word.Count)
            {
                int found = IndexOf(word, first, index);
                if (found == -1)
                {
                    newWord.AddRange(word.Skip(index));
                    break;
                }

                newWord.AddRange(word.Skip(index).Take(found - index));
                index = found;
                if (index < word.Count - 1 && ByteArraysEqual(word[index], first) && ByteArraysEqual(word[index + 1], second))
                {
                    newWord.Add(merged);
                    index += 2;
                }
                else
                {
                    newWord.Add(word[index]);
                    index++;
                }
            }

            word = newWord;
            if (word.Count == 1)
            {
                break;
            }

            pairs = GetPairs(word);
        }

        return word;
    }

    private static int IndexOf(List<byte[]> word, byte[] target, int start)
    {
        for (int i = start; i < word.Count; i++)
        {
            if (ByteArraysEqual(word[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private static HashSet<(byte[], byte[])> GetPairs(List<byte[]> word)
    {
        var pairs = new HashSet<(byte[], byte[])>(BytePairComparer.Instance);
        for (int i = 0; i < word.Count - 1; i++)
        {
            pairs.Add((word[i], word[i + 1]));
        }

        return pairs;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(static p => p).ToArray();

    private static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right) =>
        left.AsSpan().SequenceEqual(right);

    private sealed class ByteSequenceComparer : IEqualityComparer<ReadOnlyMemory<byte>>
    {
        public static ByteSequenceComparer Instance { get; } = new();

        public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) => x.Span.SequenceEqual(y.Span);

        public int GetHashCode(ReadOnlyMemory<byte> obj)
        {
            HashCode hash = new();
            foreach (byte value in obj.Span)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    private sealed class BytePairComparer : IEqualityComparer<(byte[], byte[])>
    {
        public static BytePairComparer Instance { get; } = new();

        public bool Equals((byte[], byte[]) x, (byte[], byte[]) y) =>
            ByteArraysEqual(x.Item1, y.Item1) && ByteArraysEqual(x.Item2, y.Item2);

        public int GetHashCode((byte[], byte[]) obj)
        {
            HashCode hash = new();
            foreach (byte value in obj.Item1)
            {
                hash.Add(value);
            }

            foreach (byte value in obj.Item2)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
