namespace Trackdub.Contracts.Pipeline;

public sealed record InferenceRequestOptions(
    string? PreferredModelAlias = null,
    bool RequirePreferredModelAlias = false,
    string? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null)
{
    public static InferenceRequestOptions Default { get; } = new();

    public string? NormalizedPreferredModelAlias =>
        string.IsNullOrWhiteSpace(PreferredModelAlias)
            ? null
            : PreferredModelAlias.Trim();

    public string? NormalizedPreferredModelVariantAlias =>
        string.IsNullOrWhiteSpace(PreferredModelVariantAlias)
            ? null
            : PreferredModelVariantAlias.Trim();
}
