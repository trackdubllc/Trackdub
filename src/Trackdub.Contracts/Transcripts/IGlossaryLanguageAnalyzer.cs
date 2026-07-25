namespace Trackdub.Contracts.Transcripts;

public interface IGlossaryLanguageAnalyzer
{
    IReadOnlySet<string> SupportedSourceLanguages { get; }

    bool Supports(string sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return false;
        }

        return SupportedSourceLanguages.Contains(sourceLanguage.Trim().ToLowerInvariant());
    }

    IReadOnlyList<GlossaryAnalysisToken> Analyze(
        string sourceLanguage,
        string text);
}
