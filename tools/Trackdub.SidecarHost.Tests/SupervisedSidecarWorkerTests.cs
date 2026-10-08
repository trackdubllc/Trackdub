using System.Diagnostics;
using Trackdub.SidecarHost;
using Xunit;

namespace Trackdub.SidecarHost.Tests;

public sealed class SupervisedSidecarWorkerTests
{
    [Fact]
    public async Task Spawn_HealthGate_ReturnsAliveWithProtocolVersion()
    {
        await using var worker = SupervisedSidecarWorker.Spawn("dotnet", [FakeWorkerPublish.DllPath]);
        SidecarResponse health = worker.Request(new SidecarRequest("h1", "health"));
        Assert.Equal("alive", health.Status);
        Assert.Equal(1, health.ProtocolVersion);
    }

    [Fact]
    public async Task LoadThenInfer_CompletesLifecycleThroughFakeWorker()
    {
        await using var worker = SupervisedSidecarWorker.Spawn("dotnet", [FakeWorkerPublish.DllPath]);
        SidecarResponse load = worker.Request(new SidecarRequest("l1", "load",
            Plan: new SidecarLoadPlan("fake", ["CPU"], RequirePreferred: false)));
        Assert.Equal("loaded", load.Status);
        Assert.Equal("cpu", load.ActiveProvider);

        SidecarResponse infer = worker.Request(new SidecarRequest("i1", "infer",
            Inputs: new() { ["text"] = SidecarTensor.Utf8("hello world") }));
        Assert.Equal("ok", infer.Status);
        Assert.Equal(24000, infer.SampleRate);
    }

    [Fact]
    public async Task VersionStampMismatch_PoisonsConnection()
    {
        await using var worker = SupervisedSidecarWorker.Spawn("dotnet", [FakeWorkerPublish.DllPath],
            environment: new() { ["FAKE_PROTOCOL_VERSION"] = "2" });

        var ex = Record.Exception(() => worker.Request(new SidecarRequest("v1", "health")));
        Assert.NotNull(ex);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("protocol version", ex.Message);

        var second = Record.Exception(() => worker.Request(new SidecarRequest("v2", "health")));
        Assert.NotNull(second);
        Assert.IsType<InvalidOperationException>(second);
        Assert.Contains("unusable", second.Message);
    }

    [Fact]
    public async Task Dispose_KillsWorkerProcess()
    {
        var worker = SupervisedSidecarWorker.Spawn("dotnet", [FakeWorkerPublish.DllPath]);
        int pid = worker.ProcessId;
        await worker.DisposeAsync();

        bool gone = false;
        for (int i = 0; i < 20; i++)
        {
            try { Process.GetProcessById(pid); }
            catch (ArgumentException) { gone = true; break; }
            await Task.Delay(100);
        }
        Assert.True(gone);
    }
}

/// <summary>
/// Publishes the fake worker once per test-class-run and returns the DLL path.
/// Tests spawn it via program="dotnet", args=[dllPath].
/// </summary>
internal static class FakeWorkerPublish
{
    public static readonly string DllPath = new Lazy<string>(Publish, LazyThreadSafetyMode.ExecutionAndPublication).Value;

    private static string Publish()
    {
        string projectRoot = FindRepoRoot();
        string project = Path.Join(projectRoot, "tools", "Trackdub.SidecarHost.Tests", "FakeWorker");
        string dir = Path.Join(Path.GetTempPath(), "trackdub-sidecar-tests", "fakeworker");
        string dll = Path.Join(dir, "Trackdub.SidecarFakeWorker.dll");
        if (!File.Exists(dll))
        {
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo("dotnet", $"publish {Quote(project)} -c Debug -o {Quote(dir)}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start dotnet publish");
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000);
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"dotnet publish failed ({proc.ExitCode}): {output}");
            }
        }

        return dll;

        static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Directory.Packages.props")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("repo root not found (Directory.Packages.props missing)");
        }

        static string Quote(string s) => $"\"{s}\"";
    }
}
