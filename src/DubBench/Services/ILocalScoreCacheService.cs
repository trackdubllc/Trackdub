using DubBench.Models;

namespace DubBench.Services;

/// <summary>
/// Persists benchmark leaderboard scores to a local JSON file.
/// No backend — local-only cache for dev mock purposes.
/// </summary>
public interface ILocalScoreCacheService
{
    /// <summary>All cached leaderboard entries.</summary>
    IReadOnlyList<LeaderboardEntry> GetEntries();

    /// <summary>Add a new entry to the cache.</summary>
    void AddEntry(LeaderboardEntry entry);

    /// <summary>Clear all cached entries.</summary>
    void Clear();

    /// <summary>Reload entries from disk.</summary>
    void Refresh();

    /// <summary>Number of cached entries.</summary>
    int Count { get; }
}
