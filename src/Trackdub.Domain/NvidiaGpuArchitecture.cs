namespace Trackdub.Domain;

public enum NvidiaGpuArchitectureBucket
{
    Unknown = 0,
    Turing,
    Ampere,
    Ada,
    Blackwell
}

public static class NvidiaGpuArchitectureClassifier
{
    public static NvidiaGpuArchitectureBucket ClassifyFromGpuDescription(string? gpuDescription)
    {
        if (string.IsNullOrWhiteSpace(gpuDescription))
        {
            return NvidiaGpuArchitectureBucket.Unknown;
        }

        return ClassifyFromName(gpuDescription);
    }

    public static NvidiaGpuArchitectureBucket ClassifyFromName(string gpuName)
    {
        if (gpuName.Contains("RTX 50", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("Blackwell", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("B200", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("GB200", StringComparison.OrdinalIgnoreCase))
        {
            return NvidiaGpuArchitectureBucket.Blackwell;
        }

        if (gpuName.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("Ada", StringComparison.OrdinalIgnoreCase))
        {
            return NvidiaGpuArchitectureBucket.Ada;
        }

        if (gpuName.Contains("RTX 30", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("A30", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("Ampere", StringComparison.OrdinalIgnoreCase))
        {
            return NvidiaGpuArchitectureBucket.Ampere;
        }

        if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("Quadro", StringComparison.OrdinalIgnoreCase))
        {
            return NvidiaGpuArchitectureBucket.Turing;
        }

        return NvidiaGpuArchitectureBucket.Unknown;
    }

    public static string ToAfxArchitectureBucket(NvidiaGpuArchitectureBucket architecture) =>
        architecture switch
        {
            NvidiaGpuArchitectureBucket.Blackwell => "blackwell",
            NvidiaGpuArchitectureBucket.Ada => "ada",
            NvidiaGpuArchitectureBucket.Ampere => "ampere",
            NvidiaGpuArchitectureBucket.Turing => "turing",
            _ => "unsupported"
        };
}
