using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Application.Transcripts;
using Trackdub.Domain.Artifacts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class PipelineDegradationWriterTests
{
    private readonly FakeArtifactStore artifactStore = new();
    private readonly FakeFileFingerprintService fingerprintService = new(new FileFingerprint("abc123sha256", 512, DateTimeOffset.UnixEpoch));
    private readonly FakeMediaAssetRepository mediaAssetRepository = new();
    private readonly PipelineDegradationWriter writer;

    private readonly Guid projectId = Guid.NewGuid();
    private readonly Guid mediaAssetId = Guid.NewGuid();
    private readonly Guid stageRunId = Guid.NewGuid();

    public PipelineDegradationWriterTests()
    {
        writer = new PipelineDegradationWriter(artifactStore, fingerprintService, mediaAssetRepository);
    }

    private PipelineDegradationRecord MakeRecord(string code = "TEST_CODE", string stage = "TestStage") =>
        new(
            Stage: stage,
            Code: code,
            Message: "Something degraded.",
            Detail: "extra detail",
            SelectedFallback: "fallback-value",
            RecommendedAction: "Check the thing.",
            OccurredAtUtc: new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero),
            StageRunId: stageRunId);

    [Fact]
    public async Task WriteAsync_WritesJsonBlob_ThatDeserializesCorrectly()
    {
        PipelineDegradationRecord record = MakeRecord();

        await writer.WriteAsync(record, projectId, mediaAssetId, TestContext.Current.CancellationToken);

        // One blob should exist in the artifact store.
        Assert.Single(artifactStore.Blobs);

        byte[] json = artifactStore.Blobs.Values.Single();
        Assert.NotEmpty(json);

        PipelineDegradationRecord? deserialized = JsonSerializer.Deserialize<PipelineDegradationRecord>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(record.Code, deserialized.Code);
        Assert.Equal(record.Stage, deserialized.Stage);
        Assert.Equal(record.Message, deserialized.Message);
        Assert.Equal(record.Detail, deserialized.Detail);
        Assert.Equal(record.SelectedFallback, deserialized.SelectedFallback);
        Assert.Equal(record.RecommendedAction, deserialized.RecommendedAction);
        Assert.Equal(record.OccurredAtUtc, deserialized.OccurredAtUtc);
        Assert.Equal(record.StageRunId, deserialized.StageRunId);
    }

    [Fact]
    public async Task WriteAsync_PersistsArtifact_WithCorrectKindAndPath()
    {
        await writer.WriteAsync(MakeRecord(), projectId, mediaAssetId, TestContext.Current.CancellationToken);

        ProjectArtifact artifact = Assert.Single(mediaAssetRepository.Artifacts);
        Assert.Equal(ArtifactKind.PipelineDegradation, artifact.Kind);
        Assert.Equal(projectId, artifact.ProjectId);
        Assert.Equal(mediaAssetId, artifact.MediaAssetId);
        Assert.StartsWith("artifacts/degradation/", artifact.RelativePath);
        Assert.EndsWith(".json", artifact.RelativePath);
    }

    [Fact]
    public async Task WriteAsync_PersistsArtifact_WithDegradationCodeAndStage()
    {
        PipelineDegradationRecord record = MakeRecord(code: "MY_CODE", stage: "MyStage");

        await writer.WriteAsync(record, projectId, mediaAssetId, TestContext.Current.CancellationToken);

        ProjectArtifact artifact = Assert.Single(mediaAssetRepository.Artifacts);
        Assert.Equal("MY_CODE", artifact.DegradationCode);
        Assert.Equal("MyStage", artifact.DegradationStage);
        Assert.Equal("MY_CODE", artifact.Provenance);
        Assert.Equal(stageRunId, artifact.StageRunId);
    }

    [Fact]
    public async Task WriteAsync_PersistsArtifact_WithFingerprintFromService()
    {
        await writer.WriteAsync(MakeRecord(), projectId, mediaAssetId, TestContext.Current.CancellationToken);

        ProjectArtifact artifact = Assert.Single(mediaAssetRepository.Artifacts);
        Assert.Equal("abc123sha256", artifact.Sha256);
        Assert.Equal(512L, artifact.SizeBytes);
    }

    [Fact]
    public async Task WriteAsync_PersistsArtifact_WithCreatedAtUtcFromOccurredAt()
    {
        // CreatedAtUtc must reflect when the degradation occurred, not when it was written,
        // so that incident timelines remain accurate even under delayed/retried persistence.
        PipelineDegradationRecord record = MakeRecord();

        await writer.WriteAsync(record, projectId, mediaAssetId, TestContext.Current.CancellationToken);

        ProjectArtifact artifact = Assert.Single(mediaAssetRepository.Artifacts);
        Assert.Equal(record.OccurredAtUtc, artifact.CreatedAtUtc);
    }

    [Fact]
    public async Task WriteAsync_ThrowsArgumentNull_WhenRecordIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => writer.WriteAsync(null!, projectId, mediaAssetId, TestContext.Current.CancellationToken));
    }
}
