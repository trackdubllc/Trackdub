using System.Collections.ObjectModel;
using System.Text.Json;
using DubBench.Models;

namespace DubBench.Services;

/// <summary>
/// Persists benchmark scores to a local JSON file at
/// %LOCALAPPDATA%/Trackdub/benchmark-scores.json.
/// </summary>
public sealed class LocalScoreCacheService : ILocalScoreCacheService
{
    private readonly string _cachePath;
    private readonly ObservableCollection<LeaderboardEntry> _entries = new();

    public int Count => _entries.Count;

    public LocalScoreCacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cachePath = Path.Combine(appData, "Trackdub", "benchmark-scores.json");
        Refresh();
    }

    public IReadOnlyList<LeaderboardEntry> GetEntries() => _entries;

    public void AddEntry(LeaderboardEntry entry)
    {
        _entries.Insert(0, entry);
        Persist();
    }

    public void Clear()
    {
        _entries.Clear();
        Persist();
    }

    public void Refresh()
    {
        _entries.Clear();

        if (!File.Exists(_cachePath))
        {
            SeedMockData();
            Persist();
            return;
        }

        try
        {
            var json = File.ReadAllText(_cachePath);
            var loaded = JsonSerializer.Deserialize<List<LeaderboardEntryJson>>(json);
            if (loaded is not null)
            {
                foreach (var item in loaded)
                {
                    _entries.Add(new LeaderboardEntry(
                        item.Benchmark,
                        item.Hardware,
                        item.Score,
                        item.Timestamp));
                }
            }
        }
        catch
        {
            SeedMockData();
        }
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_entries.Select(e => new LeaderboardEntryJson
            {
                Benchmark = e.Benchmark,
                Hardware = e.Hardware,
                Score = e.Score,
                Timestamp = e.Timestamp
            }).ToList(), new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // Silently fail - cache is non-critical
        }
    }

    private void SeedMockData()
    {
        _entries.Add(new LeaderboardEntry("ONNX (Silero VAD)", "RTX 4090", 9850, new DateTime(2026, 5, 20, 14, 30, 0, DateTimeKind.Utc)));
        _entries.Add(new LeaderboardEntry("ONNX (Silero VAD)", "RTX 4080", 7200, new DateTime(2026, 5, 19, 10, 15, 0, DateTimeKind.Utc)));
        _entries.Add(new LeaderboardEntry("ONNX (Silero VAD)", "RTX 4070", 5100, new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc)));
        _entries.Add(new LeaderboardEntry("ONNX (Silero VAD)", "DirectML Fallback", 3200, new DateTime(2026, 5, 17, 16, 45, 0, DateTimeKind.Utc)));
        _entries.Add(new LeaderboardEntry("Audio-Prep (Default)", "RTX 4090", 12400, new DateTime(2026, 5, 20, 15, 0, 0, DateTimeKind.Utc)));
        _entries.Add(new LeaderboardEntry("Dubbing (30s Spanish)", "RTX 4090", 45600, new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc)));
        _entries.Add(new LeaderboardEntry("Dubbing (30s Spanish)", "CPU (AVX2)", 89200, new DateTime(2026, 5, 18, 9, 30, 0, DateTimeKind.Utc)));
    }

    private sealed class LeaderboardEntryJson
    {
        public string Benchmark { get; set; } = "";
        public string Hardware { get; set; } = "";
        public double Score { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
