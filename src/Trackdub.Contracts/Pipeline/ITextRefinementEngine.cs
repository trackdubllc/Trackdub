namespace Trackdub.Contracts.Pipeline;

public interface ITextRefinementEngine
{
    string EngineFamily { get; }

    Task<IReadOnlyList<RefinedTextSegment>> RefineAsync(
        TextRefinementRequest request,
        CancellationToken cancellationToken);
}

public sealed record TextRefinementRequest(
    IReadOnlyList<TextRefinementInputSegment> Segments,
    TextRefinementScope Scope = TextRefinementScope.Asr,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    string? PreferredModelAlias = null,
    string? PreferredExecutionProvider = null,
    bool RequirePreferredModelAlias = false,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null,
    InferenceRequestOptions? Options = null);

public sealed record TextRefinementInputSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string Text);

public sealed record RefinedTextSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string OriginalText,
    string RefinedText,
    string DisplayedText,
    bool Accepted,
    TextRefinementGuardStatus GuardStatus,
    IReadOnlyList<string> AppliedCorrections);
