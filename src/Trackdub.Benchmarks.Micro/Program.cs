using BenchmarkDotNet.Running;

namespace Trackdub.Benchmarks.Micro;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            args =
            [
                "--filter",
                "Trackdub.Benchmarks.Micro.AudioResamplingBenchmarks.*",
                "Trackdub.Benchmarks.Micro.DeepFilterNetSignalBenchmarks.*",
                "Trackdub.Benchmarks.Micro.LatentSyncTensorBenchmarks.NormalizeRgba*",
            ];
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
