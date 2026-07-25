using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Settings;

public sealed class TrackdubStoragePaths : IAppStoragePaths
{
    public TrackdubStoragePaths()
        : this(TrackdubStoragePathResolver.Resolve())
    {
    }

    public TrackdubStoragePaths(string? localAppDataRoot)
        : this(CreateLegacyOptions(localAppDataRoot))
    {
    }

    public TrackdubStoragePaths(TrackdubStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RootDirectory = NormalizeRequiredPath(options.UserDataRoot, nameof(options));
        UserDataRoot = RootDirectory;
        UserCacheRoot = NormalizeRequiredPath(options.UserCacheRoot, nameof(options));
        SharedAssetRoot = NormalizeOptionalPath(options.SharedAssetRoot);
        IsPortable = options.IsPortable;

        ModelCacheDirectory = !string.IsNullOrWhiteSpace(options.ExplicitModelCacheDirectory)
            ? NormalizeRequiredPath(options.ExplicitModelCacheDirectory, nameof(options.ExplicitModelCacheDirectory))
            : Path.Combine(UserCacheRoot, "model-cache");
        ModelCacheIndexPath = Path.Combine(ModelCacheDirectory, "model-cache-records.json");
        LogFilePath = Path.Combine(UserDataRoot, "trackdub.log");
        SettingsPath = Path.Combine(UserDataRoot, "settings.json");
        LayoutPath = Path.Combine(UserDataRoot, "avalonia-layout.json");
        ToolCacheDirectory = Path.Combine(UserCacheRoot, "tools");
        FfmpegToolCacheDirectory = Path.Combine(ToolCacheDirectory, "ffmpeg");
        EngineCacheDirectory = Path.Combine(UserCacheRoot, "EngineCache");
        ComponentCacheDirectory = Path.Combine(UserCacheRoot, "components");
    }

    public string RootDirectory { get; }

    public string UserDataRoot { get; }

    public string UserCacheRoot { get; }

    public string? SharedAssetRoot { get; }

    public bool IsPortable { get; }

    public string ModelCacheDirectory { get; }

    public string ModelCacheIndexPath { get; }

    public string LogFilePath { get; }

    public string SettingsPath { get; }

    public string LayoutPath { get; }

    public string ToolCacheDirectory { get; }

    public string FfmpegToolCacheDirectory { get; }

    public string EngineCacheDirectory { get; }

    public string ComponentCacheDirectory { get; }

    private static TrackdubStorageOptions CreateLegacyOptions(string? localAppDataRoot)
    {
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            return TrackdubStoragePathResolver.Resolve();
        }

        string root = Path.Combine(NormalizeRequiredPath(localAppDataRoot, nameof(localAppDataRoot)), "Trackdub");
        return new TrackdubStorageOptions(root, root, SharedAssetRoot: null, IsPortable: false);
    }

    private static string NormalizeRequiredPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Storage path must not be empty.", parameterName);
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
}
