namespace Trackdub.Domain.Translation;

public sealed record GlossaryEntry(
    Guid Id,
    Guid ProjectId,
    string SourceLanguage,
    string TargetLanguage,
    string SourceTerm,
    string TargetTerm,
    bool IsCaseSensitive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static GlossaryEntry Create(
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        string sourceTerm,
        string targetTerm,
        bool isCaseSensitive,
        DateTimeOffset createdAtUtc)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (GlossaryScopeIds.IsGlobalScope(projectId))
        {
            throw new ArgumentException("Use CreateGlobal for global glossary entries.", nameof(projectId));
        }

        return CreateEntry(
            Guid.NewGuid(),
            projectId,
            sourceLanguage,
            targetLanguage,
            sourceTerm,
            targetTerm,
            isCaseSensitive,
            createdAtUtc,
            createdAtUtc);
    }

    public static GlossaryEntry CreateGlobal(
        string sourceLanguage,
        string targetLanguage,
        string sourceTerm,
        string targetTerm,
        bool isCaseSensitive,
        DateTimeOffset createdAtUtc) =>
        CreateGlobal(
            Guid.NewGuid(),
            sourceLanguage,
            targetLanguage,
            sourceTerm,
            targetTerm,
            isCaseSensitive,
            createdAtUtc,
            createdAtUtc);

    public static GlossaryEntry CreateGlobal(
        Guid id,
        string sourceLanguage,
        string targetLanguage,
        string sourceTerm,
        string targetTerm,
        bool isCaseSensitive,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc) =>
        CreateEntry(
            id,
            GlossaryScopeIds.Global,
            sourceLanguage,
            targetLanguage,
            sourceTerm,
            targetTerm,
            isCaseSensitive,
            createdAtUtc,
            updatedAtUtc);

    public static void ValidateGlobalScope(GlossaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!GlossaryScopeIds.IsGlobalScope(entry.ProjectId))
        {
            throw new ArgumentException("Global glossary entries must use the global scope id.", nameof(entry));
        }
    }

    public static void ValidateProjectScope(Guid projectId)
    {
        if (projectId == Guid.Empty || GlossaryScopeIds.IsGlobalScope(projectId))
        {
            throw new ArgumentException("Project glossary entries require a project id.", nameof(projectId));
        }
    }

    private static GlossaryEntry CreateEntry(
        Guid id,
        Guid projectId,
        string sourceLanguage,
        string targetLanguage,
        string sourceTerm,
        string targetTerm,
        bool isCaseSensitive,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage, nameof(sourceLanguage));
        string normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage, nameof(targetLanguage));
        string normalizedSourceTerm = NormalizeTerm(sourceTerm, nameof(sourceTerm));
        string normalizedTargetTerm = NormalizeTerm(targetTerm, nameof(targetTerm));

        return new GlossaryEntry(
            id,
            projectId,
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            normalizedSourceTerm,
            normalizedTargetTerm,
            isCaseSensitive,
            createdAtUtc,
            updatedAtUtc);
    }

    public static string NormalizeLanguageCode(string? languageCode, string paramName)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            throw new ArgumentException("Language code is required.", paramName);
        }

        return languageCode.Trim().ToLowerInvariant();
    }

    private static string NormalizeTerm(string? term, string paramName)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            throw new ArgumentException("Glossary term is required.", paramName);
        }

        return term.Trim();
    }
}
