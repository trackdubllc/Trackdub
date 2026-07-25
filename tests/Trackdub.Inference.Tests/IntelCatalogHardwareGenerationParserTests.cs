using Trackdub.Inference.Onnx.WinMlCatalog;

namespace Trackdub.Inference.Tests;

public sealed class IntelCatalogHardwareGenerationParserTests
{
    [Theory]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1185G7 @ 3.00GHz", 11)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", 12)]
    [InlineData("Intel(R) Core(TM) i7-8650U @ 3.00GHz", 8)]
    [InlineData("Intel(R) Core(TM) i9-14900K", 14)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", 14)]
    [InlineData("Intel(R) Core(TM) Ultra 5 225H", 15)]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", 15)]
    public void TryParseProcessorGeneration_parses_common_marketing_strings(string processorName, int expectedGeneration)
    {
        bool parsed = IntelCatalogHardwareGenerationParser.TryParseProcessorGeneration(processorName, out int generation);

        Assert.True(parsed);
        Assert.Equal(expectedGeneration, generation);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM)2 Duo CPU E8400 @ 3.00GHz")]
    [InlineData("AMD Ryzen 9 7950X")]
    public void TryParseProcessorGeneration_returns_false_for_unsupported_names(string processorName)
    {
        bool parsed = IntelCatalogHardwareGenerationParser.TryParseProcessorGeneration(processorName, out int generation);

        Assert.False(parsed);
        Assert.Equal(0, generation);
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", false)]
    public void IsIntelArcGraphics_detects_arc_products(string description, bool expected)
    {
        Assert.Equal(expected, IntelCatalogHardwareGenerationParser.IsIntelArcGraphics(description));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) Ultra 5 225H", true)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", false)]
    public void IsCoreUltraSeries2_detects_arrow_lake_class_ultra_models(string processorName, bool expected)
    {
        Assert.Equal(expected, IntelCatalogHardwareGenerationParser.IsCoreUltraSeries2(processorName));
    }

    [Fact]
    public void Catalog_minimum_generations_match_windows_ml_openvino_requirements()
    {
        Assert.Equal(11, IntelCatalogHardwareGenerationParser.MinCpuGeneration);
        Assert.Equal(12, IntelCatalogHardwareGenerationParser.MinGpuGeneration);
        Assert.Equal(15, IntelCatalogHardwareGenerationParser.MinNpuGeneration);
    }
}
