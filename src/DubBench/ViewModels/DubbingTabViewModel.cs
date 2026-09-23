using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubBench.Services;
using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;

namespace DubBench.ViewModels;

public sealed partial class DubbingTabViewModel : ObservableObject, ITabViewModel
{
    private readonly IBenchmarkRunnerService _runner;
    private readonly IRecordingFixtureSource _recording;

    public string Title => "Dubbing";
    public string IconGlyph => "\U0001F3AC";
    public IReadOnlyList<string> RunModes { get; } =
        ["warm-host", "artifact-resume"];

    [ObservableProperty]
    private bool _isSelected;

    // Input video path
    [ObservableProperty]
    private string _inputPath = string.Empty;

    // Target language
    [ObservableProperty]
    private string _targetLanguage = "es";

    [ObservableProperty]
    private string _stage = string.Empty;

    [ObservableProperty]
    private string _requestedModel = string.Empty;

    [ObservableProperty]
    private string _requestedProvider = string.Empty;

    [ObservableProperty]
    private string _runMode = "warm-host";

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    // Running state
    [ObservableProperty]
    private bool _isRunning;

    // Recording fixture availability (webcam/mic)
    [ObservableProperty]
    private bool _recordingFixtureAvailable;

    // Recording state
    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private string _capturedFilePath = string.Empty;

    // Result
    [ObservableProperty]
    private BenchmarkEvidenceReport? _lastResult;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public DubbingTabViewModel()
        : this(new BenchmarkRunnerService(), new RecordingFixtureSource())
    {
    }

    public DubbingTabViewModel(IBenchmarkRunnerService runner, IRecordingFixtureSource recording)
    {
        _runner = runner;
        _recording = recording;
        _ = ProbeRecordingAsync();
    }

    private Task ProbeRecordingAsync() => ProbeRecordingSafeAsync();

    private async Task ProbeRecordingSafeAsync()
    {
        try
        {
            RecordingFixtureAvailable = await _recording.ProbeAvailabilityAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Recording probe failed: {ex.Message}";
            RecordingFixtureAvailable = false;
        }
    }

    [RelayCommand]
    private async Task CaptureRecordingAsync()
    {
        if (IsCapturing) return;

        try
        {
            IsCapturing = true;
            StatusMessage = "Capturing recording fixture...";

            var result = await _recording.CaptureAsync(
                Path.Combine(Path.GetTempPath(), "DubBench", "recordings"),
                TimeSpan.FromSeconds(10));

            if (result is not null)
            {
                CapturedFilePath = result.OutputPath;
                InputPath = result.OutputPath;
                StatusMessage = $"Captured {result.Duration.TotalSeconds:F1}s from " +
                    $"default device to {Path.GetFileName(result.OutputPath)}";
            }
            else
            {
                StatusMessage = "Recording failed — no capture device or FFmpeg not found";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Capture error: {ex.Message}";
        }
        finally
        {
            IsCapturing = false;
        }
    }

    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        if (IsRunning) return;

        if (string.IsNullOrWhiteSpace(InputPath))
        {
            StatusMessage = "Select or record an input video before running the dubbing benchmark.";
            return;
        }

        if (!File.Exists(InputPath))
        {
            StatusMessage = $"Input file not found: {InputPath}";
            return;
        }

        try
        {
            IsRunning = true;
            StatusMessage = "Running dubbing benchmark...";

            var options = new ControlledDubbingBenchmarkOptions
            {
                FixturePath = InputPath,
                OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory)
                    ? Path.Combine(Path.GetTempPath(), "Trackdub", "benchmark-runs")
                    : OutputDirectory,
                TargetLanguage = TargetLanguage,
                Stage = EmptyToNull(Stage),
                Model = EmptyToNull(RequestedModel),
                Provider = EmptyToNull(RequestedProvider),
                Mode = RunMode
            };

            LastResult = await _runner.RunControlledDubbingBenchmarkAsync(options);
            if (LastResult is null)
            {
                StatusMessage = "Dubbing benchmark did not return a result.";
                return;
            }

            StatusMessage = LastResult.Status == BenchmarkEvidenceStatus.Completed
                ? $"Measured run completed: {LastResult.RunId}. Open Local Runs to compare compatible results."
                : $"{LastResult.Status}: {LastResult.Reason ?? "See stage outcomes."}";
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

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
