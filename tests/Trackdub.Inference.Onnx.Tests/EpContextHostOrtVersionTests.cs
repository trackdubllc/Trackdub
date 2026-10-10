using System.Xml.Linq;
using Trackdub.Inference.Onnx.EpContext;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class EpContextHostOrtVersionTests
{
    [Fact]
    public void Host_ort_runtime_version_comes_from_the_central_package_pin()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Join(directory, "Directory.Packages.props")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        XElement properties = XDocument.Load(Path.Join(directory, "Directory.Packages.props")).Root!;
        // The Windows TFM build runs on Windows ML's native ORT; the portable build on stock ORT.
        bool windowsTarget = typeof(EpContextArtifact).Assembly
            .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetPlatformAttribute), inherit: false)
            .Cast<System.Runtime.Versioning.TargetPlatformAttribute>()
            .Any(attribute => attribute.PlatformName.StartsWith("Windows", StringComparison.OrdinalIgnoreCase));
        string property = windowsTarget ? "WindowsMlOnnxRuntimeVersion" : "OnnxRuntimeVersion";
        string expected = properties.Descendants(property).Single().Value.Trim();

        Assert.Equal(expected, EpContextArtifact.HostOrtRuntimeVersion);
    }
}
