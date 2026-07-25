using Trackdub.Inference.Onnx;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class PlannedRuntimeModelResolverTests
{
    [Fact]
    public void ResolveModelRootPath_WhenEntryPathIsNestedGenAiConfig_UsesPackageDirectory()
    {
        using TempDirectoryFixture fixture = new();
        string packageRoot = Path.Combine(fixture.RootPath, "cpu_and_mobile", "cpu-int4");
        Directory.CreateDirectory(packageRoot);
        string genAiConfigPath = Path.Combine(packageRoot, "genai_config.json");
        File.WriteAllText(genAiConfigPath, "{}");

        var plan = new StageRuntimePlan
        {
            ModelAlias = "phi-3.5-mini-genai",
            ModelEntryPath = genAiConfigPath,
            ModelRootPath = fixture.RootPath,
        };

        string resolvedRoot = PlannedRuntimeModelResolver.ResolveModelRootPath(
            plan,
            new BenchmarkModelPathResolver(manifestRegistry: null, modelCacheDirectory: null));

        Assert.Equal(packageRoot, resolvedRoot, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveModelRootPath_WhenEntryPathIsRootGenAiConfig_UsesCacheRoot()
    {
        using TempDirectoryFixture fixture = new();
        string genAiConfigPath = Path.Combine(fixture.RootPath, "genai_config.json");
        File.WriteAllText(genAiConfigPath, "{}");

        var plan = new StageRuntimePlan
        {
            ModelAlias = "qwen-instruct",
            ModelEntryPath = genAiConfigPath,
            ModelRootPath = fixture.RootPath,
        };

        string resolvedRoot = PlannedRuntimeModelResolver.ResolveModelRootPath(
            plan,
            new BenchmarkModelPathResolver(manifestRegistry: null, modelCacheDirectory: null));

        Assert.Equal(fixture.RootPath, resolvedRoot, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class TempDirectoryFixture : IDisposable
    {
        public string RootPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "trackdub-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectoryFixture() => Directory.CreateDirectory(RootPath);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
