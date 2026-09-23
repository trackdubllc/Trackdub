using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubBench.Services;
using DubBench.Views;
using Trackdub.Contracts.Persistence;

namespace DubBench.ViewModels;

public sealed partial class BenchmarkWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private ITabViewModel? _selectedTab;

    [ObservableProperty]
    private Control? _currentTabView;

    public ObservableCollection<ITabViewModel> Tabs { get; } = new();

    public BenchmarkWindowViewModel(IBenchmarkRunnerService runner, IBenchmarkEvidenceRepository history)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(history);

        Tabs.Add(new OnnxModelTabViewModel(runner, new OliveOptimizationService()));
        Tabs.Add(new AudioPrepTabViewModel(runner));
        Tabs.Add(new DubbingTabViewModel(runner, new RecordingFixtureSource()));
        Tabs.Add(new LeaderboardTabViewModel(history));

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
            3 => new LeaderboardTabView { DataContext = (LeaderboardTabViewModel)tab },
            _ => CurrentTabView
        };
    }
}
