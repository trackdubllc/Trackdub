using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DubBench.Services;
using DubBench.ViewModels;
using DubBench.Views;
using Trackdub.Contracts.Persistence;

namespace DubBench;

public partial class App : Application
{
    private readonly IBenchmarkRunnerService? _runner;
    private readonly IBenchmarkEvidenceRepository? _history;

    // Avalonia's XAML loader requires a public default constructor for the resource.
    // Hosts must use the injected constructor before starting a desktop lifetime.
    public App()
    {
    }

    public App(IBenchmarkRunnerService runner, IBenchmarkEvidenceRepository history)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new BenchmarkWindowViewModel(
                _runner ?? throw new InvalidOperationException("DubBench runner was not configured."),
                _history ?? throw new InvalidOperationException("DubBench history was not configured."));
            desktop.MainWindow = new BenchmarkWindow(vm);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
