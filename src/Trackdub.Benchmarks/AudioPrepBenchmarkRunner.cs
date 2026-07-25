using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Benchmarks;

public sealed class AudioPrepBenchmarkRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public async Task<AudioPrepBenchmarkReport> RunAsync(
        AudioPrepBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using FileStream stream = File.OpenRead(options.ManifestPath);
        AudioPrepBenchmarkManifest? manifest = await JsonSerializer.DeserializeAsync<AudioPrepBenchmarkManifest>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            throw new InvalidOperationException("Audio-prep benchmark manifest could not be read.");
        }

        AudioPrepBenchmarkFixtureReport[] fixtureReports = manifest.Fixtures
            .Select(BuildFixtureReport)
            .ToArray();
        AudioPrepBenchmarkAggregate aggregate = BuildAggregate(fixtureReports);
        return new AudioPrepBenchmarkReport(
            options.ManifestPath,
            options.OutputPath,
            fixtureReports,
            aggregate,
            DateTimeOffset.UtcNow);
    }

    private static AudioPrepBenchmarkFixtureReport BuildFixtureReport(AudioPrepBenchmarkFixture fixture)
    {
        AudioPrepBenchmarkProfileReport[] profiles = fixture.Results
            .Select(result => BuildProfileReport(fixture, result))
            .ToArray();
        AudioPrepBenchmarkProfileReport? raw = profiles.FirstOrDefault(IsRaw);
        AudioPrepBenchmarkProfileReport? auto = profiles.FirstOrDefault(IsAuto);
        AudioPrepBenchmarkComparison? comparison = raw is null || auto is null
            ? null
            : BuildComparison(raw, auto);
        return new AudioPrepBenchmarkFixtureReport(fixture.Id, profiles, comparison);
    }

    private static AudioPrepBenchmarkProfileReport BuildProfileReport(
        AudioPrepBenchmarkFixture fixture,
        AudioPrepBenchmarkProfileInput input)
    {
        string transcript = input.Transcript ?? string.Empty;
        double? wer = string.IsNullOrWhiteSpace(fixture.ReferenceTranscript)
            ? null
            : TextErrorRate.WordErrorRate(fixture.ReferenceTranscript, transcript);
        double? cer = string.IsNullOrWhiteSpace(fixture.ReferenceTranscript)
            ? null
            : TextErrorRate.CharacterErrorRate(fixture.ReferenceTranscript, transcript);

        return new AudioPrepBenchmarkProfileReport(
            input.ProfileId,
            wer,
            cer,
            string.IsNullOrWhiteSpace(transcript) ? 1d : 0d,
            TextErrorRate.RepetitionRate(transcript),
            input.SpeechRegionCount,
            input.SpeechCoverageSeconds,
            input.SpeakerCount,
            input.TurnCount,
            input.ProcessingSeconds,
            input.GuardrailFailures ?? []);
    }

    private static AudioPrepBenchmarkComparison BuildComparison(
        AudioPrepBenchmarkProfileReport raw,
        AudioPrepBenchmarkProfileReport auto)
    {
        double? werDelta = NullableDelta(auto.WordErrorRate, raw.WordErrorRate);
        double? cerDelta = NullableDelta(auto.CharacterErrorRate, raw.CharacterErrorRate);
        double? speechCoverageDelta = NullableDelta(auto.SpeechCoverageSeconds, raw.SpeechCoverageSeconds);
        int? speechRegionDelta = auto.SpeechRegionCount is int autoRegions && raw.SpeechRegionCount is int rawRegions
            ? autoRegions - rawRegions
            : null;
        int? speakerCountDrift = auto.SpeakerCount is int autoSpeakers && raw.SpeakerCount is int rawSpeakers
            ? Math.Abs(autoSpeakers - rawSpeakers)
            : null;
        double? turnFragmentationIncrease = auto.TurnCount is int autoTurns && raw.TurnCount is int rawTurns && rawTurns > 0
            ? Math.Max(0d, (autoTurns - rawTurns) / (double)rawTurns)
            : null;
        bool diarizationAccepted = (speakerCountDrift is null or <= 1) &&
                                   (turnFragmentationIncrease is null or <= 0.10d);
        bool accepted = (werDelta is null or <= 0.02d) &&
                        (cerDelta is null or <= 0.02d) &&
                        auto.BlankTranscriptRate <= raw.BlankTranscriptRate &&
                        auto.RepetitionRate <= raw.RepetitionRate + 0.05d &&
                        diarizationAccepted &&
                        auto.GuardrailFailures.Count == 0;

        return new AudioPrepBenchmarkComparison(
            werDelta,
            cerDelta,
            speechCoverageDelta,
            speechRegionDelta,
            speakerCountDrift,
            turnFragmentationIncrease,
            diarizationAccepted,
            accepted);
    }

    private static AudioPrepBenchmarkAggregate BuildAggregate(
        IReadOnlyList<AudioPrepBenchmarkFixtureReport> fixtureReports)
    {
        AudioPrepBenchmarkComparison[] comparisons = fixtureReports
            .Select(report => report.AutoComparison)
            .OfType<AudioPrepBenchmarkComparison>()
            .ToArray();

        return new AudioPrepBenchmarkAggregate(
            fixtureReports.Count,
            comparisons.Length,
            comparisons.Count(comparison => comparison.Accepted),
            Average(comparisons.Select(comparison => comparison.WordErrorRateDelta)),
            Average(comparisons.Select(comparison => comparison.CharacterErrorRateDelta)),
            Average(comparisons.Select(comparison => comparison.SpeechCoverageDeltaSeconds)),
            Average(comparisons.Select(comparison => comparison.TurnFragmentationIncreaseRatio)));
    }

    private static bool IsRaw(AudioPrepBenchmarkProfileReport report) =>
        report.ProfileId.Equals("raw", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuto(AudioPrepBenchmarkProfileReport report) =>
        report.ProfileId.Equals("auto", StringComparison.OrdinalIgnoreCase);

    private static double? NullableDelta(double? left, double? right) =>
        left is double leftValue && right is double rightValue ? leftValue - rightValue : null;

    private static double? Average(IEnumerable<double?> values)
    {
        double[] concrete = values.OfType<double>().ToArray();
        return concrete.Length == 0 ? null : concrete.Average();
    }
}

