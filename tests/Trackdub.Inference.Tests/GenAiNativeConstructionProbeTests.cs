using System.Diagnostics;

namespace Trackdub.Inference.Tests;

public sealed class GenAiNativeConstructionProbeTests
{
    [GenAiNativeProbeFact]
    public async Task ConstructsRealCpuModelInAChildProcess()
    {
        string executable = Environment.GetEnvironmentVariable("TRACKDUB_GENAI_PROBE_EXE")!;
        string model = Environment.GetEnvironmentVariable("TRACKDUB_GENAI_PROBE_MODEL")!;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(model);
        using var process = new Process { StartInfo = info };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("GenAI construction child exceeded 120 seconds.");
        }
        string output = await stdout;
        string error = await stderr;
        Assert.True(process.ExitCode == 0, $"Child failed/crashed ({process.ExitCode}).\n{output}\n{error}");
        Assert.Contains("GenAI CPU model construction succeeded.", output);
    }
}

internal sealed class GenAiNativeProbeFactAttribute : FactAttribute
{
    public GenAiNativeProbeFactAttribute()
    {
        if (!OperatingSystem.IsWindows() ||
            !File.Exists(Environment.GetEnvironmentVariable("TRACKDUB_GENAI_PROBE_EXE")) ||
            !Directory.Exists(Environment.GetEnvironmentVariable("TRACKDUB_GENAI_PROBE_MODEL")))
            Skip = "Set TRACKDUB_GENAI_PROBE_EXE and TRACKDUB_GENAI_PROBE_MODEL to run the real native child probe.";
    }
}
