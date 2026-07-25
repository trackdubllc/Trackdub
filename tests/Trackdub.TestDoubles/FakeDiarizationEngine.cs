using System.Text.Json;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeDiarizationEngine : ISpeakerDiarizationEngine, IStageRuntimeExecutionReporter
{
    private const string FixtureJson =
        """
        [
          { "speakerKey": "spk_0", "startSeconds": 0.0, "endSeconds": 5.8, "confidence": 0.93, "hasOverlap": false },
          { "speakerKey": "spk_1", "startSeconds": 6.0, "endSeconds": 11.8, "confidence": 0.88, "hasOverlap": true }
        ]
        """;

    private static IReadOnlyList<DiarizedSpeakerTurn>? fixtureTurns;

    private static IReadOnlyList<DiarizedSpeakerTurn> FixtureTurns
    {
        get
        {
            if (fixtureTurns is null)
            {
                fixtureTurns = JsonSerializer.Deserialize<List<DiarizedSpeakerTurn>>(
                    FixtureJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Fake diarization fixture JSON could not be deserialized.");
            }

            return fixtureTurns;
        }
    }

    public int CallCount { get; private set; }

    public bool ThrowOnDiarize { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public IReadOnlyList<DiarizedSpeakerTurn>? OverrideTurns { get; set; }

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
        string normalizedAudioPath,
        double durationSeconds,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAudioPath);
        ArgumentNullException.ThrowIfNull(speechRegions);
        cancellationToken.ThrowIfCancellationRequested();

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        if (ThrowOnDiarize)
        {
            throw new InvalidOperationException("Fake diarization failed.");
        }

        CallCount++;
        LastExecutionSummary = new StageRuntimeExecutionSummary(
            "auto",
            "cpu",
            "fake/sortformer",
            "sortformer-diarizer-4spk-v2.1",
            "default",
            "Fixture JSON diarization");
        return Task.FromResult(OverrideTurns ?? FixtureTurns);
    }
}
