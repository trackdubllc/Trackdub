using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Composition.StarterPacks;

public static class StarterPackStageMapping
{
    public static string ToStageName(RuntimeStage stage) =>
        stage switch
        {
            RuntimeStage.Vad => StageNames.Vad,
            RuntimeStage.Asr => StageNames.Asr,
            RuntimeStage.Translation => StageNames.Translation,
            RuntimeStage.Tts => StageNames.Tts,
            RuntimeStage.Diarization => StageNames.Diarization,
            RuntimeStage.Separation => StageNames.Separation,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported starter-pack stage.")
        };

    public static string ToStageName(string packStageToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packStageToken);
        return packStageToken.Trim().ToLowerInvariant() switch
        {
            "vad" => StageNames.Vad,
            "asr" => StageNames.Asr,
            "translation" => StageNames.Translation,
            "tts" => StageNames.Tts,
            "diarization" => StageNames.Diarization,
            "separation" => StageNames.Separation,
            _ => throw new InvalidOperationException($"Unknown starter-pack stage token '{packStageToken}'.")
        };
    }

    public static string ToHardwareProfileKey(StarterPackHardwareProfile profile) =>
        profile switch
        {
            StarterPackHardwareProfile.CpuSafe => "cpu_safe",
            StarterPackHardwareProfile.BalancedGpu => "balanced_gpu",
            StarterPackHardwareProfile.TurboGpu => "turbo_gpu",
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
        };

    public static StarterPackHardwareProfile FromHardwareQualityPreset(HardwareQualityPreset preset) =>
        preset switch
        {
            HardwareQualityPreset.CpuSafe => StarterPackHardwareProfile.CpuSafe,
            HardwareQualityPreset.Balanced => StarterPackHardwareProfile.BalancedGpu,
            HardwareQualityPreset.Quality or HardwareQualityPreset.Turbo => StarterPackHardwareProfile.TurboGpu,
            _ => StarterPackHardwareProfile.BalancedGpu
        };
}
