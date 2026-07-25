using Trackdub.Domain;

namespace Trackdub.Infrastructure.Components.NvidiaAfx;

public interface INvidiaAfxArchitectureDetector
{
    string DetectArchitectureBucket();
}

public sealed class NvidiaAfxArchitectureDetector : INvidiaAfxArchitectureDetector
{
    public string DetectArchitectureBucket()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "unsupported";
        }

        string gpuName = Environment.GetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(gpuName))
        {
            return "turing";
        }

        return DetectOverrideArchitectureBucket(gpuName);
    }

    internal static string DetectOverrideArchitectureBucket(string gpuName)
    {
        NvidiaGpuArchitectureBucket architecture = NvidiaGpuArchitectureClassifier.ClassifyFromName(gpuName);
        return architecture is NvidiaGpuArchitectureBucket.Unknown
            ? "turing"
            : NvidiaGpuArchitectureClassifier.ToAfxArchitectureBucket(architecture);
    }
}
