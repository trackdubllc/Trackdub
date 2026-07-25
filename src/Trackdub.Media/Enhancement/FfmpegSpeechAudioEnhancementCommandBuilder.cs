namespace Trackdub.Media.Enhancement;

internal static class FfmpegSpeechAudioEnhancementCommandBuilder
{
    public const string SpeechEnhancementFilter = "highpass=f=80,lowpass=f=8000,afftdn=nr=8:nf=-55,speechnorm=e=6.25:l=1";

    public static IReadOnlyList<string> BuildArguments(
        string inputPath,
        string outputPath,
        string audioFilter = SpeechEnhancementFilter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilter);

        return
        [
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            inputPath,
            "-vn",
            "-sn",
            "-dn",
            "-map",
            "0:a:0",
            "-filter:a",
            audioFilter,
            "-ac",
            "1",
            "-ar",
            "48000",
            "-c:a",
            "pcm_s16le",
            outputPath
        ];
    }
}
