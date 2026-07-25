using Trackdub.Domain;
using Trackdub.Inference.Onnx.DeepFilterNet;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests.DeepFilterNet;

public sealed class DeepFilterNetModelPathsTests
{
    [Fact]
    public async Task TryResolveAsync_WhenManifestRootMissing_UsesModelCacheRoot()
    {
        using TempDirectoryFixture manifestRoot = new("manifest-root");
        using TempDirectoryFixture cacheRoot = new("cache-root");
        WriteOnnxFiles(cacheRoot.RootPath);

        BundledModelManifestRegistry registry = CreateRegistry(manifestRoot.RootPath);
        var inventory = new InMemoryModelCacheInventory(
        [
            new LocalModelCacheRecord(
                "Rikorose/DeepFilterNet3",
                cacheRoot.RootPath,
                "dcbbe520263d1061693c4c4a56a6d6a917f30b25",
                "7c5399d3da8a50ebef1c1a0ae421b33376aa5e45d0e92df16da7e83c9c131916",
                DateTimeOffset.UtcNow)
        ]);

        DeepFilterNetModelPaths? paths = await DeepFilterNetModelPaths.TryResolveAsync(
            registry,
            inventory,
            CancellationToken.None);

        Assert.NotNull(paths);
        Assert.Equal(cacheRoot.RootPath, paths.RootDirectory, StringComparer.OrdinalIgnoreCase);
        Assert.True(paths.AllFilesExist());
    }

    [Fact]
    public void TryResolve_WhenManifestRootHasOnnxFiles_PrefersManifestRoot()
    {
        using TempDirectoryFixture manifestRoot = new("manifest-root");
        using TempDirectoryFixture cacheRoot = new("cache-root");
        WriteOnnxFiles(manifestRoot.RootPath);
        WriteOnnxFiles(cacheRoot.RootPath);

        BundledModelManifestRegistry registry = CreateRegistry(manifestRoot.RootPath);

        DeepFilterNetModelPaths? paths = DeepFilterNetModelPaths.TryResolve(registry);

        Assert.NotNull(paths);
        Assert.Equal(manifestRoot.RootPath, paths.RootDirectory, StringComparer.OrdinalIgnoreCase);
    }

    private static BundledModelManifestRegistry CreateRegistry(string rootPath)
    {
        string manifestDirectory = Path.Combine(rootPath, "manifest");
        Directory.CreateDirectory(manifestDirectory);
        string manifestPath = Path.Combine(manifestDirectory, "bundled-models.manifest.json");
        string relativeRoot = Path.GetRelativePath(manifestDirectory, rootPath).Replace('\\', '/');
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "models": [
                {
                  "model_id": "Rikorose/DeepFilterNet3",
                  "task": "speech-enhancement",
                  "engine_family": "deepfilternet3",
                  "capabilities": [ "speech-enhancement" ],
                  "tier": "balanced",
                  "license": "MIT",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": true,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": true,
                  "source_url": "https://huggingface.co/tonythethompson/deepfilternet3-onnx",
                  "revision": "dcbbe520263d1061693c4c4a56a6d6a917f30b25",
                  "sha256": "7c5399d3da8a50ebef1c1a0ae421b33376aa5e45d0e92df16da7e83c9c131916",
                  "aliases": [ "deepfilternet3" ],
                  "root_path": "{{relativeRoot}}",
                  "benchmark_entry": "enc.onnx",
                  "download_files": [ "erb_dec.onnx", "df_dec.onnx" ],
                  "variants": [
                    { "alias": "default", "entry_path": "enc.onnx" }
                  ],
                  "download_file_hashes": {
                    "enc.onnx": "7c5399d3da8a50ebef1c1a0ae421b33376aa5e45d0e92df16da7e83c9c131916",
                    "erb_dec.onnx": "ab669a1d10afe20911728b33053a452071042317a90581092b325da7b2f9d895",
                    "df_dec.onnx": "23114ce3b0f6464b763ee62f7bb8aab6b2a129a21eabd5bcfe59413db05f278a"
                  },
                  "hash_verification": { "mode": "required" }
                }
              ]
            }
            """);

        return BundledModelManifestRegistry.Load(manifestPath);
    }

    private static void WriteOnnxFiles(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "enc.onnx"), "enc");
        File.WriteAllText(Path.Combine(rootPath, "erb_dec.onnx"), "erb");
        File.WriteAllText(Path.Combine(rootPath, "df_dec.onnx"), "df");
    }

    private sealed class TempDirectoryFixture : IDisposable
    {
        public string RootPath { get; }

        public TempDirectoryFixture(string label)
        {
            RootPath = Path.Combine(Path.GetTempPath(), "trackdub-tests", label, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

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

    private sealed class InMemoryModelCacheInventory(IReadOnlyList<LocalModelCacheRecord> records)
        : IModelCacheInventory
    {
        public Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(records);
    }
}
