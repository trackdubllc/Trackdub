using Trackdub.Contracts;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed class TtsDubPreviewCoordinator(
    IAudioPreviewTransport transport,
    IArtifactStore artifactStore,
    TtsCandidateSelectionService? candidateSelectionService = null)
    : IDisposable
{
    private readonly IAudioPreviewTransport transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly SemaphoreSlim sequenceLock = new(1, 1);

    private bool inSequenceMode;
    private int sequenceIndex;
    private IReadOnlyList<string> sequencePaths = [];
    private string? skipWarning;
    private int sequenceRevision;
    private bool disposed;
    private int disposeStarted;
    private CancellationTokenSource sequenceCancellation = new();

    public async Task OpenTakeAsync(string artifactRelativePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRelativePath);

        CancelSequenceAdvance();
        await sequenceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(ct).ConfigureAwait(false);

            string absolutePath = artifactStore.GetPath(artifactRelativePath);
            if (!File.Exists(absolutePath))
            {
                skipWarning = $"TTS artifact file is missing: {absolutePath}";
                return;
            }

            await transport.OpenAsync(absolutePath, ct).ConfigureAwait(false);
        }
        finally
        {
            sequenceLock.Release();
        }
    }

    public async Task OpenSequenceAsync(IReadOnlyList<TtsSegmentState> states, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(states);

        CancelSequenceAdvance();
        await sequenceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(ct).ConfigureAwait(false);

            List<string> paths = [];
            List<int> skipped = [];

            foreach (TtsSegmentState state in states.OrderBy(s => s.SegmentIndex))
            {
                if (state.Status is not TtsTakeStatus.Completed ||
                    state.IsStale ||
                    string.IsNullOrWhiteSpace(state.ArtifactRelativePath))
                {
                    skipped.Add(state.SegmentIndex);
                    continue;
                }

                string absolutePath = artifactStore.GetPath(state.ArtifactRelativePath);
                if (File.Exists(absolutePath))
                {
                    paths.Add(absolutePath);
                }
                else
                {
                    skipped.Add(state.SegmentIndex);
                }
            }

            skipWarning = skipped.Count > 0
                ? $"Skipped {skipped.Count} segment(s) without playable TTS takes: {string.Join(", ", skipped)}."
                : null;

            if (paths.Count == 0)
            {
                return;
            }

            sequencePaths = paths;
            sequenceIndex = 0;
            inSequenceMode = true;
            transport.Ended += OnTransportEnded;
            CancellationTokenSource currentSequenceCancellation = sequenceCancellation;

            try
            {
                using CancellationTokenSource linkedCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, currentSequenceCancellation.Token);
                await transport.OpenAsync(sequencePaths[0], linkedCancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                ClearSequenceState();
                throw;
            }
        }
        finally
        {
            sequenceLock.Release();
        }
    }

    public Task PlayAsync(CancellationToken ct) => transport.PlayAsync(ct);

    public Task PauseAsync(CancellationToken ct) => transport.PauseAsync(ct);

    public Task SeekAsync(TimeSpan position, CancellationToken ct) => transport.SeekAsync(position, ct);

    public async Task StopAsync(CancellationToken ct)
    {
        CancelSequenceAdvance();
        await sequenceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            sequenceLock.Release();
        }
    }

    public async Task SwitchCandidateAsync(Guid translatedSegmentId, Guid candidateId, CancellationToken ct)
    {
        if (candidateSelectionService is null)
            throw new InvalidOperationException("Candidate selection service is not configured.");
        await candidateSelectionService.SelectCandidateAsync(translatedSegmentId, candidateId, ct).ConfigureAwait(false);
        string? relativePath = await candidateSelectionService
            .GetSelectedCandidateRelativePathAsync(translatedSegmentId, ct).ConfigureAwait(false);
        if (relativePath is not null)
            await OpenTakeAsync(relativePath, ct).ConfigureAwait(false);
    }

    public async Task ReloadCurrentSegmentAsync(Guid translatedSegmentId, CancellationToken ct)
    {
        if (candidateSelectionService is null) return;
        string? relativePath = await candidateSelectionService
            .GetSelectedCandidateRelativePathAsync(translatedSegmentId, ct).ConfigureAwait(false);
        if (relativePath is not null)
            await OpenTakeAsync(relativePath, ct).ConfigureAwait(false);
    }

    public async Task<AudioPreviewSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        AudioPreviewSnapshot snapshot = await transport.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (skipWarning is null)
        {
            return snapshot;
        }

        string combined = snapshot.WarningMessage is null
            ? skipWarning
            : $"{snapshot.WarningMessage} {skipWarning}";
        return snapshot with { WarningMessage = combined };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        CancelSequenceAdvance();
        if (!sequenceLock.Wait(TimeSpan.FromSeconds(10)))
        {
            // Timeout — a concurrent async operation is still using the coordinator.
            // Proceed with disposal anyway; the concurrent operation will observe
            // disposeStarted=1 and disposed state.
            sequenceLock.Release();
            sequenceLock.Dispose();
            return;
        }
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ClearSequenceState();
            sequenceCancellation.Dispose();
            transport.Dispose();
        }
        finally
        {
            sequenceLock.Release();
            sequenceLock.Dispose();
        }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        ClearSequenceState();
        await transport.StopAsync(ct).ConfigureAwait(false);
    }

    private void ClearSequenceState()
    {
        if (inSequenceMode)
        {
            transport.Ended -= OnTransportEnded;
            inSequenceMode = false;
        }

        sequenceIndex = 0;
        sequencePaths = [];
        skipWarning = null;
        sequenceRevision++;
        sequenceCancellation.Dispose();
        sequenceCancellation = new CancellationTokenSource();
    }

    private void OnTransportEnded(object? sender, EventArgs e)
    {
        if (disposed || Volatile.Read(ref disposeStarted) != 0)
        {
            return;
        }

        int expectedRevision = sequenceRevision;
        CancellationTokenSource sequenceCancellationSource = sequenceCancellation;
        CancellationToken advanceToken;
        try
        {
            advanceToken = sequenceCancellationSource.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = TryAdvanceAsync(expectedRevision, advanceToken);
    }

    private async Task TryAdvanceAsync(int expectedRevision, CancellationToken ct)
    {
        try
        {
            await sequenceLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (disposed || !inSequenceMode || expectedRevision != sequenceRevision)
                {
                    return;
                }

                int nextIndex = sequenceIndex + 1;
                if (nextIndex >= sequencePaths.Count)
                {
                    return;
                }

                sequenceIndex = nextIndex;
                try
                {
                    await transport.OpenAsync(sequencePaths[sequenceIndex], ct).ConfigureAwait(false);
                    await transport.PlayAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ClearSequenceState();
                    skipWarning = $"Sequence preview stopped while opening the next TTS take: {ex.Message}";
                }
            }
            finally
            {
                sequenceLock.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // The coordinator was disposed while we were trying to acquire the
            // lock or release it. Disposal already clears sequence state under
            // the lock, so there is nothing more to do here — and mutating
            // shared state without the lock would race with other callers.
        }
        catch (OperationCanceledException)
        {
            // A stop, new preview request, or disposal canceled the queued advance.
        }
    }

    private void CancelSequenceAdvance()
    {
        try
        {
            sequenceCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

}
