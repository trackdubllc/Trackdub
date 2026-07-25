using System.Diagnostics;

namespace DubBench.Services;

/// <summary>
/// Invokes Olive (ONNX Runtime optimization tool) as a subprocess
/// to quantize and optimize ONNX models before benchmarking.
/// </summary>
public sealed class OliveOptimizationService : IOliveOptimizationService
{
    private bool? _availability;

    public bool IsOliveAvailable => _availability ?? false;

    public async Task<bool> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "olive",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            await DrainAndWaitAsync(process, cancellationToken).ConfigureAwait(false);
            _availability = process.ExitCode == 0;
            return _availability.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _availability = false;
            return false;
        }
    }

    public async Task<string?> OptimizeAsync(
        string modelPath,
        string outputDir,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsOliveAvailable && !await ProbeAvailabilityAsync(cancellationToken).ConfigureAwait(false))
            return null;

        Directory.CreateDirectory(outputDir);

        var args = $"--input_model \"{modelPath}\" --output_dir \"{outputDir}\"";
        if (!string.IsNullOrEmpty(provider))
            args += $" --provider {provider}";

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "olive",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            await DrainAndWaitAsync(process, cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
                return null;

            var optimizedFiles = Directory.GetFiles(outputDir, "*.ort", SearchOption.AllDirectories);
            if (optimizedFiles.Length > 0)
                return optimizedFiles[0];

            var onnxFiles = Directory.GetFiles(outputDir, "*.onnx", SearchOption.AllDirectories);
            if (onnxFiles.Length > 0)
                return onnxFiles[0];

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drains stdout/stderr concurrently while waiting for exit.
    /// Kills the process on cancellation to prevent orphaned children.
    /// </summary>
    private static async Task DrainAndWaitAsync(Process process, CancellationToken cancellationToken)
    {
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(cancellationToken),
                stdoutTask,
                stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
