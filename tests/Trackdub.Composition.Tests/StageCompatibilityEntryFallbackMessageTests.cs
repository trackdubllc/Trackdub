using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Composition.Tests;

public sealed class StageCompatibilityEntryFallbackMessageTests
{
    [Fact]
    public void DescribeFallback_when_resolved_cpu_claims_gpu_unavailable()
    {
        StageCompatibilityEntry stage = Create(
            requestedEp: "directml",
            resolvedEp: "cpu",
            resolvedVariant: "fp32");

        string message = stage.DescribeFallback();

        Assert.Equal("GPU path unavailable for silero-vad. Using fp32 on cpu.", message);
    }

    [Fact]
    public void DescribeFallback_when_resolved_cuda_does_not_claim_gpu_unavailable()
    {
        StageCompatibilityEntry stage = Create(
            requestedEp: "directml",
            resolvedEp: "cuda",
            resolvedVariant: "fp16");

        string message = stage.DescribeFallback();

        Assert.Equal("Preferred accelerator unavailable for silero-vad. Using fp16 on cuda.", message);
        Assert.DoesNotContain("GPU path unavailable", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFallback_when_only_variant_changes()
    {
        StageCompatibilityEntry stage = Create(
            requestedEp: "cuda",
            resolvedEp: "cuda",
            requestedVariant: "fp32",
            resolvedVariant: "fp16");

        string message = stage.DescribeFallback();

        Assert.Equal("Preferred variant unavailable for silero-vad. Using fp16 on cuda.", message);
    }

    [Fact]
    public void DescribeFallback_when_partial_offload()
    {
        StageCompatibilityEntry stage = Create(
            requestedEp: "cuda",
            resolvedEp: "cuda",
            resolvedVariant: "fp16",
            reason: "partial_offload_required");

        string message = stage.DescribeFallback();

        Assert.Equal("Partial GPU offload required for silero-vad. Using fp16 on cuda.", message);
    }

    [Fact]
    public void DescribeFallback_when_native_cuda_disabled_on_windows()
    {
        StageCompatibilityEntry stage = Create(
            requestedEp: "cuda",
            resolvedEp: "directml",
            resolvedVariant: "fp16",
            reason: "native_cuda_disabled_on_windows");

        string message = stage.DescribeFallback();

        Assert.Equal(
            "Native CUDA is not used for starter packs on Windows. Using fp16 on directml.",
            message);
        Assert.DoesNotContain("GPU path unavailable", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFallback_when_no_fallback_is_empty()
    {
        StageCompatibilityEntry stage = Create(
            requestedEp: "cuda",
            resolvedEp: "cuda",
            fallbackApplied: false);

        Assert.Equal(string.Empty, stage.DescribeFallback());
    }

    private static StageCompatibilityEntry Create(
        string requestedEp,
        string resolvedEp,
        string requestedVariant = "default",
        string resolvedVariant = "default",
        string? reason = "ep_unavailable",
        bool fallbackApplied = true) =>
        new(
            StageNames.Vad,
            "silero-vad",
            requestedVariant,
            requestedEp,
            resolvedVariant,
            resolvedEp,
            fallbackApplied,
            reason,
            Runnable: true);
}
