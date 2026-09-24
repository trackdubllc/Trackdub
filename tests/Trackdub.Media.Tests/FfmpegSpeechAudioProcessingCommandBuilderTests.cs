using Trackdub.Media.Enhancement;

namespace Trackdub.Media.Tests;

public sealed class FfmpegSpeechAudioProcessingCommandBuilderTests
{
    [Fact]
    public void BuildArguments_uses_expected_speech_cleanup_filter_and_pcm_output()
    {
        IReadOnlyList<string> arguments = FfmpegSpeechAudioEnhancementCommandBuilder.BuildArguments(
            "input.wav",
            "output.wav");

        int filterFlagIndex = arguments
            .Select((value, index) => (value, index))
            .Where(static item => item.value == "-filter:a")
            .Select(static item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        Assert.True(filterFlagIndex >= 0);
        Assert.Equal(
            "highpass=f=80,lowpass=f=8000,afftdn=nr=8:nf=-55,speechnorm=e=6.25:l=1",
            arguments[filterFlagIndex + 1]);
        Assert.Contains("pcm_s16le", arguments);
        Assert.Contains("48000", arguments);
    }
}
