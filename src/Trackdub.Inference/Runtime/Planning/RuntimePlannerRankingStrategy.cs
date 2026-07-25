using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Runtime.Planning;

internal sealed class RuntimePlannerRankingStrategy(BundledModelManifestRegistry manifestRegistry)
{
    private readonly BundledModelManifestRegistry manifestRegistry = manifestRegistry ?? throw new ArgumentNullException(nameof(manifestRegistry));

    public RankedManifestEntry[] RankEntries(
        StageRuntimePlanningRequest request,
        StageRuntimeRequirements requirements)
    {
        return manifestRegistry.Entries
            .Where(entry => string.Equals(entry.Task, requirements.RequiredTask.ToManifestValue(), StringComparison.OrdinalIgnoreCase))
            .Where(entry => IsEngineFamilyAllowed(entry, requirements))
            .Where(entry => HasRequiredCapabilities(entry, requirements))
            .Where(entry => IsLanguageCompatible(entry, request))
            .Select(entry => new RankedManifestEntry(
                entry,
                GetSelectionRank(entry, request, requirements)))
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Entry.Aliases.FirstOrDefault() ?? candidate.Entry.ModelId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Entry.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsEngineFamilyAllowed(
        BundledModelManifestEntry entry,
        StageRuntimeRequirements requirements) =>
        requirements.AllowedEngineFamilies is null ||
        requirements.AllowedEngineFamilies.Count == 0 ||
        requirements.AllowedEngineFamilies.Any(engineFamily =>
            entry.EngineFamily.Equals(engineFamily, StringComparison.OrdinalIgnoreCase));

    private static bool HasRequiredCapabilities(
        BundledModelManifestEntry entry,
        StageRuntimeRequirements requirements) =>
        requirements.RequiredCapabilities is null ||
        requirements.RequiredCapabilities.Count == 0 ||
        requirements.RequiredCapabilities.All(requiredCapability =>
            entry.Capabilities.Any(capability =>
                capability.Equals(requiredCapability, StringComparison.OrdinalIgnoreCase)));

    private static bool IsLanguageCompatible(
        BundledModelManifestEntry entry,
        StageRuntimePlanningRequest request)
    {
        if (string.Equals(entry.Task, ModelTask.Asr.ToManifestValue(), StringComparison.OrdinalIgnoreCase))
        {
            return IsAsrLanguageCompatible(entry, request);
        }

        if (!string.Equals(entry.Task, ModelTask.Translation.ToManifestValue(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(request.SourceLanguage) ||
            string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            return true;
        }

        string sourceLanguage = request.SourceLanguage.Trim();
        string targetLanguage = request.TargetLanguage.Trim();
        if (entry.LanguageCoverage.LanguagePairs.Count > 0)
        {
            return entry.LanguageCoverage.LanguagePairs.Any(pair =>
                string.Equals(pair.SourceLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase));
        }

        if (entry.LanguageCoverage.SourceLanguages.Count == 0 &&
            entry.LanguageCoverage.TargetLanguages.Count == 0)
        {
            return true;
        }

        return CoversLanguage(entry.LanguageCoverage.SourceLanguages, sourceLanguage) &&
               CoversLanguage(entry.LanguageCoverage.TargetLanguages, targetLanguage);
    }

    private static bool IsAsrLanguageCompatible(
        BundledModelManifestEntry entry,
        StageRuntimePlanningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceLanguage))
        {
            return DeclaresAsrLanguageDetection(entry);
        }

        string sourceLanguage = NormalizeAsrSourceLanguage(request.SourceLanguage.Trim()) ?? request.SourceLanguage.Trim();
        if (sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return DeclaresAsrLanguageDetection(entry);
        }

        return entry.LanguageCoverage.SourceLanguages.Count == 0 ||
               CoversLanguage(entry.LanguageCoverage.SourceLanguages, sourceLanguage);
    }

    private static bool DeclaresAsrLanguageDetection(BundledModelManifestEntry entry) =>
        entry.Capabilities.Any(capability =>
            capability.Equals("language-detection", StringComparison.OrdinalIgnoreCase)) ||
        entry.LanguageCoverage.SourceLanguages.Any(language =>
            language.Equals("auto", StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeAsrSourceLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        string normalized = languageCode.Trim().ToLowerInvariant().Replace('_', '-');
        int separatorIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            normalized = normalized[..separatorIndex];
        }

        return normalized.Length is >= 2 and <= 8 &&
               normalized.All(static character => character is >= 'a' and <= 'z')
            ? normalized
            : null;
    }

    private static bool CoversLanguage(
        IReadOnlyList<string> languages,
        string language) =>
        languages.Any(candidate =>
            string.Equals(candidate, language, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "multi", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "auto", StringComparison.OrdinalIgnoreCase));

    public static RankedManifestEntry[] FilterPreferredModelAliasEntries(
        StageRuntimePlanningRequest request,
        IReadOnlyList<RankedManifestEntry> rankedEntries)
    {
        if (!request.RequirePreferredModelAlias)
        {
            return rankedEntries.ToArray();
        }

        if (request.NormalizedPreferredModelAlias is not string preferredModelAlias)
        {
            return [];
        }

        return rankedEntries
            .Where(candidate => ManifestEntryMatchesPreferredAlias(candidate.Entry, preferredModelAlias))
            .ToArray();
    }

    public static RankedManifestEntry[] FilterTopRankedEntriesIfRequired(
        StageRuntimeRequirements requirements,
        IReadOnlyList<RankedManifestEntry> rankedEntries)
    {
        if (!requirements.PreferTopRankedModelUntilReady || rankedEntries.Count == 0)
        {
            return rankedEntries.ToArray();
        }

        int topRank = rankedEntries.Min(candidate => candidate.Rank);
        return rankedEntries
            .Where(candidate => candidate.Rank == topRank)
            .ToArray();
    }

    private static int GetSelectionRank(
        BundledModelManifestEntry entry,
        StageRuntimePlanningRequest request,
        StageRuntimeRequirements requirements)
    {
        int rank = 0;

        if (!string.IsNullOrWhiteSpace(request.NormalizedPreferredModelAlias))
        {
            if (ManifestEntryMatchesPreferredAlias(entry, request.NormalizedPreferredModelAlias))
            {
                return 0;
            }

            rank += 10_000;
        }

        if (!string.IsNullOrWhiteSpace(request.PreferredEngineFamily) &&
            !entry.EngineFamily.Equals(request.PreferredEngineFamily.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            rank += 1_000;
        }

        if (!string.IsNullOrWhiteSpace(request.PreferredModelTier) &&
            !entry.Tier.Equals(request.PreferredModelTier.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            rank += 100;
        }

        int bestAliasRank = int.MaxValue / 2;
        for (int index = 0; index < requirements.PreferredModelAliases.Count; index++)
        {
            string preferredAlias = requirements.PreferredModelAliases[index];
            if (entry.Aliases.Any(alias => alias.Equals(preferredAlias, StringComparison.OrdinalIgnoreCase)))
            {
                bestAliasRank = Math.Min(bestAliasRank, index + 1);
            }
        }

        return rank + bestAliasRank;
    }

    private static bool ManifestEntryMatchesPreferredAlias(
        BundledModelManifestEntry entry,
        string preferredModelAlias) =>
        entry.Aliases.Any(alias => alias.Equals(preferredModelAlias, StringComparison.OrdinalIgnoreCase)) ||
        entry.ModelId.Equals(preferredModelAlias, StringComparison.OrdinalIgnoreCase);
}

internal sealed record RankedManifestEntry(
    BundledModelManifestEntry Entry,
    int Rank);
