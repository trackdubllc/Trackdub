using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DubBench.Services;
using DubBench.ViewModels;
using DubBench.Views;

namespace DubBench;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ILocalScoreCacheService scoreCache = new LocalScoreCacheService();
            var vm = new BenchmarkWindowViewModel(scoreCache);
            desktop.MainWindow = new BenchmarkWindow(vm);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
