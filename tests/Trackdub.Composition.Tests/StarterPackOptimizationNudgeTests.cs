using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackOptimizationNudgeTests
{
    [Fact]
    public async Task GetNudgesAsync_returns_nudge_when_olive_model_installed_without_optimized_variant()
    {
        var inventory = new FakeModelInventoryService();
        inventory.SetEntries(
        [
            new ModelInventoryEntry(
                "onnx-community/silero-vad",
                "Silero VAD",
                "vad",
                "onnx",
                "MIT",
                true,
                true,
                ModelCacheState.Ready,
                null,
                DateTimeOffset.UtcNow,
                null,
                IsOliveOptimizable: true),
        ]);

        var service = new StarterPackOptimizationNudgeService(new StarterPackCatalog(), inventory);
        IReadOnlyList<StarterPackOptimizationNudge> nudges = await service.GetNudgesAsync("basic", "default");

        Assert.Contains(nudges, nudge =>
            string.Equals(nudge.ModelId, "onnx-community/silero-vad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetNudgesAsync_skips_when_optimized_variant_installed()
    {
        var inventory = new FakeModelInventoryService();
        inventory.SetEntries(
        [
            new ModelInventoryEntry(
                "onnx-community/silero-vad",
                "Silero VAD",
                "vad",
                "onnx",
                "MIT",
                true,
                true,
                ModelCacheState.Ready,
                null,
                DateTimeOffset.UtcNow,
                null,
                IsOliveOptimizable: true,
                OptimizedVariants:
                [
                    new ModelOptimizedVariantInfo(
                        "gpu-int4",
                        "olive",
                        ExecutionProviderKind.DirectMl,
                        "int4",
                        ModelCacheState.Ready,
                        DateTimeOffset.UtcNow,
                        "C:\\cache",
                        "model.onnx",
                        []),
                ]),
        ]);

        var service = new StarterPackOptimizationNudgeService(new StarterPackCatalog(), inventory);
        IReadOnlyList<StarterPackOptimizationNudge> nudges = await service.GetNudgesAsync("basic", "default");

        Assert.DoesNotContain(nudges, nudge =>
            string.Equals(nudge.ModelId, "onnx-community/silero-vad", StringComparison.Ordinal));
    }
}
