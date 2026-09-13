using System.Runtime.InteropServices;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

public sealed class LibVlcRuntimeLocatorTests
{
    [Fact]
    public void ResolveRuntimePath_finds_library_directly_under_libvlc_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTempRoot();
        string libvlcDir = Path.Combine(root, "libvlc");
        try
        {
            Directory.CreateDirectory(libvlcDir);
            File.WriteAllBytes(Path.Combine(libvlcDir, "libvlc.dll"), [0x4D, 0x5A]);

            string? resolved = new LibVlcRuntimeLocator(root).ResolveRuntimePath();

            Assert.Equal(Path.GetFullPath(libvlcDir), resolved, ignoreCase: true);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ResolveRuntimePath_prefers_current_rid_subfolder_over_other_architectures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "win-arm64"
            : "win-x64";

        // NuGet runtime packages ship every architecture side by side. The first
        // enumerated subfolder must not win over the one matching this process.
        string[] otherRids = ["win-arm64", "win-x64", "win-x86"];

        string root = CreateTempRoot();
        string? ridDir = null;
        try
        {
            foreach (string other in otherRids)
            {
                string dir = Path.Combine(root, "libvlc", other);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "libvlc.dll"), [0x4D, 0x5A]);
                if (other == rid)
                {
                    ridDir = dir;
                }
            }

            string? resolved = new LibVlcRuntimeLocator(root).ResolveRuntimePath();

            Assert.NotNull(ridDir);
            Assert.Equal(Path.GetFullPath(ridDir!), resolved, ignoreCase: true);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string CreateTempRoot()
    {
        return Path.Combine(Path.GetTempPath(), "trackdub-vlc-locator-" + Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Failed to delete temporary test directory '{root}': {ex.Message}");
        }
    }
}
