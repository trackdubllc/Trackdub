namespace Trackdub.Contracts.ModelOptimization;

public sealed record OliveEnvironmentStatus(
    bool PythonAvailable,
    string? PythonVersion,
    bool VenvExists,
    bool OliveInstalled,
    string? SystemOlivePath = null,
    string? OliveVersion = null,
    bool IsSupportedOliveVersion = true,
    IReadOnlyList<string>? MissingCapabilities = null)
{
    public IReadOnlyList<string> MissingCapabilities { get; init; } = MissingCapabilities ?? [];

    public bool IsReady =>
        IsSupportedOliveVersion &&
        MissingCapabilities.Count == 0 &&
        (SystemOlivePath is not null || (PythonAvailable && VenvExists && OliveInstalled));
}
