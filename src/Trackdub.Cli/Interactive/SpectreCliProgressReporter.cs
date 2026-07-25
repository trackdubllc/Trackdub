using Spectre.Console;

using Trackdub.Contracts.Pipeline;

namespace Trackdub.Cli.Interactive;

/// <summary>
/// Maps pipeline progress events to Spectre progress tasks for interactive TTY runs.
/// </summary>
internal sealed class SpectreCliProgressReporter : IProgress<PipelineProgressEvent>
{
    private readonly ProgressContext _context;
    private readonly Dictionary<string, ProgressTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    internal SpectreCliProgressReporter(ProgressContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void Report(PipelineProgressEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_sync)
        {
            string key = value.StageKey ?? value.StageName;
            ProgressTask task = GetOrCreateTask(key, value.StageName);

            switch (value.EventKind)
            {
                case PipelineProgressEventKind.Started:
                    task.Description = BuildDescription(value, "Starting");
                    task.Value = 0;
                    break;

                case PipelineProgressEventKind.Progress:
                    task.Description = BuildDescription(value, value.Phase ?? "Running");
                    if (value.PercentComplete is double percentComplete)
                    {
                        task.MaxValue = 100;
                        task.Value = percentComplete;
                    }

                    break;

                case PipelineProgressEventKind.Completed:
                    task.Description = BuildDescription(
                        value,
                        $"Done ({value.ElapsedDuration.TotalSeconds:F1}s)");
                    task.Value = 100;
                    task.StopTask();
                    break;

                case PipelineProgressEventKind.Failed:
                    task.Description =
                        $"[red]{Markup.Escape(value.StageName)} failed[/]: {Markup.Escape(value.Message ?? "error")}";
                    task.StopTask();
                    break;

                case PipelineProgressEventKind.Skipped:
                    task.Description =
                        $"[grey]{Markup.Escape(value.StageName)} skipped[/]: {Markup.Escape(value.Message ?? string.Empty)}";
                    task.Value = 100;
                    task.StopTask();
                    break;

                default:
                    task.Description = BuildDescription(value, value.EventKind.ToString());
                    break;
            }
        }
    }

    private ProgressTask GetOrCreateTask(string key, string stageName)
    {
        if (_tasks.TryGetValue(key, out ProgressTask? existing))
        {
            return existing;
        }

        ProgressTask task = _context.AddTask(Markup.Escape(stageName), maxValue: 100);
        _tasks[key] = task;
        return task;
    }

    private static string BuildDescription(PipelineProgressEvent value, string status)
    {
        string description = $"{Markup.Escape(value.StageName)} - {Markup.Escape(status)}";

        if (!string.IsNullOrWhiteSpace(value.Message))
        {
            return $"{description}: {Markup.Escape(value.Message)}";
        }

        if (value.CompletedUnits is int completed && value.TotalUnits is int total)
        {
            return $"{description} ({completed}/{total})";
        }

        if (!string.IsNullOrWhiteSpace(value.CurrentItemLabel))
        {
            return $"{description}: {Markup.Escape(value.CurrentItemLabel)}";
        }

        return description;
    }
}
