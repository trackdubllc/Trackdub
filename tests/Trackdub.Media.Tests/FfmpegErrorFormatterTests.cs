using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class FfmpegErrorFormatterTests
{
    [Fact]
    public void BuildFailureMessage_truncates_large_standard_error()
    {
        string standardError = $"prefix-{new string('x', FfmpegErrorFormatter.MaxStandardErrorChars + 200)}";

        string message = FfmpegErrorFormatter.BuildFailureMessage("ffmpeg test", 9, standardError);

        Assert.Contains("ffmpeg test failed with exit code 9", message);
        Assert.Contains("stderr truncated", message);
        Assert.DoesNotContain("prefix-", message);
        Assert.True(message.Length < FfmpegErrorFormatter.MaxStandardErrorChars + 200);
    }
}
