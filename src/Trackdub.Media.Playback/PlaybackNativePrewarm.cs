namespace Trackdub.Media.Playback;

/// <summary>
/// Fire-and-forget warm-up that loads libmpv into the OS page cache during app startup,
/// so the first video selection doesn't also pay a cold DLL-load cost.
/// </summary>
public static class PlaybackNativePrewarm
{
    public static void Start(ILibMpvRuntimeLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        _ = Task.Run(() =>
        {
            string? runtimeLibraryPath = locator.ResolveRuntimeLibraryPath();
            if (string.IsNullOrWhiteSpace(runtimeLibraryPath))
            {
                return;
            }

            try
            {
                LibMpvNativeLibrary.EnsureLoaded(runtimeLibraryPath);
            }
            catch (DllNotFoundException)
            {
                // Best-effort warm-up only; the real open path will retry and
                // fall back to LibVLC via AvaloniaPlaybackCapabilityProbe if this fails.
            }
        });
    }
}
