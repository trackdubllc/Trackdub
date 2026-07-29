namespace DubBench.Models;

public sealed record LeaderboardEntry(
    string Benchmark,
    string Hardware,
    double Score,
    DateTime Timestamp)
{
    public string Summary => $"{Benchmark} on {Hardware} — {Score:F1} pts ({Timestamp:g})";
}
