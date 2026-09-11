using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Tests;

public sealed class BundledModelManifestRegistryTests
{
    [Fact]
    public void AppendSourceTreeToolHint_appends_guidance_for_source_tree_manifest()
    {
        string message = BundledModelManifestRegistry.AppendSourceTreeToolHint(
            Path.Join("repo", "src", "Trackdub.Inference", "Runtime", "ModelManifest", "bundled-models.manifest.json"),
            "did not contain any model entries.");

        Assert.Contains("did not contain any model entries.", message, StringComparison.Ordinal);
        Assert.Contains("repo source-tree manifest", message, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project src/Trackdub.Cli", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendSourceTreeToolHint_leaves_unrelated_paths_unchanged()
    {
        const string original = "Bundled model manifest was not found.";
        string message = BundledModelManifestRegistry.AppendSourceTreeToolHint(
            Path.Join("cache", "models", "bundled-models.manifest.json"),
            original);

        Assert.Equal(original, message);
    }
}
