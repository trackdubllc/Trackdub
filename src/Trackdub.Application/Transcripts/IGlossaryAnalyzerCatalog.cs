namespace Trackdub.Application.Transcripts;

public interface IGlossaryAnalyzerCatalog
{
    IGlossaryLanguageAnalyzer? Resolve(string sourceLanguage);
}

public sealed class GlossaryAnalyzerCatalog : IGlossaryAnalyzerCatalog
{
    public static readonly IGlossaryAnalyzerCatalog Empty = new GlossaryAnalyzerCatalog([]);

    private readonly IReadOnlyList<IGlossaryLanguageAnalyzer> analyzers;

    public GlossaryAnalyzerCatalog(IEnumerable<IGlossaryLanguageAnalyzer> analyzers)
    {
        ArgumentNullException.ThrowIfNull(analyzers);
        this.analyzers = analyzers.ToArray();
    }

    public IGlossaryLanguageAnalyzer? Resolve(string sourceLanguage) =>
        analyzers.FirstOrDefault(analyzer => analyzer.Supports(sourceLanguage));
}
