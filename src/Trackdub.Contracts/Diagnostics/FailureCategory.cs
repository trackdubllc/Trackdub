namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Classifies the broad category of a failure encountered during application operation.
/// </summary>
public enum FailureCategory
{
    ModelLoadFailure = 1,
    InferenceFailure = 2,
    MediaDecodeFailure = 3,
    PersistenceFailure = 4,
    UiCrash = 5,
    UnknownError = 6
}
