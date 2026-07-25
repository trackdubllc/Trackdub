using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DubBench.ViewModels;

public sealed record BenchmarkPreset(
    string Name,
    string Category,
    string Description,
    int RunCount,
    string Provider)
{
    public BenchmarkPresetResult? LastResult { get; set; }
}

public sealed record BenchmarkPresetResult(
    string PresetName,
    string Category,
    double DurationMs,
    DateTime CompletedAtUtc)
{
    public bool Success => DurationMs > 0;
    public string Summary => $"{PresetName} — {DurationMs:F0}ms ({CompletedAtUtc:g})";
}

public sealed partial class PresetsTabViewModel : ObservableObject, ITabViewModel
{
    public string Title => "Presets";
    public string IconGlyph => "\u2699\uFE0F";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private BenchmarkPreset? _selectedPreset;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isComparisonVisible;

    [ObservableProperty]
    private string _statusMessage = "Select a preset and click Run, or run all.";

    public ObservableCollection<BenchmarkPreset> Presets { get; } = new();
    public ObservableCollection<BenchmarkPresetResult> ComparisonResults { get; } = new();

    public PresetsTabViewModel()
    {
        Presets.Add(new BenchmarkPreset("Quick ONNX", "ONNX Model", "Silero VAD small model, 5 runs", 5, "Auto"));
        Presets.Add(new BenchmarkPreset("Full ONNX", "ONNX Model", "All providers, 50 runs each", 50, "All"));
        Presets.Add(new BenchmarkPreset("Audio-Prep Default", "Audio Preparation", "Default manifest, standard output", 1, "Auto"));
        Presets.Add(new BenchmarkPreset("Dubbing Short", "Dubbing", "30-second video, Spanish target", 1, "Auto"));
        Presets.Add(new BenchmarkPreset("Comprehensive", "All", "Full pipeline: ONNX + Audio-Prep + Dubbing", 5, "All"));
    }

    [RelayCommand]
    private async Task RunPreset()
    {
        if (SelectedPreset is null || IsRunning)
            return;

        IsRunning = true;
        StatusMessage = $"Running \"{SelectedPreset.Name}\"...";

        try
        {
            await Task.Delay(800); // Simulated benchmark duration
            var result = new BenchmarkPresetResult(
                SelectedPreset.Name,
                SelectedPreset.Category,
                Random.Shared.Next(500, 5000),
                DateTime.UtcNow);
            SelectedPreset.LastResult = result;
            ComparisonResults.Insert(0, result);
            IsComparisonVisible = true;
            StatusMessage = $"Completed: {result.Summary}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunAll()
    {
        if (IsRunning)
            return;

        IsRunning = true;
        IsComparisonVisible = true;
        ComparisonResults.Clear();
        StatusMessage = "Running all presets...";

        foreach (var preset in Presets)
        {
            try
            {
                await Task.Delay(400);
                var result = new BenchmarkPresetResult(
                    preset.Name,
                    preset.Category,
                    Random.Shared.Next(500, 5000),
                    DateTime.UtcNow);
                preset.LastResult = result;
                ComparisonResults.Add(result);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed \"{preset.Name}\": {ex.Message}";
            }
        }

        StatusMessage = $"All presets completed. {ComparisonResults.Count} results.";
        IsRunning = false;
    }

    [RelayCommand]
    private void ClearResults()
    {
        ComparisonResults.Clear();
        IsComparisonVisible = false;
        StatusMessage = "Results cleared.";
    }
}
