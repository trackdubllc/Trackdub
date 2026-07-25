using System.Text;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Onnx.ForcedAlignment;

/// <summary>
/// Handles the Qwen3-ForcedAligner token/timestamp protocol.
/// Byte-level BPE follows the GPT-2 byte-to-unicode convention used by Qwen2/tiktoken.
/// </summary>
internal static class QwenTimestampProcessor
{
    // Each timestamp class index represents this many seconds.
    // Derived from config.json: timestamp_segment_time = 80 (ms).
    internal const double SecondsPerTimestampClass = 0.08;

    // Standard GPT-2 byte-to-unicode table: maps each raw byte (0-255) to
    // a single Unicode character used as the BPE symbol alphabet.
    private static readonly char[] ByteToUnicode = BuildByteToUnicodeTable();

    /// <summary>
    /// Tokenizes <paramref name="transcript"/> and injects timestamp slots.
    /// Each word is wrapped: <c>&lt;timestamp&gt; [word tokens] &lt;timestamp&gt;</c>.
    /// </summary>
    /// <param name="transcript">Space-separated transcript (normalised, no leading/trailing whitespace).</param>
    /// <param name="timestampTokenId">Token ID for the <c>&lt;timestamp&gt;</c> special token (151705).</param>
    /// <param name="vocab">BPE vocabulary: BPE symbol string → token id.</param>
    /// <param name="merges">Ordered merge rules as (first, second) pairs. Earlier entries have higher priority.</param>
    /// <returns>
    /// <c>InputIds</c> and <c>AttentionMask</c> (all-ones — no padding; caller may pad later),
    /// and <c>TimestampPositions</c>: indices into the token sequence where a timestamp token was placed.
    /// Positions come in pairs: even index = word start, odd index = word end.
    /// </returns>
    internal static (long[] InputIds, long[] AttentionMask, int[] TimestampPositions) PrepareTokens(
        string transcript,
        long timestampTokenId,
        IReadOnlyDictionary<string, long> vocab,
        IReadOnlyList<(string First, string Second)> merges)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(vocab);
        ArgumentNullException.ThrowIfNull(merges);

        string[] words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Build merge-priority lookup: pair → index in merges list.
        var mergePriority = new Dictionary<(string, string), int>(words.Length * 4);
        for (int i = 0; i < merges.Count; i++)
        {
            (string first, string second) = merges[i];
            mergePriority.TryAdd((first, second), i);
        }

        var ids = new List<long>(words.Length * 6);
        var timestampPositions = new List<int>(words.Length * 2);

        foreach (string word in words)
        {
            // Opening <timestamp>
            timestampPositions.Add(ids.Count);
            ids.Add(timestampTokenId);

            // BPE-encode the word (with a leading space to match GPT-2 word boundary convention)
            string prefixed = '\u0120' + EncodeWordToUnicode(word);
            long[] wordTokenIds = ApplyBpe(prefixed, vocab, mergePriority);
            ids.AddRange(wordTokenIds);

            // Closing <timestamp>
            timestampPositions.Add(ids.Count);
            ids.Add(timestampTokenId);
        }

        long[] inputIds = ids.ToArray();
        long[] attentionMask = new long[inputIds.Length];
        Array.Fill(attentionMask, 1L);

