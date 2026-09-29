using System.Text;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

public sealed class WinNativeDepsManifestLoaderTests
{
    [Fact]
    public void TryLoadFromDirectory_tolerates_comments_trailing_commas_and_camel_case_keys()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestDirectory = Path.Join(root, "runtime");
            Directory.CreateDirectory(manifestDirectory);
            File.Copy(
                ResolveFixturePath("win-native-deps.manifest.json"),
                Path.Join(manifestDirectory, "win-native-deps.manifest.json"));

            WinNativeDepsManifestRoot? manifest = WinNativeDepsManifestLoader.TryLoadFromDirectory(root);

            Assert.NotNull(manifest);
            Assert.Equal(1, manifest.SchemaVersion);
            Assert.Equal("https://example.com/7zr.exe", manifest.SevenZipPortableExeUrl);

            Dictionary<string, WinNativeDepsRuntimeEntry> runtimes =
                Assert.IsType<Dictionary<string, WinNativeDepsRuntimeEntry>>(manifest.Runtimes);
            Assert.True(runtimes.ContainsKey("win-x64"));
            Assert.True(runtimes.ContainsKey("osx-arm64"));

            WinNativeDepsRuntimeEntry winX64 = runtimes["win-x64"];
            Assert.Equal("https://example.com/libmpv-dev-win-x64.zip", winX64.LibmpvDevArchiveUrl);
            Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef", winX64.LibmpvDevArchiveSha256);
            Assert.Equal("libmpv-dev/include/mpv/client.h", winX64.LibmpvExtractMember);
            Assert.Equal("https://example.com/ffmpeg-win-x64.zip", winX64.FfmpegZipUrl);
            Assert.Equal("https://example.com/uv-win-x64.zip", winX64.UvZipUrl);

            WinNativeDepsRuntimeEntry osxArm64 = runtimes["osx-arm64"];
            Assert.Equal("https://example.com/libmpv-dev-osx-arm64.tar.gz", osxArm64.LibmpvDevArchiveUrl);
            Assert.Null(osxArm64.LibmpvDevArchiveSha256);
            Assert.Null(osxArm64.FfmpegZipUrl);
            Assert.Null(osxArm64.UvZipUrl);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void TryLoadFromDirectory_prefers_the_runtime_subdirectory_candidate()
    {
        string root = CreateTempDirectory();
        try
        {
            WriteManifest(root, "runtime", MinimalManifest("https://example.com/from-runtime"));
            WriteManifest(root, ".", MinimalManifest("https://example.com/from-app-root"));

            WinNativeDepsManifestRoot? manifest = WinNativeDepsManifestLoader.TryLoadFromDirectory(root);

            Assert.NotNull(manifest);
            Assert.Equal("https://example.com/from-runtime", manifest.SevenZipPortableExeUrl);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void TryLoadFromDirectory_returns_null_when_no_manifest_is_present()
    {
        string root = CreateTempDirectory();
        try
        {
            Assert.Null(WinNativeDepsManifestLoader.TryLoadFromDirectory(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "runtimes": {} }""")]
    [InlineData("null")]
    public void TryLoadFromDirectory_returns_null_for_unsupported_payload(string json)
    {
        string root = CreateTempDirectory();
        try
        {
            WriteManifest(root, "runtime", json);

            Assert.Null(WinNativeDepsManifestLoader.TryLoadFromDirectory(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void TryLoadFromDirectory_returns_null_for_malformed_json()
    {
        string root = CreateTempDirectory();
        try
        {
            WriteManifest(root, "runtime", """{ "schemaVersion": 1,""");

            Assert.Null(WinNativeDepsManifestLoader.TryLoadFromDirectory(root));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void TryLoadFromDirectory_matches_property_names_case_insensitively()
    {
        // The canonical keys are camelCase (naming policy), but the loader also accepts other casings:
        // the shipped manifest is hand-edited, so the generated metadata must stay case-insensitive.
        string root = CreateTempDirectory();
        try
        {
            WriteManifest(
                root,
                "runtime",
                """
                {
                  "SchemaVersion": 1,
                  "SevenZipPortableExeUrl": "https://example.com/pascal-case-7zr.exe",
                  "Runtimes": {
                    "win-x64": {
                      "FFMPEGZIPURL": "https://example.com/uppercase-ffmpeg.zip"
                    }
                  }
                }
                """);

            WinNativeDepsManifestRoot? manifest = WinNativeDepsManifestLoader.TryLoadFromDirectory(root);

            Assert.NotNull(manifest);
            Assert.Equal(1, manifest.SchemaVersion);
            Assert.Equal("https://example.com/pascal-case-7zr.exe", manifest.SevenZipPortableExeUrl);

            Dictionary<string, WinNativeDepsRuntimeEntry> runtimes =
                Assert.IsType<Dictionary<string, WinNativeDepsRuntimeEntry>>(manifest.Runtimes);
            Assert.Equal("https://example.com/uppercase-ffmpeg.zip", runtimes["win-x64"].FfmpegZipUrl);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    // The manifest is read as text, which drops a byte order mark, and the tolerant reader already
    // accepts the rest: a copy re-saved by a Windows editor still resolves.
    [Fact]
    public void TryLoadFromDirectory_reads_a_manifest_re_saved_with_a_utf8_bom_and_windows_newlines()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestDirectory = Path.Join(root, "runtime");
            Directory.CreateDirectory(manifestDirectory);
            string json = File.ReadAllText(ResolveFixturePath("win-native-deps.manifest.json"))
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);
            File.WriteAllBytes(
                Path.Join(manifestDirectory, "win-native-deps.manifest.json"),
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]);

            WinNativeDepsManifestRoot? manifest = WinNativeDepsManifestLoader.TryLoadFromDirectory(root);

            Assert.NotNull(manifest);
            Assert.Equal(1, manifest.SchemaVersion);
            Dictionary<string, WinNativeDepsRuntimeEntry> runtimes =
                Assert.IsType<Dictionary<string, WinNativeDepsRuntimeEntry>>(manifest.Runtimes);
            Assert.True(runtimes.ContainsKey("win-x64"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Join(Path.GetTempPath(), "trackdub-win-native-deps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a file still held by the loader must not fail the test.
        }
    }

    private static void WriteManifest(string root, string relativeDirectory, string json)
    {
        string directory = relativeDirectory == "." ? root : Path.Join(root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, "win-native-deps.manifest.json"), json);
    }

    private static string MinimalManifest(string sevenZipPortableExeUrl) =>
        $$"""
        {
          "schemaVersion": 1,
          "sevenZipPortableExeUrl": "{{sevenZipPortableExeUrl}}"
        }
        """;

    private static string ResolveFixturePath(string fileName)
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            string candidate = Path.Join(directory, "tests", "Trackdub.Media.Tests", "Fixtures", "WinNativeDeps", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate the WinNativeDeps fixture '{fileName}' from the test output directory.");
    }
}
