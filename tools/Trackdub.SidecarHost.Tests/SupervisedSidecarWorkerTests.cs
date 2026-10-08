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
    public void EffectiveTimeout_LoadDefaultsToLoadTimeout_OthersToResponseTimeout()
    {
        Assert.Equal(SupervisedSidecarWorker.LoadTimeout,
            SupervisedSidecarWorker.EffectiveTimeout("load", null));
        Assert.Equal(SupervisedSidecarWorker.LoadTimeout,
            SupervisedSidecarWorker.EffectiveTimeout("load", SupervisedSidecarWorker.LoadTimeout));
        Assert.Equal(TimeSpan.FromSeconds(30),
            SupervisedSidecarWorker.EffectiveTimeout("load", TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(120),
            SupervisedSidecarWorker.EffectiveTimeout("infer", null));
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
    public async Task Poison_KillsTheWorkerProcess_ParityWithSupervisorRust()
    {
        // supervisor.rs poison() kills the child; the C# mirror must too, so a
        // timed-out or out-of-order worker cannot linger until dispose.
        var poisonedWorker = SupervisedSidecarWorker.Spawn("dotnet", [FakeWorkerPublish.DllPath],
            environment: new() { ["FAKE_PROTOCOL_VERSION"] = "2" });
        int poisonedPid = poisonedWorker.ProcessId;

        var ex = Record.Exception(() => poisonedWorker.Request(new SidecarRequest("p1", "health")));
        Assert.NotNull(ex);
        Assert.Contains("protocol version", ex!.Message);

        bool gone = false;
        for (int i = 0; i < 50; i++)
        {
            try
            {
                var procById = Process.GetProcessById(poisonedPid);
                if (procById.HasExited) { gone = true; break; }
            }
            catch (ArgumentException) { gone = true; break; }
            catch (InvalidOperationException) { gone = true; break; }
            await Task.Delay(100);
        }
        Assert.True(gone, $"poisoned worker pid {poisonedPid} still alive after poison");

        await poisonedWorker.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentRequests_ExchangeWithoutInterleaving()
    {
        // One-in-flight, enforced: two concurrent callers must not interleave
        // their write-read exchanges and read each other's responses.
        await using var worker = SupervisedSidecarWorker.Spawn("dotnet", [FakeWorkerPublish.DllPath]);

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            string id = $"c{i}";
            SidecarResponse resp = worker.Request(new SidecarRequest(id, "health"));
            return (id, resp);
        })).ToArray();
        var results = await Task.WhenAll(tasks);

        foreach (var (id, resp) in results)
        {
            Assert.Equal(id, resp.Id);
            Assert.Equal("alive", resp.Status);
        }
    }

    [Fact]
    public void Vocabulary_RejectsUnknownStatusAndReason()
    {
        var badStatus = new SidecarResponse("x1", "spectacular", ProtocolVersion: 1);
        var ex = Record.Exception(() => ProtocolVocabulary.Validate(badStatus));
        Assert.NotNull(ex);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("closed status set", ex!.Message);

        var badReason = new SidecarResponse("x2", "error", Reason: "made-up-reason", ProtocolVersion: 1);
        var ex2 = Record.Exception(() => ProtocolVocabulary.Validate(badReason));
        Assert.NotNull(ex2);
        Assert.IsType<InvalidOperationException>(ex2);
        Assert.Contains("closed reason set", ex2!.Message);

        // Every in-set value validates.
        foreach (string status in ProtocolVocabulary.Statuses)
        {
            ProtocolVocabulary.Validate(new SidecarResponse("ok1", status, ProtocolVersion: 1));
        }
        foreach (string reason in ProtocolVocabulary.Reasons)
        {
            ProtocolVocabulary.Validate(new SidecarResponse("ok2", "error", Reason: reason, ProtocolVersion: 1));
        }
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
/// Publishes the fake worker fresh on every test-class-run and returns the DLL
/// path. Tests spawn it via program="dotnet", args=[dllPath].
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

        // Delete-before-publish: guarantee a fresh binary on every publish so a
        // stale FakeWorker from an earlier branch/worktree/edit can never make
        // tests pass or fail for the wrong reason. If deletion fails (another
        // concurrent run holds a handle), proceed anyway — dotnet publish
        // overwrites in place.
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // concurrent run holds a handle: publish overwrites anyway
        }
        catch (UnauthorizedAccessException)
        {
            // same tolerance as above
        }

        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("dotnet", $"publish {Quote(project)} -c Debug -o {Quote(dir)}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start dotnet publish");

        // Drain both pipes concurrently: reading stdout to EOF before stderr can
        // deadlock when the child fills the stderr buffer while we wait. The
        // 120s budget applies to the WHOLE publish (drain + exit), so a hung
        // dotnet publish fails here instead of blocking the test run forever.
        using var publishCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(publishCts.Token);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(publishCts.Token);
        string output;
        try
        {
            output = stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            KillBestEffort(proc);
            throw new InvalidOperationException("dotnet publish timed out after 120s (no output)");
        }

        if (!proc.WaitForExit(5_000))
        {
            KillBestEffort(proc);
            throw new InvalidOperationException($"dotnet publish timed out after 120s: {output}");
        }
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed ({proc.ExitCode}): {output}");
        }

        return dll;

        static void KillBestEffort(Process proc)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // best-effort: the caller fails below with the collected output
            }
            catch (InvalidOperationException)
            {
                // process already exited — nothing to kill
            }
        }

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
