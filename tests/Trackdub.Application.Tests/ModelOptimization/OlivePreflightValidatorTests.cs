using Trackdub.Application.ModelOptimization;
using Trackdub.Contracts.ModelOptimization;
using Trackdub.Domain;

namespace Trackdub.Application.Tests.ModelOptimization;

public sealed class OlivePreflightValidatorTests
{
    private readonly OlivePreflightValidator _validator = new();

    [Fact]
    public void ResolveAllowedPrecisions_returns_provider_defaults_when_manifest_has_no_precision_metadata()
    {
        var availability = new ModelOptimizationAvailability(
            HasProfile: true,
            CanOptimize: true,
            ComponentRelativePaths: ["model.onnx"],
            AvailableProviders: [ExecutionProviderKind.Cpu],
            UnavailableReason: null);

        IReadOnlyList<string> precisions = _validator.ResolveAllowedPrecisions(availability, OliveExecutionProvider.Cpu);

        Assert.Equal(["fp32", "int8"], precisions);
    }

    [Fact]
    public void Validate_uses_provider_defaults_when_precision_metadata_is_missing()
    {
        var availability = new ModelOptimizationAvailability(
            HasProfile: true,
            CanOptimize: true,
            ComponentRelativePaths: ["model.onnx"],
            AvailableProviders: [ExecutionProviderKind.Cpu],
            UnavailableReason: null);

        OlivePreflightResult result = _validator.Validate(availability, OliveExecutionProvider.Cpu, "fp32");

        Assert.True(result.IsAllowed, result.ErrorReason);
    }

    [Fact]
    public void ResolveAllowedPrecisions_filters_manifest_precisions_by_provider_defaults()
    {
        var availability = new ModelOptimizationAvailability(
            HasProfile: true,
            CanOptimize: true,
            ComponentRelativePaths: ["model.onnx"],
            AvailableProviders: [ExecutionProviderKind.Cpu],
            UnavailableReason: null,
            SupportedPrecisions: ["fp16", "int8", "int4"]);

        IReadOnlyList<string> cpuPrecisions = _validator.ResolveAllowedPrecisions(availability, OliveExecutionProvider.Cpu);

        Assert.Equal(["int8"], cpuPrecisions);
    }

    [Theory]
    [InlineData(OliveExecutionProvider.Migraphx, ExecutionProviderKind.Migraphx)]
    [InlineData(OliveExecutionProvider.Qnn, ExecutionProviderKind.Qnn)]
    [InlineData(OliveExecutionProvider.OpenVino, ExecutionProviderKind.OpenVinoCatalog)]
    public void Validate_accepts_newest_olive_provider_mappings(
        OliveExecutionProvider oliveProvider,
        ExecutionProviderKind runtimeProvider)
    {
        var availability = new ModelOptimizationAvailability(
            HasProfile: true,
            CanOptimize: true,
            ComponentRelativePaths: ["model.onnx"],
            AvailableProviders: [runtimeProvider],
            UnavailableReason: null,
            SupportedPrecisions: ["int8"]);

        OlivePreflightResult result = _validator.Validate(availability, oliveProvider, "int8");

        Assert.True(result.IsAllowed, result.ErrorReason);
    }
}
