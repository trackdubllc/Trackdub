using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Infrastructure.FileSystem;

namespace Trackdub.Infrastructure.Tests;

public sealed class FileSystemVoiceCloneAuditLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Trackdub.AuditTests", Guid.NewGuid().ToString("N"));

    public FileSystemVoiceCloneAuditLogTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task VerifyAsync_valid_chain_succeeds()
    {
        var log = new FileSystemVoiceCloneAuditLog(new FakeWorkspaceContext(root));
        await log.AppendAsync(CreateEntry(), TestContext.Current.CancellationToken);
        await log.AppendAsync(CreateEntry(), TestContext.Current.CancellationToken);

        VoiceCloneAuditVerificationResult result = await log.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.EntryCount);
    }

    [Fact]
    public async Task AppendAsync_multiple_entries_form_valid_chain_without_verify_on_each_append()
    {
        // Appends use the in-memory hash cache; the chain must still be valid when
        // verified externally after all writes complete.
        var log = new FileSystemVoiceCloneAuditLog(new FakeWorkspaceContext(root));
        for (int i = 0; i < 5; i++)
        {
            await log.AppendAsync(CreateEntry(), TestContext.Current.CancellationToken);
        }

        VoiceCloneAuditVerificationResult result = await log.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(5, result.EntryCount);
    }

    [Fact]
    public async Task AppendAsync_new_log_instance_continues_chain_from_existing_file()
    {
        // Simulate session restart: a new log object should seed its hash cache from
        // the persisted file so the chain remains valid across instances.
        var ctx = new FakeWorkspaceContext(root);
        var first = new FileSystemVoiceCloneAuditLog(ctx);
        await first.AppendAsync(CreateEntry(), TestContext.Current.CancellationToken);
        await first.AppendAsync(CreateEntry(), TestContext.Current.CancellationToken);

        var second = new FileSystemVoiceCloneAuditLog(ctx);
        await second.AppendAsync(CreateEntry(), TestContext.Current.CancellationToken);

        VoiceCloneAuditVerificationResult result = await second.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.EntryCount);
    }

    [Fact]
    public async Task VerifyAsync_detects_tampered_entry()
    {
        var firstEntry = CreateEntry();
        var log = new FileSystemVoiceCloneAuditLog(new FakeWorkspaceContext(root));
        await log.AppendAsync(firstEntry, TestContext.Current.CancellationToken);
        await log.AppendAsync(CreateEntry(), TestContext.Current.CancellationToken);

        string path = Path.Combine(root, ProjectArtifactPaths.VoiceCloneAuditRelativePath);
        string[] lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        lines[0] = lines[0].Replace(
            firstEntry.ReferenceClipArtifactId.ToString("D"),
            Guid.NewGuid().ToString("D"),
            StringComparison.Ordinal);
        await File.WriteAllLinesAsync(path, lines, TestContext.Current.CancellationToken);

        VoiceCloneAuditVerificationResult result = await log.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains("hash", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static VoiceCloneAuditEntry CreateEntry() =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

    private sealed class FakeWorkspaceContext(string projectRootPath) : ITranscriptWorkspaceContext
    {
        public string ProjectRootPath { get; } = projectRootPath;

        public StudioSettings Settings => StudioSettings.Default;
    }
}
