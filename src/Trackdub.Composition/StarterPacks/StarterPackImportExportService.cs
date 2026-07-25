using System.Text.Json;
using System.Text.RegularExpressions;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Composition.StarterPacks;

public sealed partial class StarterPackImportExportService(
    IStarterPackCatalog catalog,
    StarterPackValidator validator,
    BundledModelManifestRegistry manifestRegistry,
    IStarterPackCompatibilityService compatibilityService) : IStarterPackImportExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    [GeneratedRegex("^[a-z][a-z0-9-]{1,48}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackIdPattern();

    public async Task<StarterPackImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(sourcePath))
        {
            return new StarterPackImportResult(string.Empty, false, [], $"Starter pack file not found: {sourcePath}");
        }

        string json = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        StarterPackDefinition pack;
        try
        {
            pack = StarterPackCatalog.ParseDefinition(json, StarterPackOrigin.User);
        }
        catch (Exception ex)
        {
            return new StarterPackImportResult(string.Empty, false, [], ex.Message);
        }

        if (!PackIdPattern().IsMatch(pack.Id))
        {
            return new StarterPackImportResult(
                pack.Id,
                false,
                [],
                "Pack id must match ^[a-z][a-z0-9-]{1,48}$.");
        }

        if (StarterPackShippingIds.IsShippingPack(pack.Id) && pack.PackOrigin != StarterPackOrigin.User)
        {
            return new StarterPackImportResult(
                pack.Id,
                false,
                [],
                $"Cannot import over bundled pack id '{pack.Id}' without pack_origin=user.");
        }

        try
        {
            validator.Validate(pack, manifestRegistry);
        }
        catch (Exception ex)
        {
            return new StarterPackImportResult(pack.Id, false, [], ex.Message);
        }

        var warnings = new List<string>();
        Directory.CreateDirectory(catalog.UserPacksDirectory);
        string destinationPath = Path.Combine(catalog.UserPacksDirectory, $"{pack.Id}.json");
        await File.WriteAllTextAsync(destinationPath, json, cancellationToken).ConfigureAwait(false);
        catalog.InvalidateCache();

        string profileId = StarterPackResolver.ResolveDefaultProfileId(pack);
        try
        {
            StarterPackCompatibilityReport compatibility = await compatibilityService
                .EvaluateAsync(pack.Id, profileId, hardwareProfile: null, cancellationToken)
                .ConfigureAwait(false);
            warnings.AddRange(compatibility.Stages
                .Where(stage => stage.FallbackApplied)
                .Select(stage =>
                    $"GPU path unavailable for {stage.Alias}. Using {stage.ResolvedVariant} on {stage.ResolvedExecutionProvider}."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Compatibility check skipped: {ex.Message}");
        }

        return new StarterPackImportResult(pack.Id, true, warnings);
    }

    public async Task ExportAsync(string packId, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        StarterPackDefinition pack = await catalog.GetAsync(packId, cancellationToken).ConfigureAwait(false);
        if (pack.PackOrigin != StarterPackOrigin.User)
        {
            string userPath = Path.Combine(catalog.UserPacksDirectory, $"{packId}.json");
            if (!File.Exists(userPath))
            {
                throw new InvalidOperationException($"Pack '{packId}' is bundled-only and cannot be exported.");
            }

            string json = await File.ReadAllTextAsync(userPath, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(destinationPath, json, cancellationToken).ConfigureAwait(false);
            return;
        }

        string sourcePath = Path.Combine(catalog.UserPacksDirectory, $"{packId}.json");
        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"User pack '{packId}' was not found.");
        }

        string userJson = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(destinationPath, userJson, cancellationToken).ConfigureAwait(false);
    }

    public Task ExportFromSettingsAsync(
        StudioSettings settings,
        string packId,
        string displayName,
        string description,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!PackIdPattern().IsMatch(packId))
        {
            throw new InvalidOperationException("Pack id must match ^[a-z][a-z0-9-]{1,48}$.");
        }

        var stageAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings.StageModelAliases is not null)
        {
            foreach ((string key, string value) in settings.StageModelAliases)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    stageAliases[key] = value.Trim();
                }
            }
        }

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["asr"] = AsrModelOverrideSettings.ToKey(settings.AsrModelOverride),
            ["translation"] = TranslationModelOverrideSettings.ToKey(settings.TranslationModelOverride),
            ["tts"] = TtsModelOverrideSettings.ToKey(settings.TtsModelOverride)
        };

        var exportDto = new
        {
            schema_version = 1,
            id = packId,
            pack_origin = "user",
            pack_kind = "local",
            display_name = displayName.Trim(),
            tier_preference = settings.ModelTierPreference,
            description = description.Trim(),
            profiles = new[] { new { id = "default", display_name = "Default" } },
            models = Array.Empty<object>(),
            apply = new
            {
                tier_preference = settings.ModelTierPreference,
                stage_aliases = stageAliases,
                overrides
            },
            olive_auto_run = false
        };

        string json = JsonSerializer.Serialize(exportDto, JsonOptions);
        return File.WriteAllTextAsync(destinationPath, json, cancellationToken);
    }

    public Task DeleteUserPackAsync(string packId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!PackIdPattern().IsMatch(packId))
        {
            throw new InvalidOperationException("Pack id must match ^[a-z][a-z0-9-]{1,48}$.");
        }

        string userPath = Path.Combine(catalog.UserPacksDirectory, $"{packId}.json");
        if (!File.Exists(userPath))
        {
            throw new InvalidOperationException($"User pack '{packId}' was not found.");
        }

        File.Delete(userPath);
        catalog.InvalidateCache();
        return Task.CompletedTask;
    }
}
