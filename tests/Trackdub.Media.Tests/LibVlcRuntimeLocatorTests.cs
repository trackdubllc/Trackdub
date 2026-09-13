using System.Runtime.InteropServices;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

public sealed class LibVlcRuntimeLocatorTests
{
    [Fact]
    public void ResolveRuntimePath_finds_library_directly_under_libvlc_directory()
    {
        (_, string libraryName, _) = GetPlatformFixture();

        string root = CreateTempRoot();
        string libvlcDir = Path.Combine(root, "libvlc");
        try
        {
            Directory.CreateDirectory(libvlcDir);
            File.WriteAllBytes(Path.Combine(libvlcDir, libraryName), [0x4D, 0x5A]);

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
        (string rid, string libraryName, string[] otherRids) = GetPlatformFixture();

        // Create non-matching RID folders first so EnumerateDirectories would
        // prefer the wrong architecture on creation-order filesystems. Matching
        // RID folder last reproduces the bug this test guards against.
        string root = CreateTempRoot();
        try
        {
            foreach (string other in otherRids.Where(candidate => candidate != rid))
            {
                string dir = Path.Combine(root, "libvlc", other);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, libraryName), [0x4D, 0x5A]);
            }

            string ridDir = Path.Combine(root, "libvlc", rid);
            Directory.CreateDirectory(ridDir);
            File.WriteAllBytes(Path.Combine(ridDir, libraryName), [0x4D, 0x5A]);

            string? resolved = new LibVlcRuntimeLocator(root).ResolveRuntimePath();

            Assert.Equal(Path.GetFullPath(ridDir), resolved, ignoreCase: true);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static (string Rid, string LibraryName, string[] OtherRids) GetPlatformFixture()
    {
        if (OperatingSystem.IsWindows())
        {
            string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "win-arm64"
                : "win-x64";
            return (rid, "libvlc.dll", ["win-arm64", "win-x64", "win-x86"]);
        }

        if (OperatingSystem.IsMacOS())
        {
            string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
            return (rid, "libvlc.dylib", ["osx-arm64", "osx-x64"]);
        }

        string linuxRid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-arm64"
            : "linux-x64";
        return (linuxRid, "libvlc.so", ["linux-arm64", "linux-x64"]);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Failed to delete temporary test directory '{root}': {ex.Message}");
        }
    }
}
