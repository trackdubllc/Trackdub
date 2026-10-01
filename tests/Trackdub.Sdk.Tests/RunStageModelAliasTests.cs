using Trackdub.Cli;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// run-stage --model accepts a bare alias or stage:alias. A foreign stage prefix
/// must be rejected so it cannot silently miss every model and fall back to the default route.
/// </summary>
public sealed class RunStageModelAliasTests
{
    [Theory]
    [InlineData("translation", "madlad400", "madlad400")]
    [InlineData("translation", "  madlad400  ", "madlad400")]
    [InlineData("translation", "translation:madlad400", "madlad400")]
    [InlineData("translation", "TRANSLATION:madlad400", "madlad400")]
    [InlineData("translation", " translation : madlad400 ", "madlad400")]
    [InlineData("translation", "translation:foo:bar", "foo:bar")]
    public void AcceptsBareAliasAndMatchingStagePrefix(string stageName, string modelAlias, string expectedAlias)
    {
        Dictionary<string, string>? preferences =
            CliModelOverrides.TryBuildRunStageModelPreferences(stageName, modelAlias);

        Assert.NotNull(preferences);
        KeyValuePair<string, string> only = Assert.Single(preferences);
        Assert.Equal(stageName, only.Key);
        Assert.Equal(expectedAlias, only.Value);
    }

    [Theory]
    [InlineData("translation", "asr:whisper-large-v3")]
    [InlineData("translation", ":madlad400")]
    [InlineData("tts", "translation:madlad400")]
    public void RejectsMismatchedStagePrefix(string stageName, string modelAlias)
    {
        (Dictionary<string, string>? preferences, string stderr) = Capture(stageName, modelAlias);

        Assert.Null(preferences);
        Assert.Contains("invalidArgument", stderr, StringComparison.Ordinal);
        Assert.Contains("Invalid --model", stderr, StringComparison.Ordinal);
        Assert.Contains(modelAlias, stderr, StringComparison.Ordinal);
        Assert.Contains(stageName, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("non-empty", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("translation", "")]
    [InlineData("translation", "   ")]
    [InlineData("translation", "translation:")]
    [InlineData("translation", "translation:   ")]
    public void RejectsEmptyAlias(string stageName, string modelAlias)
    {
        (Dictionary<string, string>? preferences, string stderr) = Capture(stageName, modelAlias);

        Assert.Null(preferences);
        Assert.Contains("Model alias must be non-empty", stderr, StringComparison.Ordinal);
        Assert.Contains("\"parameter\":\"--model\"", stderr, StringComparison.Ordinal);
    }

    private static (Dictionary<string, string>? Preferences, string Stderr) Capture(string stageName, string modelAlias)
    {
        TextWriter originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            Dictionary<string, string>? preferences =
                CliModelOverrides.TryBuildRunStageModelPreferences(stageName, modelAlias);
            return (preferences, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
