using Trackdub.Contracts.ModelOptimization;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class LocalModelVariantRegistrarTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(),
        "Trackdub.LocalModelVariantRegistrar.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RegisterAsync_adds_variant_to_existing_model_record()
    {
        (LocalModelCacheRecordStore store, string modelRoot) = await CreateInstalledModelAsync("nested/model.onnx");
        string variantRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        WriteFile(Path.Combine(variantRoot, "nested", "model.onnx"), "optimized");
        var registrar = new LocalModelVariantRegistrar(store);

        await registrar.RegisterAsync(
            CreateRegistration(modelRoot, variantRoot),
            TestContext.Current.CancellationToken);

        LocalModelVariantRecord variant = Assert.Single(Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken)).Variants);
        Assert.Equal("olive-cpu-fp32", variant.Alias);
        Assert.Equal(variantRoot, variant.RootPath);
        Assert.Equal("nested/model.onnx", variant.EntryRelativePath);
        Assert.Equal(["nested/model.onnx"], variant.ComponentRelativePaths);
        Assert.Equal("olive", variant.OptimizerId);
        Assert.Equal(ExecutionProviderKind.Cpu, variant.ExecutionProvider);
        Assert.Equal("fp32", variant.Precision);
        Assert.Equal("main", variant.SourceModelRevision);
        Assert.Equal("abc123", variant.SourceModelSha256);
    }

    [Fact]
    public async Task RegisterAsync_replaces_existing_variant_with_same_alias()
    {
        (LocalModelCacheRecordStore store, string modelRoot) = await CreateInstalledModelAsync("nested/model.onnx");
        string firstRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        string secondRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32-rerun");
        WriteFile(Path.Combine(firstRoot, "nested", "model.onnx"), "first");
        WriteFile(Path.Combine(secondRoot, "nested", "model.onnx"), "second");
        var registrar = new LocalModelVariantRegistrar(store);
        await registrar.RegisterAsync(CreateRegistration(modelRoot, firstRoot), TestContext.Current.CancellationToken);
        DateTimeOffset secondCreatedAt = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

        await registrar.RegisterAsync(
            CreateRegistration(modelRoot, secondRoot) with { CreatedAtUtc = secondCreatedAt },
            TestContext.Current.CancellationToken);

        LocalModelVariantRecord variant = Assert.Single(Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken)).Variants);
        Assert.Equal(secondRoot, variant.RootPath);
        Assert.Equal(secondCreatedAt, variant.CreatedAtUtc);
    }

    [Fact]
    public async Task RegisterAsync_accepts_genai_config_entry_and_component_paths()
    {
        (LocalModelCacheRecordStore store, string modelRoot) = await CreateInstalledModelAsync("genai_config.json");
        string variantRoot = Path.Combine(modelRoot, "optimized", "olive-directml-fp16");
        WriteFile(Path.Combine(variantRoot, "directml-fp16", "genai_config.json"), "{}");
        WriteFile(Path.Combine(variantRoot, "directml-fp16", "encoder.onnx"), "encoder");
        var registrar = new LocalModelVariantRegistrar(store);

        await registrar.RegisterAsync(
            CreateRegistration(modelRoot, variantRoot) with
            {
                VariantAlias = "olive-directml-fp16",
                EntryRelativePath = "directml-fp16/genai_config.json",
                ComponentRelativePaths =
                [
                    "directml-fp16/genai_config.json",
                    "directml-fp16/encoder.onnx"
                ],
                ExecutionProvider = ExecutionProviderKind.DirectMl,
                Precision = "fp16"
            },
            TestContext.Current.CancellationToken);

        LocalModelVariantRecord variant = Assert.Single(Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken)).Variants);
        Assert.Equal("directml-fp16/genai_config.json", variant.EntryRelativePath);
        Assert.Equal(
            ["directml-fp16/genai_config.json", "directml-fp16/encoder.onnx"],
            variant.ComponentRelativePaths);
    }

    [Fact]
    public async Task RegisterAsync_rejects_missing_base_cache_record()
    {
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        var store = new LocalModelCacheRecordStore(storagePaths);
        string modelRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example-model");
        string variantRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        WriteFile(Path.Combine(variantRoot, "nested", "model.onnx"), "optimized");
        var registrar = new LocalModelVariantRegistrar(store);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registrar.RegisterAsync(
                CreateRegistration(modelRoot, variantRoot),
                TestContext.Current.CancellationToken));

        Assert.Contains("base model cache record", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterAsync_rejects_variant_root_outside_base_model_root()
    {
        (LocalModelCacheRecordStore store, string modelRoot) = await CreateInstalledModelAsync("nested/model.onnx");
        string variantRoot = Path.Combine(tempRoot, "outside-variant");
        WriteFile(Path.Combine(variantRoot, "nested", "model.onnx"), "optimized");
        var registrar = new LocalModelVariantRegistrar(store);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registrar.RegisterAsync(
                CreateRegistration(modelRoot, variantRoot),
                TestContext.Current.CancellationToken));

        Assert.Contains("variant path is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../model.onnx", "nested/model.onnx")]
    [InlineData("nested/model.onnx", "../model.onnx")]
    public async Task RegisterAsync_rejects_unsafe_entry_or_component_paths(
        string entryRelativePath,
        string componentRelativePath)
    {
        (LocalModelCacheRecordStore store, string modelRoot) = await CreateInstalledModelAsync("nested/model.onnx");
        string variantRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        WriteFile(Path.Combine(variantRoot, "nested", "model.onnx"), "optimized");
        var registrar = new LocalModelVariantRegistrar(store);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registrar.RegisterAsync(
                CreateRegistration(modelRoot, variantRoot) with
                {
                    EntryRelativePath = entryRelativePath,
                    ComponentRelativePaths = [componentRelativePath]
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterAsync_rejects_missing_optimized_component_file()
    {
        (LocalModelCacheRecordStore store, string modelRoot) = await CreateInstalledModelAsync("nested/model.onnx");
        string variantRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        Directory.CreateDirectory(Path.Combine(variantRoot, "nested"));
        var registrar = new LocalModelVariantRegistrar(store);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registrar.RegisterAsync(
                CreateRegistration(modelRoot, variantRoot),
                TestContext.Current.CancellationToken));

        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private async Task<(LocalModelCacheRecordStore Store, string ModelRoot)> CreateInstalledModelAsync(
        params string[] relativeFiles)
    {
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        string modelRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example-model");
        foreach (string relativeFile in relativeFiles)
        {
            WriteFile(Path.Combine(modelRoot, relativeFile.Replace('/', Path.DirectorySeparatorChar)), "source");
        }

        var store = new LocalModelCacheRecordStore(storagePaths);
        await store.SaveAsync(
            [
                new LocalModelCacheRecord(
                    "example/model",
                    modelRoot,
                    "main",
                    "abc123",
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
            ],
            TestContext.Current.CancellationToken);
        return (store, modelRoot);
    }

    private static ModelOptimizedVariantRegistration CreateRegistration(
        string modelRoot,
        string variantRoot) =>
        new(
            ModelId: "example/model",
            BaseModelRootPath: modelRoot,
            VariantAlias: "olive-cpu-fp32",
            VariantRootPath: variantRoot,
            EntryRelativePath: "nested/model.onnx",
            ComponentRelativePaths: ["nested/model.onnx"],
            OptimizerId: "olive",
            ExecutionProvider: ExecutionProviderKind.Cpu,
            Precision: "fp32",
            CreatedAtUtc: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
