namespace Trackdub.Contracts.Benchmarking;

/// <summary>Reads the current process working set for interval peak sampling.</summary>
public interface IWorkingSetSampler
{
    long CaptureWorkingSetBytes();
}
