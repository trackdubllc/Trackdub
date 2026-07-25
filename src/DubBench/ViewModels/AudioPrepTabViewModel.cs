using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubBench.Services;
using Trackdub.Benchmarks;

namespace DubBench.ViewModels;

public sealed partial class AudioPrepTabViewModel : ObservableObject, ITabViewModel
{
    private readonly IBenchmarkRunnerService _runner;

    public string Title => "Audio-Prep";
    public string IconGlyph => "\U0001F3A4";

    [ObservableProperty]
    private bool _isSelected;

    // Manifest path
    [ObservableProperty]
    private string _manifestPath = string.Empty;

    // Output path
    [ObservableProperty]
    private string _outputPath = string.Empty;

    // Running state
    [ObservableProperty]
    private bool _isRunning;

    // Result
    [ObservableProperty]
    private AudioPrepBenchmarkReport? _lastResult;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public AudioPrepTabViewModel()
        : this(new BenchmarkRunnerService())
    {
    }

    public AudioPrepTabViewModel(IBenchmarkRunnerService runner)
    {
        _runner = runner;
    }

    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        if (IsRunning) return;

        try
        {
            IsRunning = true;
            StatusMessage = "Running audio-prep benchmark...";

            var options = new AudioPrepBenchmarkOptions(
                ManifestPath: ManifestPath,
                OutputPath: string.IsNullOrWhiteSpace(OutputPath)
                    ? Path.Combine(Path.GetTempPath(), "DubBench", $"audioprep-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json")
                    : OutputPath,
                ReportFormat: Trackdub.Benchmarks.ReportFormat.Both,
                ShowHelp: false);

            LastResult = await _runner.RunAudioPrepBenchmarkAsync(options);
            if (LastResult is null)
            {
                StatusMessage = "Audio-prep benchmark did not return a result.";
                return;
            }

            StatusMessage = $"Completed \u2014 {LastResult.Fixtures.Count} fixtures, " +
                $"{LastResult.Aggregate.AutoComparisonCount} comparisons";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }
}
