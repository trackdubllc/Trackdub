using Trackdub.Domain;

namespace Trackdub.Domain.Tests;

public sealed class ModelRuntimeTests
{
    [Theory]
    [InlineData(ModelCacheState.Missing, 0)]
    [InlineData(ModelCacheState.Downloading, 1)]
    [InlineData(ModelCacheState.Installed, 2)]
    [InlineData(ModelCacheState.Corrupt, 3)]
    [InlineData(ModelCacheState.Blocked, 4)]
    [InlineData(ModelCacheState.Ready, 5)]
    public void ModelCacheState_HasExpectedValues(ModelCacheState state, int expected)
    {
        Assert.Equal(expected, (int)state);
    }

    [Fact]
    public void ModelInventoryEntry_ConstructsWithAllFields()
    {
        var entry = new ModelInventoryEntry(
            ModelId: "silero-vad/silero_vad",
            DisplayName: "Silero VAD",
            Task: "vad",
            EngineFamily: "silero-vad",
            License: "MIT",
            CommercialAllowed: true,
            CommercialUseVerified: true,
            State: ModelCacheState.Ready,
            FileSizeBytes: 2_000_000L,
            CachedAtUtc: DateTimeOffset.Parse("2026-05-01T12:00:00+00:00"),
            FailureReason: null);

        Assert.Equal("silero-vad/silero_vad", entry.ModelId);
        Assert.Equal("Silero VAD", entry.DisplayName);
        Assert.Equal(ModelCacheState.Ready, entry.State);
        Assert.Equal(2_000_000L, entry.FileSizeBytes);
        Assert.Null(entry.FailureReason);
    }

    [Fact]
    public void ModelInventoryEntry_CorruptState_IncludesFailureReason()
    {
        var entry = new ModelInventoryEntry(
            ModelId: "whisper-medium",
            DisplayName: "Whisper Medium",
            Task: "asr",
            EngineFamily: "whisper",
            License: "MIT",
            CommercialAllowed: true,
            CommercialUseVerified: true,
            State: ModelCacheState.Corrupt,
            FileSizeBytes: null,
            CachedAtUtc: DateTimeOffset.Parse("2026-05-01T12:00:00+00:00"),
            FailureReason: "Hash mismatch: expected abc123, got def456");

        Assert.Equal(ModelCacheState.Corrupt, entry.State);
        Assert.NotNull(entry.FailureReason);
        Assert.Contains("Hash mismatch", entry.FailureReason);
    }

    [Fact]
    public void ModelInventoryEntry_MissingState_HasNoSizeOrCacheTime()
    {
        var entry = new ModelInventoryEntry(
            ModelId: "opus-mt-en-de",
            DisplayName: "OPUS MT English-German",
            Task: "translation",
            EngineFamily: "opus-mt",
            License: "CC-BY-4.0",
            CommercialAllowed: true,
            CommercialUseVerified: true,
            State: ModelCacheState.Missing,
            FileSizeBytes: null,
            CachedAtUtc: null,
            FailureReason: null);

        Assert.Equal(ModelCacheState.Missing, entry.State);
        Assert.Null(entry.FileSizeBytes);
        Assert.Null(entry.CachedAtUtc);
    }

    [Fact]
    public void ModelInventoryEntry_RecordEquality()
    {
        var a = new ModelInventoryEntry("m1", "Model 1", "vad", "silero", "MIT", true, true,
            ModelCacheState.Installed, 100L, DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), null);
        var b = new ModelInventoryEntry("m1", "Model 1", "vad", "silero", "MIT", true, true,
            ModelCacheState.Installed, 100L, DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), null);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ModelInventoryEntry_WithExpression_ChangesState()
    {
        var original = new ModelInventoryEntry("m1", "Model 1", "vad", "silero", "MIT", true, true,
            ModelCacheState.Missing, null, null, null);

        var downloading = original with { State = ModelCacheState.Downloading };

        Assert.Equal(ModelCacheState.Missing, original.State);
        Assert.Equal(ModelCacheState.Downloading, downloading.State);
    }

    [Fact]
    public void ModelOptimizationRecipeBinding_preserves_operation_metadata()
    {
        var binding = new ModelOptimizationRecipeBinding(
            ConfigRelativePath: "recipes/qnn.json",
            Provider: "qnn",
            Precision: "int8",
            Operations: [ModelOptimizationOperation.QnnConversion, ModelOptimizationOperation.Compression],
            ExpectedOutput: ModelOptimizationExpectedOutput.QnnModelLibrary,
            FallbackPolicy: ModelOptimizationFallbackPolicy.None,
            QuantizationMethod: "qnn-int8",
            RequiresCalibrationData: true,
            ScriptRelativePath: "recipes/scripts/eval.py",
            ScriptSha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Evaluator: "translation-smoke",
            SplitCount: null,
            CostModelRelativePath: null,
            AdapterRelativePath: null,
            AdapterMode: null,
            OutputManifestRelativePath: "recipes/qnn.outputs.json");

        Assert.Contains(ModelOptimizationOperation.QnnConversion, binding.Operations);
        Assert.Equal(ModelOptimizationExpectedOutput.QnnModelLibrary, binding.ExpectedOutput);
        Assert.Equal(ModelOptimizationFallbackPolicy.None, binding.FallbackPolicy);
        Assert.True(binding.RequiresCalibrationData);
    }
}
