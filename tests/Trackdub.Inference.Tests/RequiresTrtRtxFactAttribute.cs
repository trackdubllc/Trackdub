using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Skips when the TensorRT RTX EP ABI plugin is unavailable (missing bundle, no NVIDIA GPU, CI without env gate, etc.).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresTrtRtxFactAttribute : FactAttribute
{
    public RequiresTrtRtxFactAttribute()
    {
        Skip = ResolveTrtRtxSkip();
    }

    internal static string? ResolveTrtRtxSkip()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return "TensorRT RTX smoke requires Windows or Linux.";
        }

        string? smokeGate = Environment.GetEnvironmentVariable("TRACKDUB_TRT_RTX_SMOKE");
        if (!string.Equals(smokeGate, "1", StringComparison.Ordinal) &&
            !string.Equals(smokeGate, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "Set TRACKDUB_TRT_RTX_SMOKE=1 to run TensorRT RTX integration smoke on this machine.";
        }

        string? pluginDirectory = Environment.GetEnvironmentVariable(
            TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory))
        {
            return $"TensorRT RTX plugin directory missing. Set {TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable} after running tools/dev/Fetch-TrtRtxEp.ps1.";
        }

        string[] missingFiles = TensorRtRtxProviderConstants.RequiredPluginFileNames
            .Where(fileName => !File.Exists(Path.Combine(pluginDirectory, fileName)))
            .ToArray();
        if (missingFiles.Length > 0)
        {
            return $"TensorRT RTX plugin directory is incomplete (missing: {string.Join(", ", missingFiles)}).";
        }

        var probe = new TensorRtRtxReadinessProbe();
        TensorRtRtxReadinessReport report = probe
            .ProbeAsync(allowProviderDownloads: false, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (report.IsReady)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(report.Detail)
            ? $"TensorRT RTX EP ABI plugin is not ready (blocker={report.Blocker})."
            : report.Detail;
    }
}
