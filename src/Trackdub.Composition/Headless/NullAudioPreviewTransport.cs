using Trackdub.Contracts;

namespace Trackdub.Composition.Headless;

/// <summary>
/// No-op implementation of <see cref="IAudioPreviewTransport"/> for headless scenarios
/// where audio playback is not needed.
/// </summary>
internal sealed class NullAudioPreviewTransport : IAudioPreviewTransport
{
#pragma warning disable CS0067 // Event is never raised in this no-op implementation
    public event EventHandler? Ended;
#pragma warning restore CS0067

    public Task OpenAsync(string absoluteFilePath, CancellationToken ct) => Task.CompletedTask;

    public Task PlayAsync(CancellationToken ct) => Task.CompletedTask;

    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SeekAsync(TimeSpan position, CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<AudioPreviewSnapshot> GetSnapshotAsync(CancellationToken ct) =>
        Task.FromResult(AudioPreviewSnapshot.Empty);

    public void Dispose()
    {
        // No resources to release.
    }
}
