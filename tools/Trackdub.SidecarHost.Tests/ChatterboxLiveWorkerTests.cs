using System.Diagnostics;
using Trackdub.SidecarHost;
using Xunit;

namespace Trackdub.SidecarHost.Tests;

public sealed class ChatterboxLiveWorkerTests
{
    /// <summary>Absolute path to workers/chatterbox, resolved from the repo root
    /// (the test host's CWD is the bin dir, so relative paths are unusable).</summary>
    internal static string WorkerDir { get; } = Path.Join(FindRepoRoot(), "workers", "chatterbox");

    internal static string ModelSnapshotDir =>
        Environment.GetEnvironmentVariable("TRACKDUB_CHATTERBOX_SNAPSHOT")
            ?? Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Trackdub", "model-cache", "ResembleAI", "chatterbox");

    [SidecarWorkerFact]
    public async Task Health_Load_Infer_LiveChatterboxWorker_ProducesRealAudio()
    {
        // Resolve python from the worker's locked env via uv (ADR-0016 rule 2:
        // pinned interpreter, never system Python). Spawn cwd is the worker dir.
        Assert.True(Directory.Exists(ModelSnapshotDir),
            $"Chatterbox snapshot not staged at {ModelSnapshotDir}. Stage it first (see README in workers/chatterbox).");
        Assert.True(File.Exists(Path.Join(WorkerDir, "uv.lock")), "worker uv.lock missing — run uv sync");

        await using var worker = SupervisedSidecarWorker.Spawn(
            "uv", ["run", "--extra", "model", "python", "worker.py"],
            workingDirectory: WorkerDir);

        SidecarResponse health = worker.Request(new SidecarRequest("h1", "health"));
        Assert.Equal("alive", health.Status);
        Assert.Equal(1, health.ProtocolVersion);
        Assert.False(health.ModelLoaded, "fresh worker must not claim a loaded model");

        SidecarResponse load = worker.Request(new SidecarRequest("l1", "load",
            Plan: new SidecarLoadPlan(ModelSnapshotDir, ["CUDA", "CPU"], RequirePreferred: false)),
            timeout: SupervisedSidecarWorker.LoadTimeout);
        Assert.Equal("loaded", load.Status);
        Assert.Contains(load.ActiveProvider, new[] { "cuda", "cpu" });

        var sw = Stopwatch.StartNew();
        SidecarResponse infer = worker.Request(new SidecarRequest("i1", "infer",
            Inputs: new() { ["text"] = SidecarTensor.Utf8("Trackdub sidecar integration proof: the supervised worker speaks protocol one.") }));
        sw.Stop();

        Assert.Equal("ok", infer.Status);
        Assert.Equal(24000, infer.SampleRate);
        // Real audio: >0.5s of non-silent 16-bit PCM (24k samples/s).
        Assert.NotNull(infer.Outputs);
        Assert.True(infer.Outputs!["audio"].Shape[0] > 12000, $"expected more than 0.5s of audio, got {infer.Outputs!["audio"].Shape[0]} samples");
        Assert.True(sw.Elapsed.TotalSeconds > 0, "infer wall time must be positive");
        await Task.CompletedTask;
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Directory.Packages.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found (Directory.Packages.props missing)");
    }
}
