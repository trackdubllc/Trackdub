namespace Trackdub.Sdk;

public enum ErrorCode
{
    InvalidArgument = 0,
    MediaNotFound = 1,
    ProjectNotFound = 2,
    ModelNotAvailable = 3,
    RuntimeUnavailable = 4,
    StagePrerequisiteMissing = 5,
    StageFailed = 6,
    ExportFailed = 7,
    Cancelled = 8,
    ProjectLocked = 9,
    PreFlightFailed = 10,
    BlockedNonCommercial = 11,
}
