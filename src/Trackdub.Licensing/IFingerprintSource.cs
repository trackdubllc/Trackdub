namespace Trackdub.Licensing;

/// <summary>
/// Internal abstraction for platform-specific raw machine identifier retrieval.
/// </summary>
internal interface IFingerprintSource
{
    string GetRawMachineId();
}
