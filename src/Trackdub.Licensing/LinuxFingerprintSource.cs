namespace Trackdub.Licensing;

/// <summary>
/// Reads the Linux machine identifier from /etc/machine-id.
/// </summary>
internal sealed class LinuxFingerprintSource : IFingerprintSource
{
    private const string MachineIdPath = "/etc/machine-id";

    public string GetRawMachineId()
    {
        if (!File.Exists(MachineIdPath))
        {
            throw new InvalidOperationException(
                $"Linux machine-id file not found at {MachineIdPath}.");
        }

        var machineId = File.ReadAllText(MachineIdPath).Trim();

        if (string.IsNullOrWhiteSpace(machineId))
        {
            throw new InvalidOperationException(
                "Linux machine-id file is empty.");
        }

        return machineId;
    }
}
