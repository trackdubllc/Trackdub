using Trackdub.Contracts;
using Trackdub.Application.Transcripts;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class TtsDubPreviewCoordinatorTests : IDisposable
{
    private readonly string tempDir;
    private readonly FakeAudioPreviewTransport transport;
    private readonly FakeArtifactStore store;
    private readonly TtsDubPreviewCoordinator coordinator;

    public TtsDubPreviewCoordinatorTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        transport = new FakeAudioPreviewTransport();
        store = new FakeArtifactStore(tempDir);
        coordinator = new TtsDubPreviewCoordinator(transport, store);
    }

    public void Dispose()
    {
        coordinator.Dispose();
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- Single take ---

    [Fact]
    public async Task OpenTakeAsync_ExistingArtifact_TransportOpensAndPlays()
    {
        store.Seed("artifacts/tts/take1.wav", new byte[44]);

        await coordinator.OpenTakeAsync("artifacts/tts/take1.wav", TestContext.Current.CancellationToken);
        await coordinator.PlayAsync(TestContext.Current.CancellationToken);

        AudioPreviewSnapshot snapshot = await coordinator.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(snapshot.IsLoaded);
        Assert.True(snapshot.IsPlaying);
        Assert.Null(snapshot.WarningMessage);
    }

    [Fact]
    public async Task OpenTakeAsync_MissingArtifact_SnapshotHasWarning()
    {
        await coordinator.OpenTakeAsync("artifacts/tts/missing.wav", TestContext.Current.CancellationToken);

        AudioPreviewSnapshot snapshot = await coordinator.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.False(snapshot.IsLoaded);
        Assert.NotNull(snapshot.WarningMessage);
        Assert.Contains("missing", snapshot.WarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    // --- Stop ---

    [Fact]
    public async Task StopAsync_ClearsActivePreview()
    {
        store.Seed("artifacts/tts/take1.wav", new byte[44]);
        await coordinator.OpenTakeAsync("artifacts/tts/take1.wav", TestContext.Current.CancellationToken);
        await coordinator.PlayAsync(TestContext.Current.CancellationToken);

        await coordinator.StopAsync(TestContext.Current.CancellationToken);

        AudioPreviewSnapshot snapshot = await coordinator.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.False(snapshot.IsLoaded);
        Assert.False(snapshot.IsPlaying);
    }

    // --- New preview cancels previous ---

    [Fact]
    public async Task OpenTakeAsync_WhilePlayingAnother_StopsFirst()
    {
        store.Seed("artifacts/tts/take1.wav", new byte[44]);
        store.Seed("artifacts/tts/take2.wav", new byte[44]);

        await coordinator.OpenTakeAsync("artifacts/tts/take1.wav", TestContext.Current.CancellationToken);
        await coordinator.PlayAsync(TestContext.Current.CancellationToken);

        // Opening a second take should stop the first
        await coordinator.OpenTakeAsync("artifacts/tts/take2.wav", TestContext.Current.CancellationToken);

        Assert.Equal(store.GetPath("artifacts/tts/take2.wav"), transport.LastOpenedPath);
    }

    // --- Sequence playback ---

    [Fact]
    public async Task OpenSequenceAsync_AllExisting_OpensFirstSegment()
    {
        store.Seed("artifacts/tts/take1.wav", new byte[44]);
        store.Seed("artifacts/tts/take2.wav", new byte[44]);
        store.Seed("artifacts/tts/take3.wav", new byte[44]);

        IReadOnlyList<TtsSegmentState> states =
        [
            MakeState(0, "artifacts/tts/take1.wav"),
            MakeState(1, "artifacts/tts/take2.wav"),
            MakeState(2, "artifacts/tts/take3.wav"),
        ];

        await coordinator.OpenSequenceAsync(states, TestContext.Current.CancellationToken);

        Assert.Equal(store.GetPath("artifacts/tts/take1.wav"), transport.LastOpenedPath);
    }

    [Fact]
    public async Task OpenSequenceAsync_TransportEnded_AdvancesToNextSegment()
    {
        store.Seed("artifacts/tts/take1.wav", new byte[44]);
        store.Seed("artifacts/tts/take2.wav", new byte[44]);

        IReadOnlyList<TtsSegmentState> states =
        [
            MakeState(0, "artifacts/tts/take1.wav"),
            MakeState(1, "artifacts/tts/take2.wav"),
        ];

        await coordinator.OpenSequenceAsync(states, TestContext.Current.CancellationToken);
        await coordinator.PlayAsync(TestContext.Current.CancellationToken);

        Task<string> nextOpen = transport.WaitForNextOpenAsync();
        transport.SimulateEnded();

        Assert.Equal(store.GetPath("artifacts/tts/take2.wav"), await nextOpen.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenSequenceAsync_MissingArtifact_SkipsAndSurfacesWarning()
    {
        store.Seed("artifacts/tts/take1.wav", new byte[44]);
        // take2 is NOT seeded — will be missing
        store.Seed("artifacts/tts/take3.wav", new byte[44]);

        IReadOnlyList<TtsSegmentState> states =
        [
            MakeState(0, "artifacts/tts/take1.wav"),
            MakeState(1, "artifacts/tts/take2.wav"),
            MakeState(2, "artifacts/tts/take3.wav"),
        ];

        await coordinator.OpenSequenceAsync(states, TestContext.Current.CancellationToken);
        AudioPreviewSnapshot snapshot = await coordinator.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsLoaded);
        Assert.NotNull(snapshot.WarningMessage);
        Assert.Contains("Skipped", snapshot.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenSequenceAsync_AllMissing_NotLoadedWithWarning()
    {
        IReadOnlyList<TtsSegmentState> states =
        [
            MakeState(0, "artifacts/tts/missing1.wav"),
            MakeState(1, "artifacts/tts/missing2.wav"),
        ];

        await coordinator.OpenSequenceAsync(states, TestContext.Current.CancellationToken);
        AudioPreviewSnapshot snapshot = await coordinator.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.IsLoaded);
    }

    [Fact]
    public async Task OpenSequenceAsync_SkipsStaleAndIncompleteTakes()
    {
        store.Seed("artifacts/tts/take1.wav", new byte[44]);

        IReadOnlyList<TtsSegmentState> states =
        [
            MakeState(0, "artifacts/tts/take1.wav"),
            MakeState(1, "artifacts/tts/take2.wav", isStale: true),
            MakeState(2, "artifacts/tts/take3.wav", status: TtsTakeStatus.Pending),
        ];

        await coordinator.OpenSequenceAsync(states, TestContext.Current.CancellationToken);
        AudioPreviewSnapshot snapshot = await coordinator.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(store.GetPath("artifacts/tts/take1.wav"), transport.LastOpenedPath);
        Assert.NotNull(snapshot.WarningMessage);
        Assert.Contains("Skipped 2", snapshot.WarningMessage, StringComparison.Ordinal);
    }

    // --- Dispose ---

    [Fact]
    public void Dispose_DisposesTransport()
    {
        coordinator.Dispose();
        Assert.True(transport.WasDisposed);
    }

    // --- Helpers ---

    private static TtsSegmentState MakeState(
        int index,
        string? relativePath,
        bool isStale = false,
        TtsTakeStatus status = TtsTakeStatus.Completed) =>
        new(
            SegmentIndex: index,
            TakeId: Guid.NewGuid(),
            ArtifactRelativePath: relativePath,
            Status: status,
            IsStale: isStale,
            DurationSeconds: 2.0,
            DurationOverrunRatio: null,
            HasDurationWarning: false,
            WarningMessage: null);
}
