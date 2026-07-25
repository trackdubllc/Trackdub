namespace Trackdub.Media.Mixing;

/// <summary>
/// Result of attempting room-tone convolution on a dubbed take.
/// When <see cref="ProcessedSamples"/> is null, the caller should use the dry input unchanged.
/// </summary>
public readonly record struct RoomToneApplyResult(float[]? ProcessedSamples, RoomToneApplyOutcome Outcome);
