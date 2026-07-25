using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeAudioPreviewTransport : IAudioPreviewTransport
{
    private AudioPreviewSnapshot snapshot = AudioPreviewSnapshot.Empty with
    {
        IsLoaded = false
    };

    public string? WarningOnOpen { get; set; }

    public bool WasDisposed { get; private set; }

    public string? LastOpenedPath { get; private set; }

    private TaskCompletionSource<string>? nextOpen;

    public event EventHandler? Ended;

    public Task OpenAsync(string absoluteFilePath, CancellationToken ct)
    {
        LastOpenedPath = absoluteFilePath;
        nextOpen?.TrySetResult(absoluteFilePath);
        nextOpen = null;
        bool loaded = string.IsNullOrWhiteSpace(WarningOnOpen);
        snapshot = AudioPreviewSnapshot.Empty with
        {
            IsLoaded = loaded,
            IsPlaying = false,
            IsEnded = false,
            Duration = loaded ? TimeSpan.FromSeconds(5) : TimeSpan.Zero,
            WarningMessage = WarningOnOpen
        };
        return Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken ct)
    {
        if (!snapshot.IsLoaded)
        {
            return Task.CompletedTask;
        }

        snapshot = snapshot with { IsPlaying = true };
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        snapshot = snapshot with { IsPlaying = false };
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        snapshot = snapshot with { Position = position };
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        snapshot = AudioPreviewSnapshot.Empty;
        LastOpenedPath = null;
        nextOpen?.TrySetCanceled();
        nextOpen = null;
        return Task.CompletedTask;
    }

    public Task<AudioPreviewSnapshot> GetSnapshotAsync(CancellationToken ct) =>
        Task.FromResult(snapshot);

    public Task<string> WaitForNextOpenAsync()
    {
        if (nextOpen is not null)
        {
            return nextOpen.Task;
        }

        nextOpen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        return nextOpen.Task;
    }

    public void SimulateEnded()
    {
        snapshot = snapshot with { IsEnded = true, IsPlaying = false };
        Ended?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        nextOpen?.TrySetCanceled();
        nextOpen = null;
        WasDisposed = true;
    }
}
