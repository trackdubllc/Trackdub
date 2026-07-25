using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Pipelines.Transcript;

public sealed class ScriptedAudioTranscriptionEngine : IAudioTranscriptionEngine
{
    private static readonly string[] Phrases =
    [
        "Welcome to the first Trackdub transcript draft.",
        "This placeholder segment proves the transcript slice can reopen without recomputing.",
        "Edits to this text should persist as a new transcript revision.",
        "Later milestones will replace this scripted engine with the Windows ML path.",
        "The current milestone is focused on transcript flow, provenance, and persistence.",
        "This segment exists so longer media produces multiple transcript rows."
    ];

    public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        string normalizedAudioPath,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RecognizedTranscriptSegment[] segments = regions
            .OrderBy(region => region.Index)
            .Select(region =>
            {
                string text = BuildText(region.Index);
                return new RecognizedTranscriptSegment(
                    region.Index,
                    region.StartSeconds,
                    region.EndSeconds,
                    text,
                    DetectedLanguage: "en",
                    Words: BuildWords(region, text));
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>(segments);
    }

    private static string BuildText(int index)
    {
        string phrase = Phrases[index % Phrases.Length];
        return $"Segment {index + 1}. {phrase}";
    }

    private static IReadOnlyList<RecognizedTranscriptWord> BuildWords(SpeechRegion region, string text)
    {
        string[] words = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        double duration = Math.Max(0.01d, region.EndSeconds - region.StartSeconds);
        double step = duration / words.Length;
        return words
            .Select((word, index) => new RecognizedTranscriptWord(
                index,
                region.StartSeconds + (step * index),
                index == words.Length - 1 ? region.EndSeconds : region.StartSeconds + (step * (index + 1)),
                word,
                confidence: index == words.Length - 1 && region.Index % 3 == 1 ? 0.68d : 0.92d))
            .ToArray();
    }
}
