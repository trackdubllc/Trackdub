using Trackdub.Cli;

namespace Trackdub.Sdk.Tests;

public sealed class UserPathTextTests
{
    [Theory]
    [InlineData(@"C:\media\clip.mp4", @"C:\media\clip.mp4")]
    [InlineData("  C:\\media\\clip.mp4  ", @"C:\media\clip.mp4")]
    [InlineData("\"C:\\media\\clip.mp4\"", @"C:\media\clip.mp4")]
    [InlineData(" \"C:\\media\\clip.mp4\" ", @"C:\media\clip.mp4")]
    [InlineData("'C:\\media\\clip.mp4'", @"C:\media\clip.mp4")]
    [InlineData("& \"C:\\media\\clip.mp4\"", @"C:\media\clip.mp4")]
    [InlineData("& 'C:\\media\\clip.mp4'", @"C:\media\clip.mp4")]
    [InlineData("\"'C:\\media\\clip.mp4'\"", @"C:\media\clip.mp4")]
    [InlineData("\"\"", "")]
    [InlineData("''", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize_StripsExplorerAndPowerShellWrappers(string? input, string expected)
    {
        Assert.Equal(expected, UserPathText.Normalize(input));
    }

    [Fact]
    public void Normalize_StripsSmartQuotes()
    {
        string input = "\u201CC:\\media\\clip.mp4\u201D";

        Assert.Equal(@"C:\media\clip.mp4", UserPathText.Normalize(input));
    }

    [Fact]
    public void Normalize_LeavesUnmatchedOpeningQuote()
    {
        Assert.Equal("\"C:\\media\\clip.mp4", UserPathText.Normalize("\"C:\\media\\clip.mp4"));
    }

    [Fact]
    public void NormalizeOptional_ReturnsNullForQuotesOnly()
    {
        Assert.Null(UserPathText.NormalizeOptional("\"\""));
        Assert.Equal(@"C:\out", UserPathText.NormalizeOptional("\"C:\\out\""));
    }
}
