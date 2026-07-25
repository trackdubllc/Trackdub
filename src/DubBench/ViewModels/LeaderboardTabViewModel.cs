using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubBench.Models;
using DubBench.Services;

namespace DubBench.ViewModels;

public sealed partial class LeaderboardTabViewModel : ObservableObject, ITabViewModel
{
    private readonly ILocalScoreCacheService _cache;

    public string Title => "Leaderboard";
    public string IconGlyph => "\U0001F3C6";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLocalMock = true;

    [ObservableProperty]
    private string _disclaimerText = "\u26A0 LOCAL MOCK \u2014 no backend. Scores cached locally only.";

    [ObservableProperty]
    private int _scoreCount;

    [ObservableProperty]
    private string _lastUpdatedText = "Never";

    public ObservableCollection<LeaderboardEntry> Entries { get; } = new();

    public LeaderboardTabViewModel(ILocalScoreCacheService cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        LoadFromCache();
    }

    [RelayCommand]
    private void Refresh()
    {
        _cache.Refresh();
        LoadFromCache();
    }

    public void SaveToCache(LeaderboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _cache.AddEntry(entry);
        LoadFromCache();
    }

    private void LoadFromCache()
    {
        Entries.Clear();
        foreach (LeaderboardEntry entry in _cache.GetEntries())
        {
            Entries.Add(entry);
        }

        ScoreCount = Entries.Count;
        LastUpdatedText = Entries.Count > 0
            ? Entries.Max(e => e.Timestamp).ToString("g")
            : "No entries yet";
    }
}
