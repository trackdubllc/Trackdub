using System.Text.Json;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class TrackdubStoragePathResolverTests
{
    [Fact]
    public void Resolve_uses_portable_data_root_next_to_app_when_marker_exists()
    {
        string testRoot = CreateTestRoot();
        string appBaseDirectory = Path.Combine(testRoot, "app");
        Directory.CreateDirectory(appBaseDirectory);
        File.WriteAllText(Path.Combine(appBaseDirectory, "Trackdub.portable"), string.Empty);

        var context = new TrackdubStoragePathResolutionContext(
            appBaseDirectory,
            Path.Combine(testRoot, "local"),
            Path.Combine(testRoot, "program-data"),
            new Dictionary<string, string?>());

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);
        var paths = new TrackdubStoragePaths(options);

        string portableRoot = Path.GetFullPath(Path.Combine(appBaseDirectory, "portable-data"));
        Assert.True(options.IsPortable);
        Assert.Equal(portableRoot, options.UserDataRoot);
        Assert.Equal(portableRoot, options.UserCacheRoot);
        Assert.Null(options.SharedAssetRoot);
        Assert.Equal(Path.Combine(portableRoot, "model-cache"), paths.ModelCacheDirectory);
        Assert.Equal(Path.Combine(portableRoot, "avalonia-layout.json"), paths.LayoutPath);
        Assert.Equal(Path.Combine(portableRoot, "tools", "ffmpeg"), paths.FfmpegToolCacheDirectory);
        Assert.Equal(Path.Combine(portableRoot, "EngineCache"), paths.EngineCacheDirectory);
    }

    [Fact]
    public void Resolve_prefers_environment_roots_over_portable_marker()
    {
        string testRoot = CreateTestRoot();
        string appBaseDirectory = Path.Combine(testRoot, "app");
        Directory.CreateDirectory(appBaseDirectory);
        File.WriteAllText(Path.Combine(appBaseDirectory, "Trackdub.portable"), string.Empty);

        string dataRoot = Path.Combine(testRoot, "env-data");
        string cacheRoot = Path.Combine(testRoot, "env-cache");
        string sharedRoot = Path.Combine(testRoot, "env-shared");
        var environment = new Dictionary<string, string?>
        {
            ["TRACKDUB_DATA_ROOT"] = dataRoot,
            ["TRACKDUB_CACHE_ROOT"] = cacheRoot,
            ["TRACKDUB_SHARED_ASSET_ROOT"] = sharedRoot
        };

        var context = new TrackdubStoragePathResolutionContext(
            appBaseDirectory,
            Path.Combine(testRoot, "local"),
            Path.Combine(testRoot, "program-data"),
            environment);

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);

        Assert.False(options.IsPortable);
        Assert.Equal(Path.GetFullPath(dataRoot), options.UserDataRoot);
        Assert.Equal(Path.GetFullPath(cacheRoot), options.UserCacheRoot);
        Assert.Equal(Path.GetFullPath(sharedRoot), options.SharedAssetRoot);
    }

    [Fact]
    public void Resolve_uses_installer_storage_config_from_common_app_data()
    {
        string testRoot = CreateTestRoot();
        string appBaseDirectory = Path.Combine(testRoot, "app");
        string localRoot = Path.Combine(testRoot, "local");
        string commonRoot = Path.Combine(testRoot, "program-data");
        string dataRoot = Path.Combine(testRoot, "configured-data");
        string cacheRoot = Path.Combine(testRoot, "configured-cache");
        string sharedRoot = Path.Combine(testRoot, "configured-shared");
        string configDirectory = Path.Combine(commonRoot, "Trackdub");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "storage.json"),
            JsonSerializer.Serialize(new
            {
                userDataRoot = dataRoot,
                userCacheRoot = cacheRoot,
                sharedAssetRoot = sharedRoot
            }));

        var context = new TrackdubStoragePathResolutionContext(
            appBaseDirectory,
            localRoot,
            commonRoot,
            new Dictionary<string, string?>());

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);

        Assert.False(options.IsPortable);
        Assert.Equal(Path.GetFullPath(dataRoot), options.UserDataRoot);
        Assert.Equal(Path.GetFullPath(cacheRoot), options.UserCacheRoot);
        Assert.Equal(Path.GetFullPath(sharedRoot), options.SharedAssetRoot);
    }

    private static string CreateTestRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.StoragePathResolver.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
