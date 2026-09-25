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
            EngineCacheProbe.ClassifyOutcome(before, after, isEngineCacheRelevantProvider: false));
    }

    [Fact]
    public void ClassifyOutcome_wrote_when_cache_grows()
    {
        var before = new EngineCacheProbe.Snapshot("x", true, 0, 0);
        var after = new EngineCacheProbe.Snapshot("x", true, 2, 1024);

        Assert.Equal(
            "wrote",
            EngineCacheProbe.ClassifyOutcome(before, after, isEngineCacheRelevantProvider: true));
    }

    [Fact]
    public void ClassifyOutcome_unknown_when_cache_nonempty_and_unchanged()
    {
        // Cannot confirm pre-existing entries belong to this model/provider without parsing
        // engine-cache filenames, so an unchanged-but-nonempty cache is "unknown", not "hit".
        var before = new EngineCacheProbe.Snapshot("x", true, 3, 4096);
        var after = new EngineCacheProbe.Snapshot("x", true, 3, 4096);

        Assert.Equal(
            "unknown",
            EngineCacheProbe.ClassifyOutcome(before, after, isEngineCacheRelevantProvider: true));
    }

    [Fact]
    public void ClassifyOutcome_empty_when_no_cache_files()
    {
        var before = new EngineCacheProbe.Snapshot("x", true, 0, 0);
        var after = new EngineCacheProbe.Snapshot("x", true, 0, 0);

        Assert.Equal(
            "empty",
            EngineCacheProbe.ClassifyOutcome(before, after, isEngineCacheRelevantProvider: true));
    }

    [Fact]
    public void IsEngineCacheRelevantProvider_true_only_for_tensorrt_family()
    {
        Assert.True(EngineCacheProbe.IsEngineCacheRelevantProvider(BenchmarkProviderPreference.TensorRtRtx));
        Assert.True(EngineCacheProbe.IsEngineCacheRelevantProvider(BenchmarkProviderPreference.TensorRt));
        Assert.True(EngineCacheProbe.IsEngineCacheRelevantProvider(BenchmarkProviderPreference.Migraphx));
        Assert.False(EngineCacheProbe.IsEngineCacheRelevantProvider(BenchmarkProviderPreference.Cpu));
        Assert.False(EngineCacheProbe.IsEngineCacheRelevantProvider(BenchmarkProviderPreference.Dml));
    }

    [Fact]
    public void ClassifyDominantPhase_only_claims_compile_on_actual_cache_growth()
    {
        // "wrote" is the only outcome with real evidence (bytes were observed). Every other
        // outcome — including a cache hit — lacks phase timing, so it stays unknown rather
        // than guessed.
        Assert.Equal("engine-compile", EngineCacheProbe.ClassifyDominantPhase("wrote"));
        Assert.Equal("unknown", EngineCacheProbe.ClassifyDominantPhase("unknown"));
        Assert.Equal("unknown", EngineCacheProbe.ClassifyDominantPhase("not-applicable"));
        Assert.Equal("unknown", EngineCacheProbe.ClassifyDominantPhase("empty"));
    }

    [Fact]
    public void FormatNote_states_outcome_and_dominant_phase()
    {
        var before = new EngineCacheProbe.Snapshot("x", true, 0, 0);
        var after = new EngineCacheProbe.Snapshot("x", true, 1, 2048);

        string outcome = EngineCacheProbe.ClassifyOutcome(before, after, isEngineCacheRelevantProvider: true);
        string note = EngineCacheProbe.FormatNote(
            26927.75,
            outcome,
            before,
            after,
            EngineCacheProbe.ClassifyDominantPhase(outcome));

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
