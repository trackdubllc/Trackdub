using Trackdub.Domain;
using Trackdub.Inference.Onnx;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class EngineCacheProbeTests
{
    [Fact]
    public void ClassifyOutcome_not_applicable_for_cpu()
    {
        var before = new EngineCacheProbe.Snapshot(null, false, 0, 0);
        var after = new EngineCacheProbe.Snapshot(null, false, 0, 0);

        Assert.Equal(
            "not-applicable",
            EngineCacheProbe.ClassifyOutcome(before, after, BenchmarkProviderPreference.Cpu));
    }

    [Fact]
    public void ClassifyOutcome_wrote_when_cache_grows()
    {
        var before = new EngineCacheProbe.Snapshot("x", true, 0, 0);
        var after = new EngineCacheProbe.Snapshot("x", true, 2, 1024);

        Assert.Equal(
            "wrote",
            EngineCacheProbe.ClassifyOutcome(before, after, BenchmarkProviderPreference.TensorRtRtx));
    }

    [Fact]
    public void ClassifyOutcome_hit_when_cache_nonempty_and_unchanged()
    {
        var before = new EngineCacheProbe.Snapshot("x", true, 3, 4096);
        var after = new EngineCacheProbe.Snapshot("x", true, 3, 4096);

        Assert.Equal(
            "hit",
            EngineCacheProbe.ClassifyOutcome(before, after, BenchmarkProviderPreference.TensorRtRtx));
    }

    [Fact]
    public void ClassifyOutcome_empty_when_no_cache_files()
    {
        var before = new EngineCacheProbe.Snapshot("x", true, 0, 0);
        var after = new EngineCacheProbe.Snapshot("x", true, 0, 0);

        Assert.Equal(
            "empty",
            EngineCacheProbe.ClassifyOutcome(before, after, BenchmarkProviderPreference.TensorRtRtx));
    }

    [Fact]
    public void ClassifyDominantPhase_maps_cache_outcome_evidence()
    {
        Assert.Equal("engine-compile", EngineCacheProbe.ClassifyDominantPhase("wrote", BenchmarkProviderPreference.TensorRtRtx));
        Assert.Equal("model-deserialize", EngineCacheProbe.ClassifyDominantPhase("hit", BenchmarkProviderPreference.TensorRtRtx));
        Assert.Equal("model-deserialize", EngineCacheProbe.ClassifyDominantPhase("not-applicable", BenchmarkProviderPreference.Cpu));
        Assert.Equal("unknown", EngineCacheProbe.ClassifyDominantPhase("empty", BenchmarkProviderPreference.TensorRtRtx));
    }

    [Fact]
    public void FormatNote_states_outcome_and_dominant_phase()
    {
        var before = new EngineCacheProbe.Snapshot("x", true, 0, 0);
        var after = new EngineCacheProbe.Snapshot("x", true, 1, 2048);

        string note = EngineCacheProbe.FormatNote(
            26927.75,
            EngineCacheProbe.ClassifyOutcome(before, after, BenchmarkProviderPreference.TensorRtRtx),
            before,
            after,
            EngineCacheProbe.ClassifyDominantPhase("wrote", BenchmarkProviderPreference.TensorRtRtx));

        Assert.Contains("total=26927.8", note, StringComparison.Ordinal);
        Assert.Contains("engine cache=wrote", note, StringComparison.Ordinal);
        Assert.Contains("dominant=engine-compile", note, StringComparison.Ordinal);
        Assert.Contains("+1 file", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_reads_environment_overrides_without_throwing()
    {
        string? previous = Environment.GetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT");
        string temp = Path.Join(Path.GetTempPath(), $"engine-cache-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            File.WriteAllBytes(Path.Join(temp, "engine.bin"), new byte[128]);
            Environment.SetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT", temp);

            EngineCacheProbe.Snapshot snapshot = EngineCacheProbe.Capture();

            Assert.True(snapshot.DirectoryExists);
            Assert.Equal(1, snapshot.FileCount);
            Assert.Equal(128, snapshot.TotalBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT", previous);
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException ex) { Console.Error.WriteLine($"Cleanup failed for '{temp}': {ex.Message}"); }
        }
    }
}
