using Trackdub.Domain.Speakers;

namespace Trackdub.Application.Transcripts;

public sealed record OverlapRegion(
    double StartSeconds,
    double EndSeconds,
    string DetectionSource = "diarization");

public sealed class OverlapRegionDetector
{
    private const double DefaultPadSeconds = 0.25d;
    private const double DefaultMaxRegionSeconds = 30d;
    private const double MergeGapSeconds = 0.5d;

    public IReadOnlyList<OverlapRegion> DetectFromSpeakerTurns(
        IReadOnlyList<SpeakerTurn> turns,
        double? mediaDurationSeconds = null,
        double padSeconds = DefaultPadSeconds,
        double maxRegionSeconds = DefaultMaxRegionSeconds)
    {
        ArgumentNullException.ThrowIfNull(turns);

        List<(double Start, double End)> rawRegions = turns
            .Where(static turn => turn.HasOverlap)
            .Select(static turn => (turn.StartSeconds, turn.EndSeconds))
            .OrderBy(static region => region.StartSeconds)
            .ToList();

        if (rawRegions.Count == 0)
        {
            return [];
        }

        List<(double Start, double End)> merged = MergeAdjacent(rawRegions, MergeGapSeconds);
        var regions = new List<OverlapRegion>(merged.Count);
        foreach ((double start, double end) in merged)
        {
            double paddedStart = Math.Max(0d, start - padSeconds);
            double paddedEnd = mediaDurationSeconds is double duration
                ? Math.Min(duration, end + padSeconds)
                : end + padSeconds;

            if (paddedEnd <= paddedStart)
            {
                continue;
            }

            double regionLength = paddedEnd - paddedStart;
            if (regionLength > maxRegionSeconds)
            {
                double cursor = paddedStart;
                while (cursor < paddedEnd)
                {
                    double chunkEnd = Math.Min(cursor + maxRegionSeconds, paddedEnd);
                    regions.Add(new OverlapRegion(cursor, chunkEnd));
                    cursor = chunkEnd;
                }
            }
            else
            {
                regions.Add(new OverlapRegion(paddedStart, paddedEnd));
            }
        }

        return regions;
    }

    internal static List<(double Start, double End)> MergeAdjacent(
        IReadOnlyList<(double Start, double End)> regions,
        double mergeGapSeconds)
    {
        if (regions.Count <= 1)
        {
            return regions.ToList();
        }

        var merged = new List<(double Start, double End)>();
        (double curStart, double curEnd) = regions[0];
        for (int i = 1; i < regions.Count; i++)
        {
            (double start, double end) = regions[i];
            if (start <= curEnd + mergeGapSeconds)
            {
                curEnd = Math.Max(curEnd, end);
            }
            else
            {
                merged.Add((curStart, curEnd));
                (curStart, curEnd) = (start, end);
            }
        }

        merged.Add((curStart, curEnd));
        return merged;
    }
}
