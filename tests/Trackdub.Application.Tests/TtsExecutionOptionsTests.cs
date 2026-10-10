using Trackdub.Application.Transcripts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// D2: VRAM-aware TTS concurrency caps. Options resolve the effective degree of parallelism
/// from the configured maximum, tightened (never raised) by a VRAM-derived bound for the
/// model class the run routes to.
/// </summary>
public sealed class TtsExecutionOptionsTests
{
    [Fact]
    public void Resolve_UnknownVram_KeepsConfiguredValue()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 6, MaxAcceleratorVramMb: 0);

        Assert.Equal(6, options.ResolveEffectiveConcurrency(StockTtsDefaults.KokoroPrimaryAlias));
    }

    [Fact]
    public void Resolve_NullConfigured_UsesLegacyDefault()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: null, MaxAcceleratorVramMb: 0);

        Assert.Equal(TtsExecutionOptions.LegacyMaxConcurrency, options.ResolveEffectiveConcurrency(null));
        Assert.Equal(TtsExecutionOptions.LegacyMaxConcurrency, options.ResolveEffectiveConcurrency(StockTtsDefaults.KokoroPrimaryAlias));
    }

    [Fact]
    public void Resolve_SmallModelOnLargeVram_KeepsConfiguredValue()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 4, MaxAcceleratorVramMb: 12227);

        Assert.Equal(4, options.ResolveEffectiveConcurrency(StockTtsDefaults.KokoroPrimaryAlias));
        Assert.Equal(4, options.ResolveEffectiveConcurrency(null));
    }

    [Fact]
    public void Resolve_LargeModel_TightensToOne()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 4, MaxAcceleratorVramMb: 12227);

        Assert.Equal(1, options.ResolveEffectiveConcurrency("qwen3-tts-1.7b-customvoice"));
        Assert.Equal(1, options.ResolveEffectiveConcurrency("qwen3-tts-1.7b-base"));
    }

    [Fact]
    public void Resolve_LargeModelOnSmallVram_FallsToSequential()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 8, MaxAcceleratorVramMb: 6144);

        Assert.Equal(TtsExecutionOptions.MinConcurrency, options.ResolveEffectiveConcurrency("qwen3-tts-1.7b-customvoice"));
    }

    [Fact]
    public void Resolve_MediumModel_TightensNeverRaises()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 8, MaxAcceleratorVramMb: 12227);

        int effective = options.ResolveEffectiveConcurrency("chatterbox-turbo-onnx");
        Assert.True(effective <= 8, $"Expected <= 8, got {effective}.");
        Assert.Equal(8, effective);
    }

    [Fact]
    public void Resolve_MediumModelOnSmallerVram_Tightens()
    {
        // Workers: 1 + (6144 - 4096) / 512 = 5.
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 8, MaxAcceleratorVramMb: 6144);

        Assert.Equal(5, options.ResolveEffectiveConcurrency("chatterbox-turbo-onnx"));
    }

    [Fact]
    public void Resolve_MediumModel_CountsFirstWorkerInBudget()
    {
        // The 4096 MB model budget covers worker one; 512 MB more admits a second worker.
        Assert.Equal(1, new TtsExecutionOptions(8, 4096).ResolveEffectiveConcurrency("cosyvoice-300m"));
        Assert.Equal(2, new TtsExecutionOptions(8, 4608).ResolveEffectiveConcurrency("cosyvoice-300m"));
    }

    [Fact]
    public void Resolve_PlannedDeviceVram_OverridesHostMax()
    {
        // 24 GB host max would allow 8 medium workers; the planned 4 GB adapter allows 1.
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 8, MaxAcceleratorVramMb: 24576);

        Assert.Equal(1, options.ResolveEffectiveConcurrency("cosyvoice-300m", acceleratorRouted: true, deviceVramMb: 4096));
        Assert.Equal(8, options.ResolveEffectiveConcurrency("cosyvoice-300m", acceleratorRouted: true, deviceVramMb: null));
    }

    [Fact]
    public void Resolve_PlannedDeviceVramWithUnknownHostMax_StillBounds()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 8, MaxAcceleratorVramMb: 0);

        Assert.Equal(1, options.ResolveEffectiveConcurrency("qwen3-tts-1.7b-base", acceleratorRouted: true, deviceVramMb: 12227));
    }

    [Fact]
    public void Resolve_CpuRouted_IgnoresAcceleratorVramCap()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 8, MaxAcceleratorVramMb: 6144);

        Assert.Equal(8, options.ResolveEffectiveConcurrency("chatterbox-turbo-onnx", acceleratorRouted: false));
    }

    [Fact]
    public void Resolve_ConfiguredAboveAbsoluteMax_Clamps()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 100, MaxAcceleratorVramMb: 0);

        Assert.Equal(TtsExecutionOptions.AbsoluteMaxConcurrency, options.ResolveEffectiveConcurrency(null));
    }

    [Fact]
    public void Resolve_ConfiguredZeroOrNegative_UsesLegacyDefault()
    {
        Assert.Equal(TtsExecutionOptions.LegacyMaxConcurrency,
            new TtsExecutionOptions(0, 12227).ResolveEffectiveConcurrency(null));
        Assert.Equal(TtsExecutionOptions.LegacyMaxConcurrency,
            new TtsExecutionOptions(-3, 0).ResolveEffectiveConcurrency(StockTtsDefaults.KokoroPrimaryAlias));
    }

    [Fact]
    public void Resolve_VramDerivedBoundNeverExceedsCap()
    {
        var options = new TtsExecutionOptions(ConfiguredMaxConcurrency: 8, MaxAcceleratorVramMb: 12227);

        // Medium model: (12227 - 4096) / 512 = 15 -> clamped to MaxVramDerivedConcurrency (8).
        Assert.Equal(TtsExecutionOptions.MaxVramDerivedConcurrency,
            options.ResolveEffectiveConcurrency("cosyvoice-300m"));
    }

    [Fact]
    public void ModelBudget_LargeClass_UsesLargestBudget()
    {
        Assert.Equal(TtsExecutionOptions.LargeModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("qwen3-tts-1.7b-base"));
    }

    [Fact]
    public void ModelBudget_MediumClass()
    {
        Assert.Equal(TtsExecutionOptions.MediumModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("qwen3-tts-0.6b-customvoice"));
        Assert.Equal(TtsExecutionOptions.MediumModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("chatterbox-multilingual"));
        Assert.Equal(TtsExecutionOptions.MediumModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("cosyvoice"));
        Assert.Equal(TtsExecutionOptions.MediumModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("chatterbox"));
        Assert.Equal(TtsExecutionOptions.MediumModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("chatterbox-turbo"));
        Assert.Equal(TtsExecutionOptions.MediumModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("chatterbox-multilingual-onnx"));
    }

    [Fact]
    public void ModelBudget_SmallOrUnknownClass_UsesSmallBudget()
    {
        Assert.Equal(TtsExecutionOptions.SmallModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("kokoro-onnx"));
        Assert.Equal(TtsExecutionOptions.SmallModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget(null));
        // Unknown alias: no known large/medium marker -> conservative small default (planner owns unknowns).
        Assert.Equal(TtsExecutionOptions.SmallModelVramMb, TtsExecutionOptionsTestAccess.ModelBudget("mystery-model"));
    }
}

/// <summary>Exposes internal budget resolution for tests without widening the public surface.</summary>
internal static class TtsExecutionOptionsTestAccess
{
    public static long ModelBudget(string? alias) =>
        TtsExecutionOptions.Default.ResolveModelVramBudgetMb(alias);
}
