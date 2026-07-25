using Microsoft.Extensions.Logging;

namespace Trackdub.Media.Mixing;

/// <summary>
/// Per-render counters for room-tone acoustic matching (preview and export mix).
/// </summary>
internal sealed class RoomToneAcousticMatchStats
{
    private int appliedFromPreRoll;
    private int appliedFromFallbackImpulse;
    private int skippedInputEmpty;
    private int skippedPreRollTooShort;
    private int skippedPreRollSilent;
    private int skippedPreRollImpulseSilent;
    private int clipsConsidered;

    public void Record(RoomToneApplyOutcome outcome)
    {
        clipsConsidered++;
        switch (outcome)
        {
            case RoomToneApplyOutcome.AppliedFromPreRoll:
                appliedFromPreRoll++;
                break;
            case RoomToneApplyOutcome.AppliedFromFallbackImpulse:
                appliedFromFallbackImpulse++;
                break;
            case RoomToneApplyOutcome.SkippedInputEmpty:
                skippedInputEmpty++;
                break;
            case RoomToneApplyOutcome.SkippedPreRollTooShort:
                skippedPreRollTooShort++;
                break;
            case RoomToneApplyOutcome.SkippedPreRollSilent:
                skippedPreRollSilent++;
                break;
            case RoomToneApplyOutcome.SkippedPreRollImpulseSilent:
                skippedPreRollImpulseSilent++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown room-tone outcome.");
        }
    }

    public void LogSummary(ILogger logger, string context)
    {
        if (clipsConsidered == 0)
        {
            return;
        }

        int applied = appliedFromPreRoll + appliedFromFallbackImpulse;
        int skipped = clipsConsidered - applied;
        logger.LogInformation(
            "Room-tone acoustic match ({Context}): clips={Clips} applied={Applied} (preRoll={PreRoll}, fallback={Fallback}) skipped={Skipped} (tooShort={TooShort}, silentPreRoll={SilentPreRoll}, silentImpulse={SilentImpulse}, emptyInput={EmptyInput})",
            context,
            clipsConsidered,
            applied,
            appliedFromPreRoll,
            appliedFromFallbackImpulse,
            skipped,
            skippedPreRollTooShort,
            skippedPreRollSilent,
            skippedPreRollImpulseSilent,
            skippedInputEmpty);
    }
}
