namespace Trackdub.Sdk.Tests;

public sealed class HeadlessPipelineSmokeMediaTests
{
    [Fact]
    public void GetSourceMediaPath_ConfiguredPath_ReturnsFullPath()
    {
        const string configuredPath = "fixtures/custom-smoke.mp4";
        string? originalValue = Environment.GetEnvironmentVariable(
            HeadlessPipelineSmokeMedia.SourceMediaPathEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                HeadlessPipelineSmokeMedia.SourceMediaPathEnvironmentVariable,
                configuredPath);

            string result = HeadlessPipelineSmokeMedia.GetSourceMediaPath();

            Assert.Equal(Path.GetFullPath(configuredPath), result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                HeadlessPipelineSmokeMedia.SourceMediaPathEnvironmentVariable,
                originalValue);
        }
    }

    [Fact]
    public void GetSourceMediaPath_NoConfiguredPath_ReturnsFixturePath()
    {
        string? originalValue = Environment.GetEnvironmentVariable(
            HeadlessPipelineSmokeMedia.SourceMediaPathEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                HeadlessPipelineSmokeMedia.SourceMediaPathEnvironmentVariable,
                null);

            string result = HeadlessPipelineSmokeMedia.GetSourceMediaPath();

            Assert.Equal(Path.Join(AppContext.BaseDirectory, "Fixtures", "smoke.mp4"), result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                HeadlessPipelineSmokeMedia.SourceMediaPathEnvironmentVariable,
                originalValue);
        }
    }
}
