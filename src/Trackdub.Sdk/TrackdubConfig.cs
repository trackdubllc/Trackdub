using System.Text.Json;

using Trackdub.Contracts;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Sdk;

public sealed record TrackdubConfigPathSnapshot(
    string UserDataRoot,
    string UserCacheRoot,
    string? SharedAssetRoot,
    bool IsPortable,
    string ModelCacheDirectory,
    string ModelCacheIndexPath,
    string LogFilePath,
    string SettingsPath,
    string LayoutPath,
    string ToolCacheDirectory,
    string FfmpegToolCacheDirectory,
    string EngineCacheDirectory,
    string ComponentCacheDirectory,
    string? BundledManifestPath,
    string? BundledManifestLoadError);

public sealed record TrackdubConfigShowSnapshot(
    TrackdubConfigPathSnapshot Paths,
    bool SettingsFileExists,
    string? SettingsReadError,
    IReadOnlyList<RecentProjectEntry> RecentProjects,
    string? DefaultSourceLanguage,
    string? DefaultTargetLanguage,
    string ModelTierPreference);

public static class TrackdubConfig
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static TrackdubConfigPathSnapshot CapturePaths(TrackdubSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        string? manifestPath = null;
        string? manifestError = null;

        if (BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error)
            && registry is not null)
        {
            manifestPath = registry.ManifestPath;
        }
        else
        {
            manifestError = error;
        }

        return new TrackdubConfigPathSnapshot(
            storagePaths.UserDataRoot,
            storagePaths.UserCacheRoot,
            storagePaths.SharedAssetRoot,
            storagePaths.IsPortable,
            storagePaths.ModelCacheDirectory,
            storagePaths.ModelCacheIndexPath,
            storagePaths.LogFilePath,
            storagePaths.SettingsPath,
            storagePaths.LayoutPath,
            storagePaths.ToolCacheDirectory,
            storagePaths.FfmpegToolCacheDirectory,
            storagePaths.EngineCacheDirectory,
            storagePaths.ComponentCacheDirectory,
            manifestPath,
            manifestError);
    }

    public static async Task<TrackdubConfigShowSnapshot> CaptureShowAsync(
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken)
    {
        TrackdubConfigPathSnapshot paths = CapturePaths(factory);
        PersistedSettingsSnapshot persisted = await TryReadPersistedSettingsAsync(
            paths.SettingsPath,
            cancellationToken).ConfigureAwait(false);

        return new TrackdubConfigShowSnapshot(
            paths,
            persisted.SettingsFileExists,
            persisted.SettingsReadError,
            persisted.RecentProjects,
            persisted.DefaultSourceLanguage,
            persisted.DefaultTargetLanguage,
            persisted.ModelTierPreference);
    }

    private static async Task<PersistedSettingsSnapshot> TryReadPersistedSettingsAsync(
        string settingsPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            return new PersistedSettingsSnapshot(
                SettingsFileExists: false,
                SettingsReadError: null,
                RecentProjects: [],
                DefaultSourceLanguage: null,
                DefaultTargetLanguage: null,
                ModelTierPreference: StudioSettings.Default.ModelTierPreference);
        }

        try
        {
            await using FileStream stream = File.OpenRead(settingsPath);
            StudioSettings? settings = await JsonSerializer
                .DeserializeAsync<StudioSettings>(stream, SettingsJsonOptions, cancellationToken)
                .ConfigureAwait(false);

            settings ??= StudioSettings.Default;

            IReadOnlyList<RecentProjectEntry> recentProjects = settings.RecentProjects
                .Where(entry => Directory.Exists(entry.ProjectPath))
                .ToList();

            return new PersistedSettingsSnapshot(
                SettingsFileExists: true,
                SettingsReadError: null,
                RecentProjects: recentProjects,
                DefaultSourceLanguage: settings.DefaultSourceLanguage,
                DefaultTargetLanguage: settings.DefaultTargetLanguage,
                ModelTierPreference: settings.ModelTierPreference);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PersistedSettingsSnapshot(
                SettingsFileExists: true,
                SettingsReadError: ex.Message,
                RecentProjects: [],
                DefaultSourceLanguage: null,
                DefaultTargetLanguage: null,
                ModelTierPreference: StudioSettings.Default.ModelTierPreference);
        }
    }

    private sealed record PersistedSettingsSnapshot(
        bool SettingsFileExists,
        string? SettingsReadError,
        IReadOnlyList<RecentProjectEntry> RecentProjects,
        string? DefaultSourceLanguage,
        string? DefaultTargetLanguage,
        string ModelTierPreference);
}
