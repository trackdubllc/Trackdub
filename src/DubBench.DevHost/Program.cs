using Avalonia;
using DubBench;
using DubBench.Services;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.Infrastructure.Settings;

namespace DubBench.DevHost;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var history = new BenchmarkEvidenceRepository(
            new SqliteUserBenchmarkDatabase(new TrackdubStoragePaths().UserDataRoot));
        var runner = new BenchmarkRunnerService(history);
        var builder = AppBuilder.Configure(() => new App(runner, history))
            .UsePlatformDetect()
            .LogToTrace();
#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder;
    }
}
