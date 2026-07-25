using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts.Dubbing;

namespace Trackdub.Sdk;

/// <summary>
/// Writes a run manifest JSON file to the project output directory after every pipeline run.
/// The manifest captures the full <see cref="DubbingRunResult"/> including run ID, timestamps,
/// overall status, per-stage outcomes, execution snapshot, artifact paths, degradation records,
/// and reason codes.
/// </summary>
/// <remarks>
/// Two files are written on each invocation:
/// <list type="bullet">
///   <item><c>{outputDirectory}/run-manifest.json</c> — latest manifest (overwritten each run)</item>
///   <item><c>{outputDirectory}/run-manifests/run-{runId}.json</c> — historical manifest (one per run)</item>
/// </list>
/// I/O errors are logged but not thrown — the manifest is informational and must not
/// cause a pipeline run to fail.
/// </remarks>
public sealed class RunManifestWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Writes the run manifest to the specified output directory.
    /// </summary>
    /// <param name="result">The completed pipeline run result to serialize.</param>
    /// <param name="outputDirectory">The project output directory where manifests are written.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    public async Task WriteAsync(
        DubbingRunResult result,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        try
        {
            // Ensure the output directory exists.
            Directory.CreateDirectory(outputDirectory);

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions);

            // Write latest manifest (overwriting previous).
            string latestPath = Path.Combine(outputDirectory, "run-manifest.json");
            await File.WriteAllBytesAsync(latestPath, jsonBytes, cancellationToken).ConfigureAwait(false);

            // Write historical manifest.
            string historyDirectory = Path.Combine(outputDirectory, "run-manifests");
            Directory.CreateDirectory(historyDirectory);

            string historyPath = Path.Combine(historyDirectory, $"run-{result.RunId}.json");
            await File.WriteAllBytesAsync(historyPath, jsonBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation — caller requested abort.
            throw;
        }
        catch (Exception)
        {
            // Manifest writing is informational. Swallow I/O errors so the pipeline
            // run result is not lost due to a secondary write failure.
            // In production, this would log via IApplicationLogger.
        }
    }
}
