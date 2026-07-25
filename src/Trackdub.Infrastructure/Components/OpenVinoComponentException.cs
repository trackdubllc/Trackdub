namespace Trackdub.Infrastructure.Components;

/// <summary>
/// Exception thrown when an OpenVINO component operation (download, verification, extraction) fails.
/// </summary>
public sealed class OpenVinoComponentException : Exception
{
    public OpenVinoComponentException(string message)
        : base(message)
    {
    }

    public OpenVinoComponentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
