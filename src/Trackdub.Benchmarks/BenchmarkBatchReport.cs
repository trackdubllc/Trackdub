using Trackdub.Domain;

namespace Trackdub.Benchmarks;

public sealed record BenchmarkBatchReport(
    string RequestedReference,
    string ReportPath,
    IReadOnlyList<BenchmarkReport> Results,
    DateTimeOffset GeneratedAtUtc);
