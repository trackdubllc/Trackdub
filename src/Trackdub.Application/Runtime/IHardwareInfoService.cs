namespace Trackdub.Application.Runtime;

public sealed record HardwareDisplaySummary(
    string CpuName,
    string GpuName,
    string VramDisplay,
    string RamDisplay);

public interface IHardwareInfoService
{
    Task<HardwareDisplaySummary> GetSummaryAsync(CancellationToken cancellationToken = default);
}
