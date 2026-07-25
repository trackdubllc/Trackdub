namespace Trackdub.Contracts.Pipeline;

public interface ITranslationEngine
{
    Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}
