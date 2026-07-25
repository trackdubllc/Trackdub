using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Composition.StarterPacks;

public sealed class StarterPackCatalog : IStarterPackCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string bundledDirectory;
    private readonly string userPacksDirectory;
    private readonly StarterPackValidator? validator;
    private readonly BundledModelManifestRegistry? manifestRegistry;
    private readonly object sync = new();
    private IReadOnlyDictionary<string, StarterPackDefinition>? cache;

    public StarterPackCatalog()
        : this(storagePaths: null, validator: null, manifestRegistry: null)
    {
    }

    public StarterPackCatalog(IAppStoragePaths? storagePaths)
        : this(storagePaths, validator: null, manifestRegistry: null)
    {
    }

    public StarterPackCatalog(
        IAppStoragePaths? storagePaths,
        StarterPackValidator? validator,
        BundledModelManifestRegistry? manifestRegistry)
    {
        bundledDirectory = Path.Combine(AppContext.BaseDirectory, "StarterPacks");
        userPacksDirectory = storagePaths is null
            ? Path.Combine(Path.GetTempPath(), "Trackdub", "StarterPacks", "user")
            : Path.Combine(storagePaths.UserDataRoot, "StarterPacks");
        UserPacksDirectory = userPacksDirectory;
        this.validator = validator;
        this.manifestRegistry = manifestRegistry;
    }

    public string UserPacksDirectory { get; }

    public Task<IReadOnlyList<StarterPackDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<StarterPackDefinition>>(LoadAll().Values.OrderBy(p => p.Id).ToList());
    }

    public Task<StarterPackDefinition> GetAsync(string packId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);

        IReadOnlyDictionary<string, StarterPackDefinition> packs = LoadAll();
        if (packs.TryGetValue(packId.Trim(), out StarterPackDefinition? pack))
        {
            return Task.FromResult(pack);
        }

        throw new InvalidOperationException($"Starter pack '{packId}' was not found.");
    }

    public void InvalidateCache() => cache = null;

    internal void InvalidateCacheForTests() => InvalidateCache();

    public static StarterPackDefinition ParseDefinition(string json, StarterPackOrigin origin)
    {
        StarterPackFileDto? dto = JsonSerializer.Deserialize<StarterPackFileDto>(json, JsonOptions)
            ?? throw new InvalidOperationException("Starter pack JSON is empty.");
        return dto.ToDefinition(origin);
    }

    private IReadOnlyDictionary<string, StarterPackDefinition> LoadAll()
    {
        lock (sync)
        {
            if (cache is not null)
            {
                return cache;
            }

            var packs = new Dictionary<string, StarterPackDefinition>(StringComparer.OrdinalIgnoreCase);
            LoadDirectory(bundledDirectory, packs, StarterPackOrigin.Bundled);
            if (Directory.Exists(userPacksDirectory))
            {
                LoadDirectory(userPacksDirectory, packs, StarterPackOrigin.User);
            }

            cache = packs;
            return cache;
        }
    }

    private void LoadDirectory(
        string directory,
        Dictionary<string, StarterPackDefinition> packs,
        StarterPackOrigin origin)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                StarterPackDefinition definition = ParseDefinition(json, origin);
                if (origin == StarterPackOrigin.Bundled && validator is not null && manifestRegistry is not null)
                {
                    validator.Validate(definition, manifestRegistry);
                }

                packs[definition.Id] = definition;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                if (origin == StarterPackOrigin.Bundled)
                {
                    throw new InvalidOperationException($"Bundled starter pack '{file}' failed validation.", ex);
                }

                // Skip invalid user overrides; bundled packs fail fast above.
            }
        }
    }

    private sealed class StarterPackFileDto
    {
        public int SchemaVersion { get; init; }
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string TierPreference { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public List<StarterPackProfileDto> Profiles { get; init; } = [];
        public List<StarterPackModelDto> Models { get; init; } = [];
        public StarterPackTranslationDto? Translation { get; init; }
        public List<string>? OptionalModels { get; init; }
        public bool OliveAutoRun { get; init; }
        public string? PackKind { get; init; }
        public string? PackOrigin { get; init; }
        public StarterPackCloudDefaultsDto? CloudDefaults { get; init; }
        public StarterPackApplyDto? Apply { get; init; }
        public bool AllowOverride { get; init; }

        public StarterPackDefinition ToDefinition(StarterPackOrigin loadOrigin)
        {
            StarterPackOrigin packOrigin = ParsePackOrigin(PackOrigin) ?? loadOrigin;
            return new StarterPackDefinition(
                SchemaVersion,
                Id,
                DisplayName,
                TierPreference,
                Description,
                Profiles.Select(static profile => new StarterPackProfileDefinition(
                    profile.Id,
                    profile.DisplayName,
                    profile.AsrModelId)).ToList(),
                Models.Select(static model => new StarterPackModelDefinition(
                    model.ModelId,
                    model.Stage,
                    model.Required,
                    model.Alias,
                    model.RuntimeDefaults.ToDictionary(
                        static pair => pair.Key,
                        static pair => new StarterPackRuntimeDefaults(pair.Value.Variant, pair.Value.ExecutionProvider),
                        StringComparer.OrdinalIgnoreCase),
                    string.IsNullOrWhiteSpace(model.Source) ? "local" : model.Source.Trim(),
                    string.IsNullOrWhiteSpace(model.VariantPreference) ? "auto" : model.VariantPreference.Trim())).ToList(),
                Translation is null
                    ? null
                    : new StarterPackTranslationDefinition(Translation.Strategy, Translation.ModelId, Translation.Alias),
                OptionalModels,
                OliveAutoRun,
                ParsePackKind(PackKind),
                CloudDefaults?.ToRecord(),
                packOrigin,
                Apply?.ToRecord());
        }
    }

    private sealed class StarterPackProfileDto
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? AsrModelId { get; init; }
    }

    private sealed class StarterPackModelDto
    {
        public string ModelId { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public bool Required { get; init; }
        public string Alias { get; init; } = string.Empty;
        public string? Source { get; init; }
        public string? VariantPreference { get; init; }
        public Dictionary<string, StarterPackRuntimeDefaultsDto> RuntimeDefaults { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StarterPackRuntimeDefaultsDto
    {
        public string Variant { get; init; } = "default";
        public string ExecutionProvider { get; init; } = "auto";
    }

    private sealed class StarterPackTranslationDto
    {
        public string Strategy { get; init; } = "universal";
        public string ModelId { get; init; } = string.Empty;
        public string Alias { get; init; } = string.Empty;
    }

    private sealed class StarterPackCloudDefaultsDto
    {
        public string Asr { get; init; } = string.Empty;
        public string Translation { get; init; } = string.Empty;
        public string Tts { get; init; } = string.Empty;

        public StarterPackCloudDefaults ToRecord() =>
            new(Asr, Translation, Tts);
    }

    private sealed class StarterPackApplyDto
    {
        public string? TierPreference { get; init; }
        public Dictionary<string, string>? StageAliases { get; init; }
        public Dictionary<string, string>? Overrides { get; init; }
        public Dictionary<string, string>? CloudStages { get; init; }

        public StarterPackApplyBlock ToRecord() =>
            new(
                TierPreference,
                StageAliases,
                Overrides,
                CloudStages);
    }

    private static StarterPackKind ParsePackKind(string? packKind) =>
        packKind?.Trim().ToLowerInvariant() switch
        {
            "cloud" => StarterPackKind.Cloud,
            "hybrid" => StarterPackKind.Hybrid,
            _ => StarterPackKind.Local
        };

    private static StarterPackOrigin? ParsePackOrigin(string? packOrigin) =>
        packOrigin?.Trim().ToLowerInvariant() switch
        {
            "user" => StarterPackOrigin.User,
            "bundled" => StarterPackOrigin.Bundled,
            _ => null
        };
}
