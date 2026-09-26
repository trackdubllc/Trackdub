using System.Globalization;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks;

internal static class ResourceTelemetryOptionsParser
{
    public const string Usage = "[--max-cpu-percent <0..100>] [--max-working-set-bytes <bytes>] [--max-allocated-bytes <bytes>] [--min-available-vram-mb <mb>]";
    public const string Description = "Resource limits are optional: normalized CPU percent (0..100), endpoint working set bytes, managed allocated bytes, and a minimum free-VRAM floor in MB. The VRAM floor is adapter-wide headroom, not this process's allocation, so it detects memory pressure rather than attributing bytes to a stage. Byte and VRAM limits must be nonnegative integers; omitted limits are unbounded.";

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
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) || bytes < 0)
                {
                    error.WriteLine($"Invalid value '{value}' for {option}. Expected a nonnegative 64-bit integer byte count.");
                    return false;
                }

                updatedBounds = option switch
                {
                    "--max-working-set-bytes" => bounds with { MaxWorkingSetBytes = bytes },
                    "--max-allocated-bytes" => bounds with { MaxManagedAllocatedBytes = bytes },
                    _ => bounds with { MinAvailableVramMb = bytes },
                };
                return true;
            default:
                error.WriteLine($"Unknown option {option}.");
                return false;
        }
    }
}
