namespace Trackdub.Domain.StageRuns;

/// <summary>
/// Canonical stage name constants used when creating and querying <see cref="StageRunRecord"/> entries.
/// All <c>StageRunRecord.Start</c> call sites must use these constants — never inline string literals.
/// </summary>
public static class StageNames
{
    public const string Vad = "vad";
    public const string Asr = "asr";
    public const string Diarization = "diarization";
    public const string SpeakerAssignment = "speaker-assignment";
    public const string Translation = "translation";
    public const string TextRefinement = "text-refinement";
    public const string TextRefinementAsr = "text-refinement-asr";
    public const string TextRefinementTranslation = "text-refinement-translation";
    public const string Tts = "tts";
    public const string Separation = "separation";
    public const string SpeechEnhancement = "speech-enhancement";
    public const string AudioPreparation = "audio-preparation";
    public const string PreviewMix = "preview-mix";
    public const string VoiceCloning = "voice-cloning";
    public const string Export = "export";
    public const string LipSync = "lip-sync";
    public const string LipSynthesis = "lip-synthesis";
    public const string OverlapRescue = "overlap-rescue";
}
