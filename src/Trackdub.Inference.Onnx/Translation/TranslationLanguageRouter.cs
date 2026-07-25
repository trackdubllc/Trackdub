using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Translation;

public sealed class TranslationLanguageRouter(
    BundledModelManifestRegistry manifestRegistry,
    IModelCacheInventory modelCacheInventory,
    IRuntimePlanner? runtimePlanner = null)
    : ITranslationLanguageRouter
{
    private readonly BundledModelManifestRegistry manifestRegistry = manifestRegistry ?? throw new ArgumentNullException(nameof(manifestRegistry));
    private readonly IModelCacheInventory modelCacheInventory = modelCacheInventory ?? throw new ArgumentNullException(nameof(modelCacheInventory));

    // Routing through IRuntimePlanner lets the router share the same planning rules
    // (model integrity, smoke tests, EP availability) as the rest of the pipeline.
    // Optional only so existing test doubles that build the router without DI continue to compile;
    // the production composition root always supplies a planner.
    private readonly IRuntimePlanner? runtimePlanner = runtimePlanner;

    public async Task<IReadOnlyList<TranslationTargetLanguageOption>> GetSupportedTargetLanguagesAsync(
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        TranslationRoutingContext context = await BuildContextAsync(cancellationToken).ConfigureAwait(false);
        return TranslationLanguageCoverageMatrix.GetTargets(sourceLanguage)
            .Select(language =>
            {
                TranslationRouteSelection route = ResolveRoute(
                    context,
                    sourceLanguage,
                    language.Code,
                    language.DisplayName);
                return new TranslationTargetLanguageOption(
                    language.Code,
                    language.DisplayName,
                    route.RoutingKind,
                    route.IsAvailable,
                    route.IsAvailable
                        ? route.RouteDetail
                        : route.UnavailableReason ?? route.RouteDetail);
            })
            .ToArray();
    }

    public async Task<TranslationRouteSelection> ResolveRouteAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        string? preferredModelAlias = null)
    {
        TranslationRoutingContext context = await BuildContextAsync(cancellationToken).ConfigureAwait(false);
        return ResolveRoute(context, sourceLanguage, targetLanguage, displayName: null, preferredModelAlias);
    }

    private async Task<TranslationRoutingContext> BuildContextAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalModelCacheRecord> cacheRecords = await modelCacheInventory.LoadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, IReadOnlyList<LocalModelCacheRecord>> cacheIndex = cacheRecords
            .GroupBy(record => record.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LocalModelCacheRecord>)group
                    .OrderByDescending(record => record.CachedAtUtc)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        Dictionary<(string SourceLanguage, string TargetLanguage), IReadOnlyList<BundledModelManifestEntry>> directEntriesByPair = manifestRegistry.Entries
            .Where(static entry => string.Equals(entry.Task, "translation", StringComparison.OrdinalIgnoreCase) &&
                                   IsDirectTranslationEntry(entry))
            .SelectMany(entry => EnumerateDirectPairs(entry).Select(pair => (Entry: entry, Pair: pair)))
            .GroupBy(candidate => candidate.Pair)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<BundledModelManifestEntry>)group.Select(candidate => candidate.Entry).ToArray());

        BundledModelManifestEntry[] pivotEntries = manifestRegistry.Entries.Where(entry =>
            string.Equals(entry.Task, "translation", StringComparison.OrdinalIgnoreCase) &&
            IsPivotTranslationEntry(entry))
            .ToArray();

        IReadOnlyDictionary<string, bool> planAvailabilityByModelId = await BuildPlanAvailabilityAsync(
            directEntriesByPair.Values.SelectMany(static entries => entries).Concat(pivotEntries),
            cancellationToken).ConfigureAwait(false);

        return new TranslationRoutingContext(
            cacheIndex,
            directEntriesByPair,
            pivotEntries,
            planAvailabilityByModelId);
    }

    /// <summary>
    /// Asks the shared <see cref="IRuntimePlanner"/> whether each candidate translation model
    /// is currently runnable. The router only marks a route as available when the planner
    /// agrees, so model integrity checks, EP availability, and smoke tests are honored
    /// uniformly with the rest of the pipeline.
    /// When no planner is configured (legacy test wiring) the router falls back to the
    /// inline cache+file checks below.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, bool>> BuildPlanAvailabilityAsync(
        IEnumerable<BundledModelManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        if (runtimePlanner is null)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        var availability = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (BundledModelManifestEntry entry in entries.DistinctBy(e => e.ModelId, StringComparer.OrdinalIgnoreCase))
        {
            string? primaryAlias = entry.Aliases.FirstOrDefault();
            try
            {
                StageRuntimePlan plan = await runtimePlanner.PlanAsync(
                    new StageRuntimePlanningRequest(
                        Stage: RuntimeStage.Translation,
                        PreferredModelAlias: primaryAlias,
                        PreferredEngineFamily: entry.EngineFamily,
                        RequirePreferredModelAlias: !string.IsNullOrWhiteSpace(primaryAlias)),
                    cancellationToken).ConfigureAwait(false);
                availability[entry.ModelId] = plan.IsRunnable();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Planner failure is treated as "not runnable" — a strictly more conservative
                // outcome than silently masking an underlying problem with the planner stack.
                availability[entry.ModelId] = false;
            }
        }

        return availability;
    }

    private bool IsPlanRunnableOrUngated(TranslationRoutingContext context, BundledModelManifestEntry entry)
    {
        // When no planner is configured, fall back to the inline file checks each TryResolve
        // method already performs. With a planner present, route availability requires the
        // planner to confirm the model is in a runnable status (Ready or Verified).
        if (runtimePlanner is null)
        {
            return true;
        }

        return context.PlanAvailabilityByModelId.TryGetValue(entry.ModelId, out bool isRunnable) && isRunnable;
    }

    private TranslationRouteSelection ResolveRoute(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage,
        string? displayName,
        string? preferredModelAlias = null)
    {
        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage)
            ?? throw new InvalidOperationException("Source language is required.");
        string normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage)
            ?? throw new InvalidOperationException("Target language is required.");

        if (!TranslationLanguageCoverageMatrix.TryGetLanguage(normalizedTargetLanguage, out TranslationLanguageDefinition? targetDefinition) ||
            !TranslationLanguageCoverageMatrix.GetTargets(normalizedSourceLanguage)
                .Any(candidate => string.Equals(candidate.Code, normalizedTargetLanguage, StringComparison.Ordinal)))
        {
            return new TranslationRouteSelection(
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                TranslationRoutingKind.Unavailable,
                IsAvailable: false,
                ProviderName: "none",
                RouteDetail: "Unavailable",
                UnavailableReason: $"Milestone 9 coverage does not include {normalizedSourceLanguage} -> {normalizedTargetLanguage}.");
        }

        string resolvedDisplayName = displayName ?? targetDefinition!.DisplayName;
        if (TryResolvePreferredRoute(
            context,
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            preferredModelAlias,
            out TranslationRouteSelection? preferredRoute))
        {
            return preferredRoute!;
        }

        if (TryResolveDirectRoute(context, normalizedSourceLanguage, normalizedTargetLanguage, out TranslationRouteSelection? directRoute))
        {
            return directRoute!;
        }

        if (TryResolvePivotRoute(context, normalizedSourceLanguage, normalizedTargetLanguage, out TranslationRouteSelection? pivotRoute))
        {
            return pivotRoute!;
        }

        if (TryResolveGenAiPivotRoute(context, normalizedSourceLanguage, normalizedTargetLanguage, out TranslationRouteSelection? genAiPivotRoute))
        {
            return genAiPivotRoute!;
        }

        string pivotLabel = ResolveUnavailablePivotLabel(context.PivotEntries);
        string missingReason = context.DirectEntriesByPair.ContainsKey((normalizedSourceLanguage, normalizedTargetLanguage))
            ? $"Direct translation is not installed for {resolvedDisplayName}, and {pivotLabel} is unavailable."
            : $"{pivotLabel} is unavailable for {resolvedDisplayName}.";

        return new TranslationRouteSelection(
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            TranslationRoutingKind.Unavailable,
            IsAvailable: false,
            ProviderName: "none",
            RouteDetail: "Unavailable",
            UnavailableReason: missingReason);
    }

    private static bool HasAlias(BundledModelManifestEntry entry, string alias) =>
        entry.Aliases.Any(candidate => candidate.Equals(alias.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool TryResolvePreferredRoute(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage,
        string? preferredModelAlias,
        out TranslationRouteSelection? route)
    {
        route = null;
        if (string.IsNullOrWhiteSpace(preferredModelAlias))
        {
            return false;
        }

        foreach (BundledModelManifestEntry entry in GetDirectEntriesForPair(context, sourceLanguage, targetLanguage)
                     .Concat(GetPivotEntriesForPair(context, sourceLanguage, targetLanguage))
                     .Where(entry => HasAlias(entry, preferredModelAlias)))
        {
            if (HasDirectPair(entry, sourceLanguage, targetLanguage) &&
                TryResolveDirectRoute(context, sourceLanguage, targetLanguage, entry, out route))
            {
                return true;
            }

            if (IsPivotTranslationEntry(entry) &&
                TryResolvePivotRoute(context, sourceLanguage, targetLanguage, entry, out route))
            {
                return true;
            }

            if (string.Equals(entry.EngineFamily, "phi-genai", StringComparison.OrdinalIgnoreCase) &&
                TryResolveGenAiPivotRoute(context, sourceLanguage, targetLanguage, entry, out route))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveDirectRoute(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage,
        out TranslationRouteSelection? route)
    {
        route = null;
        foreach (BundledModelManifestEntry entry in GetDirectEntriesForPair(context, sourceLanguage, targetLanguage))
        {
            if (TryResolveDirectRoute(context, sourceLanguage, targetLanguage, entry, out route))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveDirectRoute(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage,
        BundledModelManifestEntry entry,
        out TranslationRouteSelection? route)
    {
        route = null;
        if (!IsPlanRunnableOrUngated(context, entry))
        {
            return false;
        }

        foreach (string entryPath in EnumerateResolvedEntryPaths(context.CacheIndex, entry))
        {
            string modelRootPath = ResolveOpusModelRootPath(entryPath);
            if (!HasOpusSupportingFiles(modelRootPath))
            {
                continue;
            }

            route = new TranslationRouteSelection(
                sourceLanguage,
                targetLanguage,
                TranslationRoutingKind.Direct,
                IsAvailable: true,
                ProviderName: ResolveProviderName(entry),
                RouteDetail: ResolveDirectRouteDetail(entry),
                ModelId: entry.ModelId,
                PreferredModelAlias: entry.Aliases.FirstOrDefault(),
                ResolvedModelEntryPath: entryPath,
                EngineFamily: entry.EngineFamily);
            return true;
        }

        return false;
    }

    private bool TryResolvePivotRoute(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage,
        out TranslationRouteSelection? route)
    {
        route = null;
        foreach (BundledModelManifestEntry entry in GetPivotEntriesForPair(context, sourceLanguage, targetLanguage))
        {
            if (TryResolvePivotRoute(context, sourceLanguage, targetLanguage, entry, out route))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolvePivotRoute(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage,
        BundledModelManifestEntry entry,
        out TranslationRouteSelection? route)
    {
        route = null;
        if (!IsPlanRunnableOrUngated(context, entry))
        {
            return false;
        }

        foreach (string entryPath in EnumerateResolvedMadladEntryPaths(context.CacheIndex, entry))
        {
            string modelRootPath = Path.GetDirectoryName(entryPath)
                ?? throw new InvalidOperationException("MADLAD model root path could not be resolved.");
            if (!HasMadladSupportingFiles(modelRootPath))
            {
                continue;
            }

            string routeDetail = ResolvePivotRouteDetail(
                entry,
                context.DirectEntriesByPair.ContainsKey((sourceLanguage, targetLanguage)));

            route = new TranslationRouteSelection(
                sourceLanguage,
                targetLanguage,
                TranslationRoutingKind.Pivot,
                IsAvailable: true,
                ProviderName: ResolveProviderName(entry),
                RouteDetail: routeDetail,
                ModelId: entry.ModelId,
                PreferredModelAlias: entry.Aliases.FirstOrDefault(),
                ResolvedModelEntryPath: entryPath,
                EngineFamily: entry.EngineFamily);
            return true;
        }

        return false;
    }

    private bool TryResolveGenAiPivotRoute(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage,
        out TranslationRouteSelection? route)
    {
        route = null;
        foreach (BundledModelManifestEntry entry in context.PivotEntries
            .Where(static e => string.Equals(e.EngineFamily, "phi-genai", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryResolveGenAiPivotRoute(context, sourceLanguage, targetLanguage, entry, out route))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveGenAiPivotRoute(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage,
        BundledModelManifestEntry entry,
        out TranslationRouteSelection? route)
    {
        route = null;
        if (!IsPlanRunnableOrUngated(context, entry))
        {
            return false;
        }

        foreach (string entryPath in EnumerateResolvedEntryPaths(context.CacheIndex, entry))
        {
            string? modelRootPath = Path.GetDirectoryName(entryPath);
            if (modelRootPath is null || !HasGenAiSupportingFiles(modelRootPath))
            {
                continue;
            }

            route = new TranslationRouteSelection(
                sourceLanguage,
                targetLanguage,
                TranslationRoutingKind.Pivot,
                IsAvailable: true,
                ProviderName: entry.EngineFamily,
                RouteDetail: ResolvePivotRouteLabel(entry),
                ModelId: entry.ModelId,
                PreferredModelAlias: entry.Aliases.FirstOrDefault(),
                ResolvedModelEntryPath: entryPath,
                EngineFamily: entry.EngineFamily);
            return true;
        }

        return false;
    }

    private static bool HasGenAiSupportingFiles(string modelRootPath) =>
        File.Exists(Path.Combine(modelRootPath, "genai_config.json"));

    private static IReadOnlyList<BundledModelManifestEntry> GetDirectEntriesForPair(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage) =>
        context.DirectEntriesByPair.TryGetValue((sourceLanguage, targetLanguage), out IReadOnlyList<BundledModelManifestEntry>? entries)
            ? entries
            : [];

    private static IEnumerable<BundledModelManifestEntry> GetPivotEntriesForPair(
        TranslationRoutingContext context,
        string sourceLanguage,
        string targetLanguage) =>
        context.PivotEntries.Where(entry => CoversLanguagePair(entry.LanguageCoverage, sourceLanguage, targetLanguage));

    private static IEnumerable<string> EnumerateResolvedEntryPaths(
        IReadOnlyDictionary<string, IReadOnlyList<LocalModelCacheRecord>> cacheIndex,
        BundledModelManifestEntry entry)
    {
        if (!cacheIndex.TryGetValue(entry.ModelId, out IReadOnlyList<LocalModelCacheRecord>? records))
        {
            yield break;
        }

        string relativeEntryPath = Path.GetRelativePath(entry.RootDirectory, entry.DefaultBenchmarkEntryPath);
        foreach (LocalModelCacheRecord record in records)
        {
            string resolvedPath = Path.GetFullPath(Path.Combine(record.RootPath, relativeEntryPath));
            if (File.Exists(resolvedPath))
            {
                yield return resolvedPath;
            }

        }
    }

    private static IEnumerable<string> EnumerateResolvedMadladEntryPaths(
        IReadOnlyDictionary<string, IReadOnlyList<LocalModelCacheRecord>> cacheIndex,
        BundledModelManifestEntry entry)
    {
        if (!cacheIndex.TryGetValue(entry.ModelId, out IReadOnlyList<LocalModelCacheRecord>? records))
        {
            yield break;
        }

        foreach (LocalModelCacheRecord record in records)
        {
            foreach (string candidatePath in EnumerateMadladEncoderEntryPaths(entry))
            {
                string relativeEntryPath = Path.GetRelativePath(entry.RootDirectory, candidatePath);
                string resolvedPath = Path.GetFullPath(Path.Combine(record.RootPath, relativeEntryPath));
                if (File.Exists(resolvedPath))
                {
                    yield return resolvedPath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateMadladEncoderEntryPaths(BundledModelManifestEntry entry)
    {
        yield return entry.DefaultBenchmarkEntryPath;

        foreach (BundledModelManifestVariant variant in entry.Variants)
        {
            string fileName = Path.GetFileName(variant.EntryPath);
            if (fileName.StartsWith("encoder_model", StringComparison.OrdinalIgnoreCase))
            {
                yield return variant.EntryPath;
            }
        }
    }

    private static bool HasOpusSupportingFiles(string modelRootPath)
    {
        string onnxDirectory = Path.Combine(modelRootPath, "onnx");
        return (File.Exists(Path.Combine(modelRootPath, "decoder_model.onnx")) ||
                File.Exists(Path.Combine(modelRootPath, "decoder_model_merged.onnx")) ||
                File.Exists(Path.Combine(onnxDirectory, "decoder_model.onnx")) ||
                File.Exists(Path.Combine(onnxDirectory, "decoder_model_merged.onnx"))) &&
               File.Exists(Path.Combine(modelRootPath, "vocab.json")) &&
               (File.Exists(Path.Combine(modelRootPath, "source.spm")) || File.Exists(Path.Combine(modelRootPath, "source.model"))) &&
               (File.Exists(Path.Combine(modelRootPath, "target.spm")) || File.Exists(Path.Combine(modelRootPath, "target.model")));
    }

    private static string ResolveOpusModelRootPath(string entryPath)
    {
        string entryDirectory = Path.GetDirectoryName(entryPath)
            ?? throw new InvalidOperationException("Direct Opus model root path could not be resolved.");
        if (HasOpusTokenizerFiles(entryDirectory))
        {
            return entryDirectory;
        }

        string? parentDirectory = Path.GetDirectoryName(entryDirectory);
        return parentDirectory is not null && HasOpusTokenizerFiles(parentDirectory)
            ? parentDirectory
            : entryDirectory;
    }

    private static bool HasOpusTokenizerFiles(string modelRootPath) =>
        File.Exists(Path.Combine(modelRootPath, "vocab.json")) &&
        (File.Exists(Path.Combine(modelRootPath, "source.spm")) || File.Exists(Path.Combine(modelRootPath, "source.model"))) &&
        (File.Exists(Path.Combine(modelRootPath, "target.spm")) || File.Exists(Path.Combine(modelRootPath, "target.model")));

    private static bool HasMadladSupportingFiles(string modelRootPath)
    {
        return (File.Exists(Path.Combine(modelRootPath, "decoder_model.onnx")) ||
                File.Exists(Path.Combine(modelRootPath, "decoder_model_merged.onnx")) ||
                File.Exists(Path.Combine(modelRootPath, "decoder_model_quantized.onnx")) ||
                File.Exists(Path.Combine(modelRootPath, "decoder_model_int8.onnx"))) &&
               (File.Exists(Path.Combine(modelRootPath, "spiece.model")) ||
                File.Exists(Path.Combine(modelRootPath, "tokenizer.model")) ||
                File.Exists(Path.Combine(modelRootPath, "sentencepiece.model")));
    }

    private static (string SourceLanguage, string TargetLanguage)? NormalizePair(ModelLanguagePair pair)
    {
        string? sourceLanguage = NormalizeLanguageCode(pair.SourceLanguage);
        string? targetLanguage = NormalizeLanguageCode(pair.TargetLanguage);
        return sourceLanguage is null || targetLanguage is null
            ? null
            : (sourceLanguage, targetLanguage);
    }

    private static IEnumerable<(string SourceLanguage, string TargetLanguage)> EnumerateDirectPairs(BundledModelManifestEntry entry)
    {
        HashSet<(string SourceLanguage, string TargetLanguage)> pairs = [];
        foreach ((string SourceLanguage, string TargetLanguage)? explicitPair in entry.LanguageCoverage.LanguagePairs.Select(NormalizePair))
        {
            if (explicitPair is { } value && pairs.Add(value))
            {
                yield return value;
            }
        }
    }

    private static bool HasDirectPair(
        BundledModelManifestEntry entry,
        string sourceLanguage,
        string targetLanguage) =>
        entry.LanguageCoverage.LanguagePairs.Any(pair =>
            string.Equals(pair.SourceLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase));

    private static bool CoversLanguagePair(
        ModelLanguageCoverage coverage,
        string sourceLanguage,
        string targetLanguage)
    {
        if (coverage.LanguagePairs.Select(NormalizePair).Any(pair => pair == (sourceLanguage, targetLanguage)))
        {
            return true;
        }

        return CoversLanguage(coverage.SourceLanguages, sourceLanguage) &&
               CoversLanguage(coverage.TargetLanguages, targetLanguage);
    }

    private static bool CoversLanguage(IReadOnlyList<string> supportedLanguages, string language) =>
        supportedLanguages.Any(candidate =>
            string.Equals(candidate, language, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "multi", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "any", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "*", StringComparison.OrdinalIgnoreCase));

    private static bool HasCapability(BundledModelManifestEntry entry, string capability) =>
        entry.Capabilities.Any(candidate => string.Equals(candidate, capability, StringComparison.OrdinalIgnoreCase));

    private static bool IsDirectTranslationEntry(BundledModelManifestEntry entry)
    {
        if (HasCapability(entry, "direct-translation"))
        {
            return true;
        }

        if (HasCapability(entry, "pivot-translation"))
        {
            return false;
        }

        return entry.LanguageCoverage.LanguagePairs.Count > 0;
    }

    private static bool IsPivotTranslationEntry(BundledModelManifestEntry entry)
    {
        if (HasCapability(entry, "pivot-translation"))
        {
            return true;
        }

        if (HasCapability(entry, "direct-translation"))
        {
            return false;
        }

        return entry.LanguageCoverage.SourceLanguages.Count > 0 ||
               entry.LanguageCoverage.TargetLanguages.Count > 0;
    }

    private static string ResolveProviderName(BundledModelManifestEntry entry) =>
        entry.EngineFamily.ToLowerInvariant() switch
        {
            "madlad" => "madlad400",
            var family => family
        };

    private static string ResolveDirectRouteDetail(BundledModelManifestEntry entry) =>
        string.Equals(entry.EngineFamily, "opus-mt", StringComparison.OrdinalIgnoreCase)
            ? "Direct Opus-MT"
            : $"Direct {entry.EngineFamily}";

    private static string ResolvePivotRouteDetail(BundledModelManifestEntry entry, bool directPairKnown)
    {
        string label = ResolvePivotRouteLabel(entry);
        return directPairKnown
            ? $"{label} (direct translation pair missing)"
            : label;
    }

    private static string ResolveUnavailablePivotLabel(IReadOnlyList<BundledModelManifestEntry> pivotEntries) =>
        pivotEntries.Count == 0
            ? "pivot translation"
            : ResolvePivotRouteLabel(pivotEntries[0]);

    private static string ResolvePivotRouteLabel(BundledModelManifestEntry entry) =>
        entry.EngineFamily.ToLowerInvariant() switch
        {
            "madlad" => "MADLAD-400 pivot",
            "phi-genai" => "Multilingual pivot",
            _ => "Pivot translation"
        };

    private static string? NormalizeLanguageCode(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().ToLowerInvariant();

    private sealed record TranslationRoutingContext(
        IReadOnlyDictionary<string, IReadOnlyList<LocalModelCacheRecord>> CacheIndex,
        IReadOnlyDictionary<(string SourceLanguage, string TargetLanguage), IReadOnlyList<BundledModelManifestEntry>> DirectEntriesByPair,
        IReadOnlyList<BundledModelManifestEntry> PivotEntries,
        IReadOnlyDictionary<string, bool> PlanAvailabilityByModelId);
}
