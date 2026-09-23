namespace Trackdub.Inference.Onnx.ParakeetTdt;

/// <summary>
/// NeMo <c>vocab.txt</c> ("piece id" per line). The joint emits one logit per entry and the
/// last entry is the TDT blank (<c>&lt;blk&gt;</c>), followed by the duration logits.
/// </summary>
internal sealed class ParakeetTdtVocab
{
    private readonly string[] pieces;

    private ParakeetTdtVocab(string[] pieces)
    {
        this.pieces = pieces;
    }

    public int Count => pieces.Length;

    public int BlankId => pieces.Length - 1;

    public static async Task<ParakeetTdtVocab> LoadAsync(string vocabPath, CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync(vocabPath, cancellationToken).ConfigureAwait(false);
        var pieces = new string[lines.Length];
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int separator = line.LastIndexOf(' ');
            if (separator <= 0 || !int.TryParse(line.AsSpan(separator + 1), out int id) || id != index)
            {
                throw new InvalidOperationException($"Parakeet vocab line {index + 1} is not '<piece> {index}'.");
            }

            pieces[index] = line[..separator];
        }

        if (pieces.Length == 0 || pieces[^1] != "<blk>")
        {
            throw new InvalidOperationException("Parakeet vocab must end with the <blk> blank token.");
        }

        return new ParakeetTdtVocab(pieces);
    }

    public string Piece(int tokenId) => pieces[tokenId];

    /// <summary>Control pieces such as &lt;unk&gt;, &lt;pad&gt;, &lt;|nospeech|&gt; never reach transcript text.</summary>
    public bool IsControl(int tokenId)
    {
        string piece = pieces[tokenId];
        return piece.Length > 2 && piece[0] == '<' && piece[^1] == '>';
    }
}
