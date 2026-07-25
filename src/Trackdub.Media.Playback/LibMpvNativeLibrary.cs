using System.Runtime.InteropServices;

namespace Trackdub.Media.Playback;

/// <summary>
/// Process-wide libmpv native library handle. libmpv is not reliably safe to
/// <see cref="NativeLibrary.Free"/> and reload on Windows between back-to-back
/// project opens, so we keep one load for the app lifetime.
/// </summary>
internal static class LibMpvNativeLibrary
{
    private static readonly object Sync = new();
    private static IntPtr handle;
    private static string? loadedPath;

    public static IntPtr EnsureLoaded(string runtimeLibraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeLibraryPath);

        lock (Sync)
        {
            if (handle != IntPtr.Zero)
            {
                return handle;
            }

            handle = OperatingSystem.IsWindows()
                ? LoadWindowsLibrary(runtimeLibraryPath)
                : NativeLibrary.Load(runtimeLibraryPath);
            loadedPath = runtimeLibraryPath;
            return handle;
        }
    }

    private static IntPtr LoadWindowsLibrary(string libraryPath)
    {
        const uint loadLibrarySearchDefaultDirs = 0x00001000;
        const uint loadLibrarySearchDllLoadDir = 0x00000100;
        IntPtr loaded = LoadLibraryExW(
            libraryPath,
            IntPtr.Zero,
            loadLibrarySearchDefaultDirs | loadLibrarySearchDllLoadDir);
        if (loaded == IntPtr.Zero)
        {
            throw new DllNotFoundException(
                $"Failed to load libmpv from '{libraryPath}'. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        return loaded;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);
}
