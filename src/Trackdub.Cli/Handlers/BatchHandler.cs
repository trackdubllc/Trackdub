using System.Text.Json;

using Trackdub.Contracts.Pipeline;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Executes batch pipeline processing across multiple media files and emits
/// a structured <see cref="BatchReport"/> as JSON to standard output.
/// </summary>
internal static class BatchHandler
{
    public static async Task<int> ExecuteAsync(
        TrackdubSessionFactory factory,
        IReadOnlyList<string> mediaFiles,
        DubbingSessionOptions templateOptions,
        BatchOptions batchOptions,
        string? presetName,
        string progressFormat,
        TextWriter output,
        CancellationToken ct)
    {
        if (presetName is not null)
        {
            await Console.Error.WriteLineAsync(
                $"Using preset '{presetName}': target-language={templateOptions.TargetLanguageCode}" +
                FormatOptional("source-language", templateOptions.SourceLanguageCode) +
                FormatOptional("export-format", templateOptions.ExportFormat) +
                $", files={mediaFiles.Count}")
                .ConfigureAwait(false);
        }

        return await CliProgressRunner.ExecuteAsync(
            progressFormat,
            async (progress, cancellationToken) =>
            {
                var engine = new TrackdubDubbingEngine(factory);
                var processor = new BatchProcessor(engine);

                BatchReport report = await processor.ExecuteAsync(
                    mediaFiles,
                    templateOptions,
                    batchOptions,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                string json = JsonSerializer.Serialize(report, CliJsonOptions.Default);
                await output.WriteLineAsync(json).ConfigureAwait(false);

                return report.FailedCount > 0
                    ? Program.ExitPipelineFailure
                    : Program.ExitSuccess;
            },
            ct).ConfigureAwait(false);
    }

    private static string FormatOptional(string key, string? value) =>
        value is not null ? $", {key}={value}" : string.Empty;
}
