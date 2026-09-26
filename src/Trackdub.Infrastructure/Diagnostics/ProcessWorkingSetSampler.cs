using System.Diagnostics;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>Samples the current process working set; child-process memory is excluded.</summary>
public sealed class ProcessWorkingSetSampler : IWorkingSetSampler
{
    public long CaptureWorkingSetBytes()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64;
    }
}
