using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Composition.Tests.Headless;

public sealed class HeadlessPersistedEpLicensesTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "TrackdubTests",
        "ep-license-" + Guid.NewGuid().ToString("N"));

    public HeadlessPersistedEpLicensesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task PersistNvidiaLicense_survives_a_fresh_headless_load()
    {
        var paths = new TrackdubStoragePaths(
            new TrackdubStorageOptions(_root, _root, SharedAssetRoot: null, IsPortable: false));

        await HeadlessPersistedEpLicenses.PersistNvidiaTensorRtRtxAcceptanceAsync(
            paths,
            TestContext.Current.CancellationToken);

        StudioSettings reloaded = HeadlessPersistedEpLicenses.Load(paths);
        Assert.True(reloaded.NvidiaTensorRtRtxLicenseAccepted);

        var options = new HeadlessTrackdubOptions
        {
            ModelDirectory = _root,
            ModelCacheDirectory = _root,
        };
        var inMemory = new InMemoryStudioSettingsService(options, HeadlessPersistedEpLicenses.Load(options));
        StudioSettings seeded = await inMemory.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(seeded.NvidiaTensorRtRtxLicenseAccepted);
    }
}
