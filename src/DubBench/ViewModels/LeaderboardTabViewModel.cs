using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;

namespace DubBench.ViewModels;

public sealed record LocalBenchmarkEntry(
    Guid RunId,
    string Scenario,
    string RunMode,
    string Status,
    string Provider,
    string Duration,
    DateTimeOffset CompletedAtUtc);

public sealed partial class LeaderboardTabViewModel : ObservableObject, ITabViewModel
{
    private readonly IBenchmarkEvidenceRepository _history;

    public string Title => "Local Runs";
    public string IconGlyph => "\U0001F4CA";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "No measured benchmark runs yet.";

    [ObservableProperty]
    private int _runCount;

    public ObservableCollection<LocalBenchmarkEntry> Entries { get; } = new();

    public LeaderboardTabViewModel(IBenchmarkEvidenceRepository history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
            return;

        try
        {
            IsLoading = true;
            IReadOnlyList<BenchmarkEvidenceReport> reports = await _history.ListRecentAsync(
                BenchmarkEvidenceKind.Benchmark, 100);
            Entries.Clear();
            foreach (BenchmarkEvidenceReport report in reports)
            {
                string provider = report.ActualProvider ?? "Unknown provider";
                string duration = report.TimingsMilliseconds.TryGetValue("total", out double? total) && total.HasValue
                    ? $"{total.Value:F0} ms"
                    : "Time unavailable";
                Entries.Add(new LocalBenchmarkEntry(
                    report.RunId,
                    report.Scenario,
                    report.RunMode,
                    report.Status.ToString(),
                    provider,
                    duration,
                    report.CompletedAtUtc));
            }

            RunCount = Entries.Count;
            StatusMessage = RunCount == 0
                ? "No measured benchmark runs yet. Run a configured benchmark, then refresh."
                : $"{RunCount} local benchmark run{(RunCount == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load local runs: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
