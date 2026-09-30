using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Infrastructure.Tests.Components.NvidiaAfx;

public sealed class NvidiaAfxRuntimePackageGatesTests
{
    [Fact]
    public void IsDownloadable_RejectsPlaceholderManifestPackages()
    {
        var package = new NvidiaAfxRuntimePackage(
            Architecture: "ada",
            DownloadUrl: "https://example.invalid/trackdub/nvidia-afx/ada/runtime.zip",
            Sha256: "0000000000000000000000000000000000000000000000000000000000000000",
            SizeBytes: 0,
            RuntimeVersion: "0.0.0",
            LicenseUrl: NvidiaAfxIntegrationLicenseUrl(),
            ModelRelativePaths: ["models/denoiser_48k.nvam"]);

        Assert.False(NvidiaAfxRuntimePackageGates.IsDownloadable(package));
        Assert.Contains("placeholder", NvidiaAfxRuntimePackageGates.DescribeBlocker(package), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsDownloadable_AcceptsVerifiedHttpsPackageMetadata()
    {
        var package = new NvidiaAfxRuntimePackage(
            Architecture: "ada",
            DownloadUrl: "https://cdn.example.com/trackdub/nvidia-afx/ada/runtime.zip",
            Sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            SizeBytes: 1_048_576,
            RuntimeVersion: "1.2.3",
            LicenseUrl: NvidiaAfxIntegrationLicenseUrl(),
            ModelRelativePaths: ["models/denoiser_48k.nvam"]);

        Assert.True(NvidiaAfxRuntimePackageGates.IsDownloadable(package));
    }

    [Fact]
    public void BundledManifest_HasNoDownloadablePackages()
    {
        string manifestPath = ResolveBundledManifestPath();
        NvidiaAfxRuntimeManifest manifest = NvidiaAfxRuntimeManifestLoader.Load(manifestPath);
        Assert.False(NvidiaAfxRuntimePackageGates.ManifestHasAnyDownloadablePackage(manifest));
    }

    private static string NvidiaAfxIntegrationLicenseUrl() =>
        "https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-software-license-agreement/";

    private static string ResolveBundledManifestPath()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            string candidate = Path.Join(directory, "src", "Trackdub.Composition", "nvidiaafx-runtime.manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate nvidiaafx-runtime.manifest.json from the test output directory.");
    }
}