        return (inputIds, attentionMask, timestampPositions.ToArray());
    }

    /// <summary>
    /// Extracts word-level timings from the model's logit output at timestamp positions.
    /// </summary>
    /// <param name="logits">Flat float array of shape [1, seqLen, classCount] in row-major order.</param>
    /// <param name="seqLen">Sequence length (second dimension of logits).</param>
    /// <param name="classCount">Number of timestamp classes (fifth dimension; 5000 for Qwen3-FA).</param>
    /// <param name="timestampPositions">
    /// Positions in the token sequence where timestamp tokens were placed.
    /// Even indices are word starts; odd indices are word ends.
    /// Must have length == 2 * words.Length.
    /// </param>
    /// <param name="words">Word strings in the same order as PrepareTokens was called.</param>
    internal static WordTiming[] ExtractWordTimings(
        float[] logits,
        int seqLen,
        int classCount,
        int[] timestampPositions,
        string[] words)
    {
        ArgumentNullException.ThrowIfNull(logits);
        ArgumentNullException.ThrowIfNull(timestampPositions);
        ArgumentNullException.ThrowIfNull(words);

        if (timestampPositions.Length != words.Length * 2)
        {
            throw new ArgumentException(
                $"timestampPositions length {timestampPositions.Length} must be 2 × words length {words.Length}.",
                nameof(timestampPositions));
        }

        var timings = new WordTiming[words.Length];

        for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
        {
            int startPos = timestampPositions[wordIndex * 2];
            int endPos = timestampPositions[(wordIndex * 2) + 1];

            (int startClass, double startConf) = ArgmaxWithConfidence(logits, seqLen, classCount, startPos);
            (int endClass, double endConf) = ArgmaxWithConfidence(logits, seqLen, classCount, endPos);

            double startSeconds = startClass * SecondsPerTimestampClass;
            double endSeconds = endClass * SecondsPerTimestampClass;

            // Guard against models predicting end ≤ start.
            if (endSeconds <= startSeconds)
            {
                endSeconds = startSeconds + SecondsPerTimestampClass;
            }

            double wordConfidence = (startConf + endConf) / 2.0;

            timings[wordIndex] = new WordTiming(
                words[wordIndex],
                TimeSpan.FromSeconds(startSeconds),
                TimeSpan.FromSeconds(endSeconds),
                wordConfidence);
        }

        return timings;
    }

    /// <summary>
    /// Computes the mean softmax-max confidence across all timestamp positions.
    /// Used to populate <see cref="AlignmentConfidence.Overall"/>.
    /// </summary>
    internal static double ComputeMeanTimestampConfidence(
        float[] logits,
        int seqLen,
        int classCount,
        int[] timestampPositions)
    {
        if (timestampPositions.Length == 0)
        {
            return 0.0;
        }

        double total = 0.0;
        foreach (int pos in timestampPositions)
        {
            (_, double conf) = ArgmaxWithConfidence(logits, seqLen, classCount, pos);
            total += conf;
        }

        return total / timestampPositions.Length;
    }

    // ── BPE helpers ────────────────────────────────────────────────────────────

    /// <summary>Encodes a word's bytes to GPT-2 unicode symbols (no leading-space; caller adds Ġ).</summary>
    private static string EncodeWordToUnicode(string word)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(word);
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            sb.Append(ByteToUnicode[b]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Applies BPE merges to <paramref name="unicodeWord"/> and returns the resulting token IDs.
    /// Falls back to individual byte-tokens or the unknown token (0) for OOV symbols.
    /// </summary>
    private static long[] ApplyBpe(
        string unicodeWord,
        IReadOnlyDictionary<string, long> vocab,
        Dictionary<(string, string), int> mergePriority)
    {
        if (string.IsNullOrEmpty(unicodeWord))
        {
            return [];
        }

        // Initialise symbols as individual characters (each char is a BPE atom).
        var symbols = new List<string>(unicodeWord.Length);
        foreach (char c in unicodeWord)
        {
            symbols.Add(c.ToString());
        }

        // Iteratively find and apply the highest-priority (lowest-index) merge.
        while (symbols.Count > 1)
        {
            int bestIndex = int.MaxValue;
            int bestPos = -1;

            for (int i = 0; i < symbols.Count - 1; i++)
            {
                if (mergePriority.TryGetValue((symbols[i], symbols[i + 1]), out int priority) && priority < bestIndex)
                {
                    bestIndex = priority;
                    bestPos = i;
                }
            }

            if (bestPos < 0)
            {
                break;
            }

            string merged = symbols[bestPos] + symbols[bestPos + 1];
            symbols[bestPos] = merged;
            symbols.RemoveAt(bestPos + 1);
        }

        // Map BPE symbols to token IDs.
        var ids = new long[symbols.Count];
        for (int i = 0; i < symbols.Count; i++)
        {
            ids[i] = vocab.TryGetValue(symbols[i], out long id) ? id : 0L;
        }

        return ids;
    }

    // ── Logit helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns (argmaxClass, softmax probability of argmax class) at the given sequence position.
    /// Uses numerically stable softmax (subtract max before exp).
    /// </summary>
    private static (int ArgmaxClass, double SoftmaxProb) ArgmaxWithConfidence(
        float[] logits,
        int seqLen,
        int classCount,
        int sequencePosition)
    {
        if (sequencePosition < 0 || sequencePosition >= seqLen)
        {
            return (0, 0.0);
        }

        int offset = sequencePosition * classCount;
        int bestClass = 0;
        float bestLogit = logits[offset];

        for (int c = 1; c < classCount; c++)
        {
            float v = logits[offset + c];
            if (v > bestLogit)
            {
                bestLogit = v;
                bestClass = c;
            }
        }

        // Numerically stable softmax: sum exp(logit - max), then divide.
        double sumExp = 0.0;
        for (int c = 0; c < classCount; c++)
        {
            sumExp += Math.Exp(logits[offset + c] - bestLogit);
        }

        double confidence = sumExp > 0.0 ? 1.0 / sumExp : 0.0;
        return (bestClass, confidence);
    }

    // ── GPT-2 byte-to-unicode table ────────────────────────────────────────────

    private static char[] BuildByteToUnicodeTable()
    {
        // The GPT-2 byte-to-unicode mapping assigns a printable Unicode character
        // to every byte value 0-255. Bytes already in the printable ASCII range
        // (and two Latin-1 supplement bands) map to themselves; the remaining
        // 68 bytes map sequentially starting at codepoint 256 (Ā).
        var table = new char[256];
        int next = 256;
        for (int b = 0; b < 256; b++)
        {
            bool selfMapped =
                (b >= 33 && b <= 126) ||   // ! … ~
                (b >= 161 && b <= 172) ||  // ¡ … ¬
                (b >= 174 && b <= 255);    // ® … ÿ

            table[b] = selfMapped ? (char)b : (char)next++;
        }

        // Codepoint 160 (non-breaking space) is NOT in the self-mapped bands
        // and gets mapped to a sequential codepoint above 256.
        // The loop above handles it correctly since it falls in the else branch.
        // Space (32) maps to Ġ (U+0120), which lands in the sequential range.
        return table;
    }
}
