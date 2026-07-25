using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteSpeakerConsentServiceTests
{
    private static async Task SeedSpeakerAsync(SqliteProjectDatabase database, Guid projectId, Guid speakerId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO speakers (id, project_id, display_name, created_at_utc)
            VALUES ($id, $projectId, $displayName, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$id", speakerId.ToString());
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$displayName", "Test Speaker");
        command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [Fact]
    public async Task RecordConsentAsync_persists_and_round_trips()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-RoundTrip", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            Guid speakerId = Guid.NewGuid();
            await SeedSpeakerAsync(database, project.Id, speakerId);
            VoiceCloneConsentRecord recorded = await consentService.RecordConsentAsync(
                project.Id, speakerId, isThirdPartyConsent: true, notes: "Written consent on file", TestContext.Current.CancellationToken);

            Assert.NotEqual(Guid.Empty, recorded.Id);
            Assert.Equal(project.Id, recorded.ProjectId);
            Assert.Equal(speakerId, recorded.SpeakerId);
            Assert.Equal(VoiceCloneConsentRecord.CurrentVersion, recorded.ConsentVersion);
            Assert.True(recorded.IsThirdPartyConsent);
            Assert.Equal("Written consent on file", recorded.Notes);
            Assert.True(recorded.IsActive);
            Assert.Null(recorded.ExpiresAtUtc);
            Assert.Null(recorded.RevokedAtUtc);

            VoiceCloneConsentRecord? reloaded = await consentService.GetConsentAsync(speakerId, TestContext.Current.CancellationToken);
            Assert.NotNull(reloaded);
            Assert.Equal(recorded.Id, reloaded.Id);
            Assert.Equal(project.Id, reloaded.ProjectId);
            Assert.Equal(speakerId, reloaded.SpeakerId);
            Assert.Equal(recorded.GrantedAtUtc, reloaded.GrantedAtUtc);
            Assert.Equal(VoiceCloneConsentRecord.CurrentVersion, reloaded.ConsentVersion);
            Assert.True(reloaded.IsThirdPartyConsent);
            Assert.Equal("Written consent on file", reloaded.Notes);
            Assert.True(reloaded.IsActive);
            Assert.Null(reloaded.ExpiresAtUtc);
            Assert.Null(reloaded.RevokedAtUtc);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IsConsentGrantedAsync_returns_true_for_active_consent()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-Active", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            Guid speakerId = Guid.NewGuid();
            await SeedSpeakerAsync(database, project.Id, speakerId);
            await consentService.RecordConsentAsync(project.Id, speakerId, isThirdPartyConsent: false, notes: null, TestContext.Current.CancellationToken);

            bool granted = await consentService.IsConsentGrantedAsync(speakerId, TestContext.Current.CancellationToken);
            Assert.True(granted);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IsConsentGrantedAsync_returns_false_when_no_consent()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-Missing", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            bool granted = await consentService.IsConsentGrantedAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
            Assert.False(granted);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IsConsentGrantedAsync_returns_false_when_database_not_exists()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var consentService = new SqliteSpeakerConsentService(database);

            bool granted = await consentService.IsConsentGrantedAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
            Assert.False(granted);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetConsentAsync_returns_null_when_no_consent()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-Null", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            VoiceCloneConsentRecord? result = await consentService.GetConsentAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetConsentAsync_returns_null_when_database_not_exists()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var consentService = new SqliteSpeakerConsentService(database);

            VoiceCloneConsentRecord? result = await consentService.GetConsentAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RevokeConsentAsync_sets_revoked_at()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-Revoke", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            Guid speakerId = Guid.NewGuid();
            await SeedSpeakerAsync(database, project.Id, speakerId);
            await consentService.RecordConsentAsync(project.Id, speakerId, isThirdPartyConsent: false, notes: null, TestContext.Current.CancellationToken);

            await consentService.RevokeConsentAsync(speakerId, TestContext.Current.CancellationToken);

            VoiceCloneConsentRecord? record = await consentService.GetConsentAsync(speakerId, TestContext.Current.CancellationToken);
            Assert.NotNull(record);
            Assert.NotNull(record.RevokedAtUtc);
            Assert.False(record.IsActive);
            Assert.False(await consentService.IsConsentGrantedAsync(speakerId, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RevokeConsentAsync_twice_does_not_throw()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-RevokeTwice", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            Guid speakerId = Guid.NewGuid();
            await SeedSpeakerAsync(database, project.Id, speakerId);
            await consentService.RecordConsentAsync(project.Id, speakerId, isThirdPartyConsent: false, notes: null, TestContext.Current.CancellationToken);

            await consentService.RevokeConsentAsync(speakerId, TestContext.Current.CancellationToken);
            // Second revoke should not throw
            await consentService.RevokeConsentAsync(speakerId, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RevokeConsentAsync_non_existent_speaker_does_not_throw()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-NoOpRevoke", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            await consentService.RevokeConsentAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecordConsentAsync_replaces_previous_consent_for_same_speaker()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-Replace", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            Guid speakerId = Guid.NewGuid();
            await SeedSpeakerAsync(database, project.Id, speakerId);

            await consentService.RecordConsentAsync(
                project.Id, speakerId, isThirdPartyConsent: false, notes: "First consent", TestContext.Current.CancellationToken);
            VoiceCloneConsentRecord second = await consentService.RecordConsentAsync(
                project.Id, speakerId, isThirdPartyConsent: true, notes: "Replacement consent", TestContext.Current.CancellationToken);

            VoiceCloneConsentRecord? current = await consentService.GetConsentAsync(speakerId, TestContext.Current.CancellationToken);
            Assert.NotNull(current);
            Assert.Equal(second.Id, current.Id);
            Assert.Equal("Replacement consent", current.Notes);
            Assert.True(current.IsThirdPartyConsent);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetConsentAsync_returns_most_recent_consent_by_granted_at()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-Latest", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            Guid speakerId = Guid.NewGuid();
            await SeedSpeakerAsync(database, project.Id, speakerId);

            VoiceCloneConsentRecord first = await consentService.RecordConsentAsync(
                project.Id, speakerId, isThirdPartyConsent: false, notes: "Older", TestContext.Current.CancellationToken);

            // Simulate time passing by recording another consent with the same speaker
            // Note: INSERT OR REPLACE uses (project_id, speaker_id) unique constraint,
            // so each record replaces the previous — but the LIMIT 1 / ORDER BY handles the case
            VoiceCloneConsentRecord second = await consentService.RecordConsentAsync(
                project.Id, speakerId, isThirdPartyConsent: false, notes: "Newer", TestContext.Current.CancellationToken);

            Assert.NotEqual(first.Id, second.Id);

            VoiceCloneConsentRecord? reloaded = await consentService.GetConsentAsync(speakerId, TestContext.Current.CancellationToken);
            Assert.NotNull(reloaded);
            Assert.Equal(second.Id, reloaded.Id);
            Assert.Equal("Newer", reloaded.Notes);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecordConsentAsync_respects_expiry()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Consent-Expiry", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            Guid speakerId = Guid.NewGuid();
            await SeedSpeakerAsync(database, project.Id, speakerId);

            // Record consent that expires in the past (immediately expired)
            VoiceCloneConsentRecord expiredRecord = VoiceCloneConsentRecord.Create(
                project.Id, speakerId, isThirdPartyConsent: false, notes: "Expired");
            expiredRecord = expiredRecord with { ExpiresAtUtc = now.AddSeconds(-1) };

            // Insert it directly via SQL to test the raw data handling
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            await using SqliteConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO voice_clone_consents (id, project_id, speaker_id, granted_at_utc, consent_version,
                    is_third_party, notes, expires_at_utc, revoked_at_utc)
                VALUES ($id, $projectId, $speakerId, $grantedAtUtc, $consentVersion,
                    $isThirdParty, $notes, $expiresAtUtc, $revokedAtUtc);
                """;
            command.Parameters.AddWithValue("$id", SqliteValueConverters.ToDbValue(expiredRecord.Id));
            command.Parameters.AddWithValue("$projectId", SqliteValueConverters.ToDbValue(expiredRecord.ProjectId));
            command.Parameters.AddWithValue("$speakerId", SqliteValueConverters.ToDbValue(expiredRecord.SpeakerId));
            command.Parameters.AddWithValue("$grantedAtUtc", SqliteValueConverters.ToDbValue(expiredRecord.GrantedAtUtc));
            command.Parameters.AddWithValue("$consentVersion", expiredRecord.ConsentVersion);
            command.Parameters.AddWithValue("$isThirdParty", expiredRecord.IsThirdPartyConsent ? 1L : 0L);
            command.Parameters.AddWithValue("$notes", expiredRecord.Notes ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$expiresAtUtc", SqliteValueConverters.ToDbValue(expiredRecord.ExpiresAtUtc!.Value));
            command.Parameters.AddWithValue("$revokedAtUtc", DBNull.Value);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            VoiceCloneConsentRecord? reloaded = await consentService.GetConsentAsync(speakerId, TestContext.Current.CancellationToken);
            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded.ExpiresAtUtc);
            Assert.False(reloaded.IsActive, "Expired consent should not be active");
            Assert.False(await consentService.IsConsentGrantedAsync(speakerId, TestContext.Current.CancellationToken),
                "IsConsentGranted should return false for expired consent");
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecordConsentAsync_persists_independent_consents_per_project()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Consent.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var consentService = new SqliteSpeakerConsentService(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var projectA = new TrackdubProject(Guid.NewGuid(), "ProjectA", now, now);
            var projectB = new TrackdubProject(Guid.NewGuid(), "ProjectB", now, now);

            await projectRepository.InitializeAsync(projectA, TestContext.Current.CancellationToken);
            await projectRepository.InitializeAsync(projectB, TestContext.Current.CancellationToken);

            Guid speakerAId = Guid.NewGuid();
            Guid speakerBId = Guid.NewGuid();
            await SeedSpeakerAsync(database, projectA.Id, speakerAId);
            await SeedSpeakerAsync(database, projectB.Id, speakerBId);

            await consentService.RecordConsentAsync(
                projectA.Id, speakerAId, isThirdPartyConsent: false, notes: "Project A consent", TestContext.Current.CancellationToken);
            await consentService.RecordConsentAsync(
                projectB.Id, speakerBId, isThirdPartyConsent: true, notes: "Project B consent", TestContext.Current.CancellationToken);

            VoiceCloneConsentRecord? consentA = await consentService.GetConsentAsync(speakerAId, TestContext.Current.CancellationToken);
            VoiceCloneConsentRecord? consentB = await consentService.GetConsentAsync(speakerBId, TestContext.Current.CancellationToken);

            Assert.NotNull(consentA);
            Assert.Equal(projectA.Id, consentA.ProjectId);
            Assert.Equal(speakerAId, consentA.SpeakerId);
            Assert.False(consentA.IsThirdPartyConsent);
            Assert.Equal("Project A consent", consentA.Notes);

            Assert.NotNull(consentB);
            Assert.Equal(projectB.Id, consentB.ProjectId);
            Assert.Equal(speakerBId, consentB.SpeakerId);
            Assert.True(consentB.IsThirdPartyConsent);
            Assert.Equal("Project B consent", consentB.Notes);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }
}
