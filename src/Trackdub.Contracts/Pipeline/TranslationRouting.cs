namespace Trackdub.Contracts.Pipeline;

public enum TranslationRoutingKind
{
    Direct = 1,
    Pivot = 2,
    Unavailable = 3
}

public sealed record TranslationTargetLanguageOption(
    string LanguageCode,
    string DisplayName,
    TranslationRoutingKind RoutingKind,
    bool IsAvailable,
    string Detail)
{
    /// <summary>Language name only; routing <see cref="Detail"/> is for logs and pipeline, not UI labels.</summary>
    public string DisplayLabel => DisplayName;
}

public sealed record TranslationRouteSelection(
    string SourceLanguage,
    string TargetLanguage,
    TranslationRoutingKind RoutingKind,
    bool IsAvailable,
    string ProviderName,
    string RouteDetail,
    string? ModelId = null,
    string? PreferredModelAlias = null,
    string? ResolvedModelEntryPath = null,
    string? UnavailableReason = null,
    string? EngineFamily = null);

public sealed record TranslationExecutionMetadata(
    string ProviderName,
    string? ModelId,
    string? ModelAlias,
    string? SelectedExecutionProvider,
    TranslationRoutingKind RoutingKind);

public interface ITranslationLanguageRouter
{
    Task<IReadOnlyList<TranslationTargetLanguageOption>> GetSupportedTargetLanguagesAsync(
        string sourceLanguage,
        CancellationToken cancellationToken);

    Task<TranslationRouteSelection> ResolveRouteAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        string? preferredModelAlias = null);
}

public interface ITranslationExecutionMetadataReporter
{
    TranslationExecutionMetadata? LastExecutionMetadata { get; }
}
