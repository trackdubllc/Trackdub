using System.Globalization;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks;

internal static class ResourceTelemetryOptionsParser
{
    public const string Usage = "[--max-cpu-percent <0..100>] [--max-working-set-bytes <bytes>] [--max-allocated-bytes <bytes>] [--min-available-vram-mb <mb>] [--max-gpu-bytes <bytes>]";
    public const string Description = "Resource limits are optional: normalized CPU percent (0..100), sampled peak working-set bytes (25 ms cadence), managed allocated bytes, a minimum free-VRAM floor in MB, and a maximum for this process's dedicated GPU memory in bytes. The VRAM floor is adapter-wide headroom, not this process's allocation, so it detects memory pressure; --max-gpu-bytes bounds the process-isolated reading instead, so it attributes GPU memory to this run. Byte and VRAM limits must be nonnegative integers; omitted limits are unbounded. A VRAM floor above the host's total video memory is physically impossible and is rejected before the run starts.";

    public static bool IsResourceOption(string option) => option is
        "--max-cpu-percent" or
        "--max-working-set-bytes" or
        "--max-allocated-bytes" or
        "--min-available-vram-mb" or
        "--max-gpu-bytes";

    public static bool TryApply(
        string option,
        string value,
        ResourceTelemetryBounds bounds,
        TextWriter error,
        out ResourceTelemetryBounds updatedBounds)
    {
        updatedBounds = bounds;
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine($"Missing value for {option}.");
            return false;
        }

        switch (option)
        {
            case "--max-cpu-percent":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cpu) ||
                    !double.IsFinite(cpu) || cpu < 0 || cpu > 100)
                {
                    error.WriteLine($"Invalid value '{value}' for {option}. Expected a finite normalized percentage from 0 to 100.");
                    return false;
                }

                updatedBounds = bounds with { MaxCpuPercent = cpu };
                return true;
            case "--max-working-set-bytes":
            case "--max-allocated-bytes":
            case "--min-available-vram-mb":
            case "--max-gpu-bytes":
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) || bytes < 0)
                {
                    error.WriteLine($"Invalid value '{value}' for {option}. Expected a nonnegative 64-bit integer byte count.");
                    return false;
                }

                updatedBounds = option switch
                {
                    "--max-working-set-bytes" => bounds with { MaxWorkingSetBytes = bytes },
                    "--max-allocated-bytes" => bounds with { MaxManagedAllocatedBytes = bytes },
                    "--max-gpu-bytes" => bounds with { MaxGpuBytes = bytes },
                    _ => bounds with { MinAvailableVramMb = bytes },
                };
                return true;
            default:
                error.WriteLine($"Unknown option {option}.");
                return false;
        }
    }
}
