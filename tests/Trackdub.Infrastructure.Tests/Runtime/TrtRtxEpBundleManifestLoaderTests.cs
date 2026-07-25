using Trackdub.Infrastructure.Runtime.TrtRtxEp;

namespace Trackdub.Infrastructure.Tests.Runtime;

public sealed class TrtRtxEpBundleManifestLoaderTests
{
    [Fact]
    public void Load_repo_manifest_has_required_schema_and_rid_entries()
    {
        string manifestPath = ResolveRepoManifestPath();
        Assert.True(File.Exists(manifestPath), $"Expected manifest at {manifestPath}");

        TrtRtxEpBundleManifest manifest = TrtRtxEpBundleManifestLoader.Load(manifestPath);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Version));
        Assert.False(string.IsNullOrWhiteSpace(manifest.CudaVariant));
        Assert.False(string.IsNullOrWhiteSpace(manifest.LicenseUrl));
        Assert.True(manifest.Packages.ContainsKey("win-x64"));
        Assert.True(manifest.Packages.ContainsKey("linux-x64"));

        foreach (string rid in new[] { "win-x64", "linux-x64" })
        {
            TrtRtxEpBundlePackage package = manifest.Packages[rid];
            Assert.False(string.IsNullOrWhiteSpace(package.ArchiveUrl));
            Assert.False(string.IsNullOrWhiteSpace(package.ArchiveKind));
            Assert.False(string.IsNullOrWhiteSpace(package.Sha256));
            Assert.True(package.SizeBytes > 0);
            Assert.StartsWith("https://", package.ArchiveUrl, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Load_missing_file_throws_file_not_found()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-trt-rtx-manifest-{Guid.NewGuid():N}.json");
        Assert.Throws<FileNotFoundException>(() => TrtRtxEpBundleManifestLoader.Load(missingPath));
    }

    [Fact]
    public void Load_invalid_schema_throws()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"trt-rtx-manifest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                tempPath,
                """
                {
                  "schemaVersion": 99,
                  "version": "0.0.0",
                  "cudaVariant": "cu12",
                  "licenseUrl": "https://example.com",
                  "packages": {
                    "win-x64": {
                      "archiveUrl": "https://example.com/a.zip",
                      "archiveKind": "zip",
                      "sha256": "abc",
                      "sizeBytes": 1
                    }
                  }
                }
                """);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => TrtRtxEpBundleManifestLoader.Load(tempPath));
            Assert.Contains("schemaVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string ResolveRepoManifestPath()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "runtime", "trt-rtx-ep.manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate runtime/trt-rtx-ep.manifest.json from test output directory.");
    }
}
