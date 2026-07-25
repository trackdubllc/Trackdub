namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Null-object implementation of <see cref="IOpenVinoAvailabilityProvider"/> that always
/// reports OpenVINO as unavailable. Used as a default when the OpenVINO bootstrapper
/// has not been registered (e.g., before composition wiring is complete).
/// </summary>
public sealed class NullOpenVinoAvailabilityProvider : IOpenVinoAvailabilityProvider
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public bool UseOpenVinoCpuProxy => false;
}
