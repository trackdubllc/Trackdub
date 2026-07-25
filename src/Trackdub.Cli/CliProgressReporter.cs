using System.Text.Json;
using System.Text.Json.Serialization;

using Trackdub.Contracts.Pipeline;

namespace Trackdub.Cli;

/// <summary>
/// Reports pipeline progress events to stderr in either JSON or human-readable text format.
/// </summary>
internal sealed class CliProgressReporter : IProgress<PipelineProgressEvent>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _format;

    /// <summary>
    /// Initializes a new instance of <see cref="CliProgressReporter"/>.
    /// </summary>
    /// <param name="format">The output format: "json" for single-line JSON or "text" for human-readable lines.</param>
    public CliProgressReporter(string format)
    {
        _format = format;
    }

    public void Report(PipelineProgressEvent value)
    {
        if (_format == "json")
        {
            ReportJson(value);
        }
        else
        {
            ReportText(value);
        }
    }

    private static void ReportJson(PipelineProgressEvent value)
    {
        string json = JsonSerializer.Serialize(value, s_jsonOptions);
        Console.Error.WriteLine(json);
    }

    private static void ReportText(PipelineProgressEvent value)
    {
        string line = value.EventKind switch
        {
            PipelineProgressEventKind.Started => $"[Started] {value.StageName}",
            PipelineProgressEventKind.Progress => $"[Progress] {FormatProgress(value)}",
            PipelineProgressEventKind.Completed => $"[Completed] {value.StageName} ({value.ElapsedDuration.TotalSeconds:F1}s)",
            PipelineProgressEventKind.Failed => $"[Failed] {value.StageName}: {value.Message}",
            PipelineProgressEventKind.Skipped => $"[Skipped] {value.StageName}: {value.Message}",
            _ => $"[{value.EventKind}] {value.StageName}",
        };

        Console.Error.WriteLine(line);
    }

    private static string FormatProgress(PipelineProgressEvent value)
    {
        string suffix = value.PercentComplete is double percentComplete
            ? $"{percentComplete:0}%"
            : value.Phase ?? "running";
        return string.IsNullOrWhiteSpace(value.Message)
            ? $"{value.StageName}: {suffix}"
            : $"{value.StageName}: {suffix} - {value.Message}";
    }
}
