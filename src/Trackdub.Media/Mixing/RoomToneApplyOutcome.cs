namespace Trackdub.Media.Mixing;

/// <summary>
/// How room-tone acoustic matching resolved for one dubbed speech clip.
/// </summary>
public enum RoomToneApplyOutcome
{
    AppliedFromPreRoll,
    AppliedFromFallbackImpulse,
    SkippedInputEmpty,
    SkippedPreRollTooShort,
    SkippedPreRollSilent,
    SkippedPreRollImpulseSilent,
}
