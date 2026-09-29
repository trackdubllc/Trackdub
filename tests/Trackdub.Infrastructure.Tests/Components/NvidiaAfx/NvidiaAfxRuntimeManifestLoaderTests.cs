using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Infrastructure.Tests.Components.NvidiaAfx;

public sealed class NvidiaAfxRuntimeManifestLoaderTests
{
    [Fact]
    public void Load_binds_camel_case_manifest_fields()
    {
        NvidiaAfxRuntimeManifest manifest =
            NvidiaAfxRuntimeManifestLoader.Load(ResolveFixturePath("nvidia-afx-runtime.manifest.json"));

        Assert.Equal("1.0.0", manifest.ManifestVersion);
        Assert.Equal(2, manifest.Packages.Length);

        NvidiaAfxRuntimePackage winX64 = manifest.Packages[0];
        Assert.Equal("win-x64", winX64.Architecture);
        Assert.Equal("https://example.com/afx-runtime-win-x64.zip", winX64.DownloadUrl);
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef", winX64.Sha256);
        Assert.Equal(1_234_567_890L, winX64.SizeBytes);
        Assert.Equal("1.2.3", winX64.RuntimeVersion);
        Assert.Equal("https://example.com/afx-license", winX64.LicenseUrl);

        string[] expectedModelPaths = ["models/afx/win-x64/encoder.onnx", "models/afx/win-x64/decoder.onnx"];
        Assert.Equal(expectedModelPaths, winX64.ModelRelativePaths);

        NvidiaAfxRuntimePackage linuxX64 = manifest.Packages[1];
        Assert.Equal("linux-x64", linuxX64.Architecture);
        Assert.Equal("feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface", linuxX64.Sha256);
        Assert.Equal(987_654_321L, linuxX64.SizeBytes);

        string[] expectedLinuxModelPaths = ["models/afx/linux-x64/encoder.onnx"];
        Assert.Equal(expectedLinuxModelPaths, linuxX64.ModelRelativePaths);
    }

    [Fact]
    public void Load_missing_file_throws_file_not_found()
    {
        string missingPath = Path.Join(Path.GetTempPath(), $"missing-nvidia-afx-manifest-{Guid.NewGuid():N}.json");

        Assert.Throws<FileNotFoundException>(() => NvidiaAfxRuntimeManifestLoader.Load(missingPath));
    }

    [Theory]
    [InlineData("""{ "manifestVersion": "1.0.0" }""")]
    [InlineData("""{ "manifestVersion": "1.0.0", "packages": [] }""")]
    public void Load_without_packages_throws(string json)
    {
        string tempPath = Path.Join(Path.GetTempPath(), $"nvidia-afx-manifest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, json);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => NvidiaAfxRuntimeManifestLoader.Load(tempPath));
            Assert.Contains("packages", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string ResolveFixturePath(string fileName)
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            string candidate = Path.Join(directory, "tests", "Trackdub.Infrastructure.Tests", "Fixtures", "NvidiaAfx", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate the NVIDIA AFX fixture '{fileName}' from the test output directory.");
    }
}
