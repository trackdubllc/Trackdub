namespace Trackdub.Sdk.Tests;

/// <summary>
/// Custom [Fact] attribute that skips the test at discovery time when the smoke media
/// fixture is not present. When smoke.mp4 IS available, the test runs normally.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SmokeTestFactAttribute : FactAttribute
{
    private const string SmokeMediaFileName = "smoke.mp4";
    private const string FixturesDirectory = "Fixtures";

    public SmokeTestFactAttribute()
    {
        string smokeMediaPath = HeadlessPipelineSmokeMedia.GetSourceMediaPath();

        if (!File.Exists(smokeMediaPath))
        {
            Skip = $"Smoke media not found. Place '{SmokeMediaFileName}' at: " +
                   Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, FixturesDirectory));
        }
    }
}

internal static class HeadlessPipelineSmokeMedia
{
    internal const string SourceMediaPathEnvironmentVariable = "TRACKDUB_SMOKE_MEDIA_PATH";

    internal static string GetSourceMediaPath()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(SourceMediaPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.Join(AppContext.BaseDirectory, "Fixtures", "smoke.mp4");
    }
}
