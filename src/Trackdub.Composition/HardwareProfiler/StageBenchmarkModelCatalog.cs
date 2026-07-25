using Trackdub.Domain;

namespace Trackdub.Composition.HardwareProfiler;

internal static class StageBenchmarkModelCatalog
{
    internal sealed record ScenarioDefinition(
        StageBenchmarkScenario Scenario,
        string ScenarioName,
        IReadOnlyList<string> ModelAliases);

    internal static IReadOnlyList<ScenarioDefinition> All { get; } =
    [
        new(StageBenchmarkScenario.Vad, "vad", ["silero-vad"]),
        new(StageBenchmarkScenario.Asr, "asr", ["whisper-tiny-onnx", "whisper-tiny"]),
        new(StageBenchmarkScenario.Translation, "translation", ["madlad400", "opus-en-es", "helsinki-opus-en-es"]),
        new(StageBenchmarkScenario.Tts, "tts", ["kokoro-onnx", "kokoro"])
    ];
}
