namespace Trackdub.Contracts;

public sealed record AudioPreviewSnapshot(
    bool IsLoaded,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsEnded,
    string? WarningMessage)
{
    public static AudioPreviewSnapshot Empty { get; } =
        new(false, false, TimeSpan.Zero, TimeSpan.Zero, false, null);
}

public interface IAudioPreviewTransport : IDisposable
{
    event EventHandler? Ended;

    Task OpenAsync(string absoluteFilePath, CancellationToken ct);

    Task PlayAsync(CancellationToken ct);

    Task PauseAsync(CancellationToken ct);

    Task SeekAsync(TimeSpan position, CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    Task<AudioPreviewSnapshot> GetSnapshotAsync(CancellationToken ct);
}
