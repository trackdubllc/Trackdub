using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteConsentServiceTests
{
    private static TrackdubStoragePaths CreateStoragePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        return new TrackdubStoragePaths(root);
    }

    [Fact]
    public void New_service_instance_does_not_preload_consent_from_database_audit_trail()
    {
        TrackdubStoragePaths paths = CreateStoragePaths();
        try
        {
            using var service1 = new SqliteConsentService(paths);
            Assert.False(service1.IsVoiceCloningConsentGranted);

            service1.GrantVoiceCloningConsent();
            Assert.True(service1.IsVoiceCloningConsentGranted);

            using var service2 = new SqliteConsentService(paths);
            Assert.False(service2.IsVoiceCloningConsentGranted);
        }
        finally
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ClearVoiceCloningConsent_persists_across_service_instances()
    {
        TrackdubStoragePaths paths = CreateStoragePaths();
        try
        {
            using var service1 = new SqliteConsentService(paths);
            service1.GrantVoiceCloningConsent();
            Assert.True(service1.IsVoiceCloningConsentGranted);

            service1.ClearVoiceCloningConsent();
            Assert.False(service1.IsVoiceCloningConsentGranted);

            using var service2 = new SqliteConsentService(paths);
            Assert.False(service2.IsVoiceCloningConsentGranted);
        }
        finally
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void IsVoiceCloningConsentGranted_returns_false_when_database_not_exists()
    {
        TrackdubStoragePaths paths = CreateStoragePaths();
        try
        {
            using var service = new SqliteConsentService(paths);
            Assert.False(service.IsVoiceCloningConsentGranted);
        }
        finally
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void GrantVoiceCloningConsent_fires_event()
    {
        TrackdubStoragePaths paths = CreateStoragePaths();
        try
        {
            using var service = new SqliteConsentService(paths);
            int eventCount = 0;
            service.VoiceCloningConsentChanged += (_, _) => eventCount++;

            service.GrantVoiceCloningConsent();

            Assert.Equal(1, eventCount);
        }
        finally
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void GrantVoiceCloningConsent_does_not_fire_event_when_already_granted()
    {
        TrackdubStoragePaths paths = CreateStoragePaths();
        try
        {
            using var service = new SqliteConsentService(paths);
            service.GrantVoiceCloningConsent();

            int eventCount = 0;
            service.VoiceCloningConsentChanged += (_, _) => eventCount++;

            service.GrantVoiceCloningConsent();

            Assert.Equal(0, eventCount);
        }
        finally
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ClearVoiceCloningConsent_fires_event()
    {
        TrackdubStoragePaths paths = CreateStoragePaths();
        try
        {
            var service = new SqliteConsentService(paths);
            service.GrantVoiceCloningConsent();

            int eventCount = 0;
            service.VoiceCloningConsentChanged += (_, _) => eventCount++;

            service.ClearVoiceCloningConsent();

            Assert.Equal(1, eventCount);
        }
        finally
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ClearVoiceCloningConsent_does_not_fire_event_when_already_cleared()
    {
        TrackdubStoragePaths paths = CreateStoragePaths();
        try
        {
            using var service = new SqliteConsentService(paths);
            service.GrantVoiceCloningConsent();
            service.ClearVoiceCloningConsent();

            int eventCount = 0;
            service.VoiceCloningConsentChanged += (_, _) => eventCount++;

            service.ClearVoiceCloningConsent();

            Assert.Equal(0, eventCount);
        }
        finally
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void SessionId_differs_per_instance()
    {
        TrackdubStoragePaths paths = CreateStoragePaths();
        try
        {
            using var service1 = new SqliteConsentService(paths);
            using var service2 = new SqliteConsentService(paths);

            Assert.NotEqual(service1.SessionId, service2.SessionId);
        }
        finally
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                Directory.Delete(paths.RootDirectory, recursive: true);
            }
        }
    }
}
