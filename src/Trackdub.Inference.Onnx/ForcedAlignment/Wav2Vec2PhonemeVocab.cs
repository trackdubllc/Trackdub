using System.Text.Json;

namespace Trackdub.Inference.Onnx.ForcedAlignment;

/// <summary>
/// Loads a <c>vocab.json</c> file of the form <c>{ "symbol": index, ... }</c> and
/// provides O(1) bidirectional lookups between symbol strings and integer indices.
/// All I/O is performed once in the constructor; inference-time calls are allocation-free.
/// </summary>
public sealed class Wav2Vec2PhonemeVocab
{
    private readonly string?[] _indexToSymbol;
    private readonly Dictionary<string, int> _symbolToIndex;

    public Wav2Vec2PhonemeVocab(string vocabFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabFilePath);

        string json = File.ReadAllText(vocabFilePath);
        Dictionary<string, int>? raw = JsonSerializer.Deserialize<Dictionary<string, int>>(json)
            ?? throw new InvalidOperationException(
                $"Vocab file deserialised to null: '{vocabFilePath}'.");

        if (raw.Count == 0)
            throw new InvalidOperationException($"Vocab file contains no entries: '{vocabFilePath}'.");

        int maxIndex = 0;
        foreach (int idx in raw.Values)
        {
            if (idx > maxIndex) maxIndex = idx;
        }

        _indexToSymbol = new string?[maxIndex + 1];
        _symbolToIndex = new Dictionary<string, int>(raw.Count, StringComparer.Ordinal);

        foreach ((string symbol, int index) in raw)
        {
            if (index >= 0 && index < _indexToSymbol.Length)
                _indexToSymbol[index] = symbol;
            _symbolToIndex[symbol] = index;
        }

        WordBoundaryIndex = _symbolToIndex.TryGetValue(WordBoundarySymbol, out int wbIdx)
            ? wbIdx
            : -1;
    }

    public int BlankIndex => 0;
    public string WordBoundarySymbol => "|";
    public int WordBoundaryIndex { get; }
    public int Count => _indexToSymbol.Length;

    public bool TryGetSymbol(int index, out string symbol)
    {
        if (index < 0 || index >= _indexToSymbol.Length)
        {
            symbol = string.Empty;
            return false;
        }

        string? s = _indexToSymbol[index];
        if (s is null)
        {
            symbol = string.Empty;
            return false;
        }

        symbol = s;
        return true;
    }

    public bool TryGetIndex(string symbol, out int index) =>
        _symbolToIndex.TryGetValue(symbol, out index);
}
