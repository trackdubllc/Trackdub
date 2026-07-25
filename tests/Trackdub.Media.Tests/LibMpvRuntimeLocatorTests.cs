using System.Runtime.InteropServices;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

public sealed class LibMpvRuntimeLocatorTests
{
    [Fact]
    public void ResolveRuntimeLibraryPath_finds_library_under_base_directory_native_tree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "win-arm64"
            : "win-x64";

        string root = Path.Combine(Path.GetTempPath(), "trackdub-mpv-locator-" + Guid.NewGuid().ToString("N"));
        string bundledPath = Path.Combine(root, "native", rid, "libmpv-2.dll");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bundledPath)!);
            File.WriteAllBytes(bundledPath, [0x4D, 0x5A]);

            string? resolved = new LibMpvRuntimeLocator(root).ResolveRuntimeLibraryPath();

            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(bundledPath), resolved, ignoreCase: true);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void ResolveRuntimeLibraryPath_prefers_bundled_tree_over_local_app_data_when_both_exist()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "win-arm64"
            : "win-x64";

        string sandbox = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string appDataPath = Path.Combine(sandbox, "appdata", "Trackdub", "native", rid, "libmpv-2.dll");

        string root = Path.Combine(sandbox, "bundled");
        string bundledPath = Path.Combine(root, "native", rid, "libmpv-2.dll");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(appDataPath)!);
            File.WriteAllBytes(appDataPath, [0x4D, 0x5A]);

            Directory.CreateDirectory(Path.GetDirectoryName(bundledPath)!);
            File.WriteAllBytes(bundledPath, [0x4D, 0x5A]);

            string? resolved = new LibMpvRuntimeLocator(root).ResolveRuntimeLibraryPath();

            Assert.NotNull(resolved);
            Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }
        }
    }
}
