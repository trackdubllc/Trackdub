using Trackdub.Domain;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Transcripts;

public enum TranslatedWordAlignmentOutcomeKind
{
    Succeeded = 1,
    Unavailable = 2,
    Failed = 3
}

public sealed record TranslatedWordAlignmentRequest(
    TranscriptSegment SourceSegment,
    TranslatedSegment TranslatedSegment,
    string SourceLanguage,
    string TargetLanguage,
    string? PreferredModelAlias = null,
    ExecutionProviderKind? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null);

public sealed record TranslatedWordAlignmentResult(
    TranslatedWordAlignmentOutcomeKind Outcome,
    IReadOnlyList<TranslatedWord> Words,
    string? Detail = null)
{
    public static TranslatedWordAlignmentResult Succeeded(IReadOnlyList<TranslatedWord> words) =>
        new(TranslatedWordAlignmentOutcomeKind.Succeeded, words);

    public static TranslatedWordAlignmentResult Unavailable(string detail) =>
        new(TranslatedWordAlignmentOutcomeKind.Unavailable, [], detail);

    public static TranslatedWordAlignmentResult Failed(string detail) =>
        new(TranslatedWordAlignmentOutcomeKind.Failed, [], detail);
}

public interface ITranslatedWordAlignmentService
{
    Task<TranslatedWordAlignmentResult> AlignAsync(
        TranslatedWordAlignmentRequest request,
        CancellationToken cancellationToken);
}

public sealed class UnavailableTranslatedWordAlignmentService : ITranslatedWordAlignmentService
{
    public Task<TranslatedWordAlignmentResult> AlignAsync(
        TranslatedWordAlignmentRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(TranslatedWordAlignmentResult.Unavailable(
            "Translated word timing alignment is not configured."));
}