internal static class TextErrorRate
{
    public static double WordErrorRate(string reference, string candidate)
    {
        string[] referenceTokens = TokenizeWords(reference);
        string[] candidateTokens = TokenizeWords(candidate);
        if (referenceTokens.Length == 0)
        {
            return candidateTokens.Length == 0 ? 0d : 1d;
        }

        return EditDistance(referenceTokens, candidateTokens) / (double)referenceTokens.Length;
    }

    public static double CharacterErrorRate(string reference, string candidate)
    {
        char[] referenceChars = Normalize(reference).ToCharArray();
        char[] candidateChars = Normalize(candidate).ToCharArray();
        if (referenceChars.Length == 0)
        {
            return candidateChars.Length == 0 ? 0d : 1d;
        }

        return EditDistance(referenceChars, candidateChars) / (double)referenceChars.Length;
    }

    public static double RepetitionRate(string text)
    {
        string[] tokens = TokenizeWords(text);
        if (tokens.Length < 2)
        {
            return 0d;
        }

        int repeats = 0;
        for (int index = 1; index < tokens.Length; index++)
        {
            if (tokens[index].Equals(tokens[index - 1], StringComparison.Ordinal))
            {
                repeats++;
            }
        }

        return repeats / (double)(tokens.Length - 1);
    }

    private static string[] TokenizeWords(string text) =>
        Normalize(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string text)
    {
        var chars = text
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static int EditDistance<T>(IReadOnlyList<T> reference, IReadOnlyList<T> candidate)
        where T : IEquatable<T>
    {
        int[,] distances = new int[reference.Count + 1, candidate.Count + 1];
        for (int i = 0; i <= reference.Count; i++)
        {
            distances[i, 0] = i;
        }

        for (int j = 0; j <= candidate.Count; j++)
        {
            distances[0, j] = j;
        }

        for (int i = 1; i <= reference.Count; i++)
        {
            for (int j = 1; j <= candidate.Count; j++)
            {
                int substitutionCost = reference[i - 1].Equals(candidate[j - 1]) ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + substitutionCost);
            }
        }

        return distances[reference.Count, candidate.Count];
    }
}
