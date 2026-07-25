namespace Trackdub.Contracts;

public interface ISpeechAudioEnhancementService
{
    Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken);
}

public sealed record SpeechAudioEnhancementRequest(
    string SourceAudioPath,
    string DestinationPath,
    SpeechAudioEnhancementOptions? Options = null);

public sealed record SpeechAudioEnhancementResult(
    string OutputPath,
    double DurationSeconds,
    int SampleRate,
    int ChannelCount,
    long SampleFrames,
    SpeechAudioEnhancementBackend Backend = SpeechAudioEnhancementBackend.Ffmpeg,
    string? BackendProfile = null);

/// <summary>
/// Options controlling which audio enhancement backend and profile to use.
/// </summary>
public sealed record SpeechAudioEnhancementOptions(
    bool EnableNvidiaAfx,
    NvidiaAfxProfile NvidiaAfxProfile,
    float NvidiaAfxIntensityRatio)
{
    /// <summary>
    /// Default options: NVIDIA AFX disabled, noise+reverb profile, full intensity.
    /// </summary>
    public static SpeechAudioEnhancementOptions Default { get; } =
        new(EnableNvidiaAfx: false, NvidiaAfxProfile: NvidiaAfxProfile.NoiseAndReverb, NvidiaAfxIntensityRatio: 1.0f);
}

/// <summary>
/// Identifies which backend processed the audio enhancement.
/// </summary>
public enum SpeechAudioEnhancementBackend
{
    /// <summary>FFmpeg-based loudness normalisation (always available).</summary>
    Ffmpeg = 0,

    /// <summary>NVIDIA RTXVoice / AFX SDK.</summary>
    NvidiaAfx = 1,

    /// <summary>DeepFilterNet ONNX model.</summary>
    DeepFilterNet = 2,
}
