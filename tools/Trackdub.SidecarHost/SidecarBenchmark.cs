using System.Diagnostics;

namespace Trackdub.SidecarHost;

/// <summary>
/// Measures the sidecar tax on the live worker: N warm infer calls, recording
/// per-call wall time (host write → worker response read). Returns the median
/// and raw samples for the evidence file (docs/development/benchmark-evidence.md
/// schema, kept minimal: host, tfm, timestamp, n, samplesMs, medianMs).
/// </summary>
public static class SidecarBenchmark
{
    public static (double MedianMs, double[] SamplesMs) MeasureWarmInfer(
        SupervisedSidecarWorker worker,
        int warmup = 3,
        int samples = 30)
    {
        var text = SidecarTensor.Utf8("The quick brown fox jumps over the lazy dog.");
        for (int i = 0; i < warmup; i++)
        {
            worker.Request(new SidecarRequest($"w{i}", "infer", Inputs: new() { ["text"] = text }));
        }

        var samplesMs = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            var sw = Stopwatch.StartNew();
            SidecarResponse resp = worker.Request(new SidecarRequest($"m{i}", "infer", Inputs: new() { ["text"] = text }));
            sw.Stop();
            if (resp.Status != "ok")
            {
                throw new InvalidOperationException($"infer {i} failed: {resp.Reason}");
            }

            samplesMs[i] = sw.Elapsed.TotalMilliseconds;
        }

        double[] sorted = [.. samplesMs.Order()];
        int mid = sorted.Length / 2;
        double median = sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
        return (median, samplesMs);
    }
}
