using System.Diagnostics;
using Xunit;

namespace Trackdub.SidecarHost.Tests;

public sealed class SupervisorGateTests
{
    [SidecarWorkerFact]
    public async Task RustSupervisor_HealthGate_AcceptsRealWorker()
    {
        // The shipped supervision path: the Rust binary gates the real Python
        // worker through the protocol health check and prints its verdict JSON.
        string repoRoot = FindRepoRoot();
        // cargo names the binary trackdub-supervisor on Linux/macOS, with .exe on Windows.
        string supervisorExe = Path.Join(repoRoot, "workers", "supervisor", "target", "release",
            OperatingSystem.IsWindows() ? "trackdub-supervisor.exe" : "trackdub-supervisor");
        Assert.True(File.Exists(supervisorExe),
            $"supervisor binary missing at {supervisorExe} — run: cargo build --release --manifest-path workers/supervisor/Cargo.toml");

        var psi = new ProcessStartInfo(supervisorExe)
        {
            WorkingDirectory = Path.Join(repoRoot, "workers", "chatterbox"),
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // One token per ArgumentList entry: --check uv run --extra model python worker.py
        foreach (string token in new[] { "--check", "uv", "run", "--extra", "model", "python", "worker.py" })
        {
            psi.ArgumentList.Add(token);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start supervisor");
        // Bound the stdout read too: it completes only when the supervisor closes
        // stdout (normally at exit), so without a token a hung supervisor would
        // outlive the WaitForExit guard and hang the test indefinitely.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(200));
        string output;
        try
        {
            output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                // best-effort: the test fails below regardless
            }
            throw new InvalidOperationException("supervisor --check produced no verdict within 200s (stdout read timed out)");
        }
        Assert.True(proc.WaitForExit(200_000), "supervisor --check timed out");
        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("\"status\":\"accepted\"", output);
        Assert.Contains("trackdub-supervisor/0.1.0", output);

        static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Directory.Packages.props")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("repo root not found (Directory.Packages.props missing)");
        }
    }
}
