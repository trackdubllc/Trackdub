using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubBench.Services;
using DubBench.Views;

namespace DubBench.ViewModels;

public sealed partial class BenchmarkWindowViewModel : ObservableObject
{
    private readonly ILocalScoreCacheService _scoreCache;

    [ObservableProperty]
    private ITabViewModel? _selectedTab;

    [ObservableProperty]
    private Control? _currentTabView;

    public ObservableCollection<ITabViewModel> Tabs { get; } = new();

    public BenchmarkWindowViewModel()
        : this(new LocalScoreCacheService())
    {
    }

    public BenchmarkWindowViewModel(ILocalScoreCacheService scoreCache)
    {
        _scoreCache = scoreCache ?? throw new ArgumentNullException(nameof(scoreCache));

        Tabs.Add(new OnnxModelTabViewModel());
        Tabs.Add(new AudioPrepTabViewModel());
        Tabs.Add(new DubbingTabViewModel());
        Tabs.Add(new PresetsTabViewModel());
        Tabs.Add(new LeaderboardTabViewModel(_scoreCache));

        if (Tabs.Count > 0)
            SelectTab(0);
    }

    [RelayCommand]
    private void SelectTab(int index)
    {
        if (index < 0 || index >= Tabs.Count)
            return;

        foreach (var t in Tabs)
            t.IsSelected = false;
        var tab = Tabs[index];
        tab.IsSelected = true;
        SelectedTab = tab;

        CurrentTabView = index switch
        {
            0 => new OnnxModelTabView { DataContext = (OnnxModelTabViewModel)tab },
            1 => new AudioPrepTabView { DataContext = (AudioPrepTabViewModel)tab },
            2 => new DubbingTabView { DataContext = (DubbingTabViewModel)tab },
            3 => new PresetsTabView { DataContext = (PresetsTabViewModel)tab },
            4 => new LeaderboardTabView { DataContext = (LeaderboardTabViewModel)tab },
            _ => CurrentTabView
        };
    }
}
