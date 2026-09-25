using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests.Settings;

public sealed class FileSmokeVerdictStoreTests
{
    private const string ModelSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void RecordVerified_persists_across_store_instances()
    {
        string path = NewStorePath();
        try
        {
            SmokeVerdictKey key = CreateKey(ModelSha, ExecutionProviderKind.TensorRTRtx, "560.35.03");

            var writer = new FileSmokeVerdictStore(path);
            writer.RecordVerified(key);

            var reader = new FileSmokeVerdictStore(path);
            Assert.True(reader.IsVerified(key));
        }
        finally
        {
            DeleteStore(path);
        }
    }

    [Fact]
    public void IsVerified_false_when_no_verdict_recorded()
    {
        string path = NewStorePath();
        try
        {
            var store = new FileSmokeVerdictStore(path);
            Assert.False(store.IsVerified(CreateKey(ModelSha, ExecutionProviderKind.TensorRTRtx, "560.35.03")));
        }
        finally
        {
            DeleteStore(path);
        }
    }

    [Fact]
    public void IsVerified_false_when_any_key_component_differs()
    {
        string path = NewStorePath();
        try
        {
            SmokeVerdictKey recorded = CreateKey(ModelSha, ExecutionProviderKind.TensorRTRtx, "560.35.03");
            var store = new FileSmokeVerdictStore(path);
            store.RecordVerified(recorded);

            // Same everything → hit.
            Assert.True(store.IsVerified(recorded));

            // Model sha256 change (re-download) → miss.
            Assert.False(store.IsVerified(recorded with
            {
                ModelSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            }));

            // EP change → miss.
            Assert.False(store.IsVerified(recorded with { ExecutionProvider = ExecutionProviderKind.DirectMl }));

            // GPU arch change → miss.
            Assert.False(store.IsVerified(recorded with { GpuArchitecture = NvidiaGpuArchitectureBucket.Blackwell }));

            // Driver version change → miss.
            Assert.False(store.IsVerified(recorded with { DriverVersion = "570.86.16" }));

            // TRT RTX EP version change → miss.
            Assert.False(store.IsVerified(recorded with { TrtRtxEpVersion = "0.4.0" }));
        }
        finally
        {
            DeleteStore(path);
        }
    }

    [Fact]
    public void RecordVerified_drops_entries_from_other_environments()
    {
        string path = NewStorePath();
        try
        {
            SmokeVerdictKey oldDriver = CreateKey(ModelSha, ExecutionProviderKind.TensorRTRtx, "560.35.03");
            SmokeVerdictKey newDriver = CreateKey(
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                ExecutionProviderKind.DirectMl,
                "570.86.16");

            var store = new FileSmokeVerdictStore(path);
            store.RecordVerified(oldDriver);
            store.RecordVerified(newDriver);

            // The new environment fingerprint owns the store: the stale driver's verdict is gone.
            Assert.False(store.IsVerified(oldDriver));
            Assert.True(store.IsVerified(newDriver));

            // And the same is true after a cold load (process restart).
            var reloaded = new FileSmokeVerdictStore(path);
            Assert.False(reloaded.IsVerified(oldDriver));
            Assert.True(reloaded.IsVerified(newDriver));
        }
        finally
        {
            DeleteStore(path);
        }
    }

    [Fact]
    public void Trt_rtx_runtime_lineage_change_misses_and_prunes_even_when_ep_abi_version_is_unchanged()
    {
        string path = NewStorePath();
        try
        {
            SmokeVerdictKey runtime15 = CreateKey(ModelSha, ExecutionProviderKind.TensorRTRtx, "560.35.03")
                with
            { TrtRtxEpVersion = "0.4.2+trt-rtx-1.5.0" };
            SmokeVerdictKey runtime16 = runtime15 with { TrtRtxEpVersion = "0.4.2+trt-rtx-1.6.1" };

            var store = new FileSmokeVerdictStore(path);
            store.RecordVerified(runtime15);

            // (a) runtime bump alone → miss.
            Assert.False(store.IsVerified(runtime16));

            // (c) recording under the new runtime drops the old environment's entries, also after reload.
            store.RecordVerified(runtime16);
            Assert.False(store.IsVerified(runtime15));
            Assert.False(new FileSmokeVerdictStore(path).IsVerified(runtime15));
            Assert.True(new FileSmokeVerdictStore(path).IsVerified(runtime16));
        }
        finally
        {
            DeleteStore(path);
        }
    }

    [Fact]
    public void RecordVerified_keeps_multiple_models_and_providers_within_one_environment()
    {
        string path = NewStorePath();
        try
        {
            SmokeVerdictKey modelA = CreateKey(ModelSha, ExecutionProviderKind.TensorRTRtx, "560.35.03");
            SmokeVerdictKey modelB = CreateKey(
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                ExecutionProviderKind.TensorRTRtx,
                "560.35.03");
            SmokeVerdictKey modelADml = CreateKey(ModelSha, ExecutionProviderKind.DirectMl, "560.35.03");

            var store = new FileSmokeVerdictStore(path);
            store.RecordVerified(modelA);
            store.RecordVerified(modelB);
            store.RecordVerified(modelADml);

            Assert.True(store.IsVerified(modelA));
            Assert.True(store.IsVerified(modelB));
            Assert.True(store.IsVerified(modelADml));
        }
        finally
        {
            DeleteStore(path);
        }
    }

    [Fact]
    public void Clear_removes_verdicts_and_file()
    {
        string path = NewStorePath();
        try
        {
            SmokeVerdictKey key = CreateKey(ModelSha, ExecutionProviderKind.TensorRTRtx, "560.35.03");
            var store = new FileSmokeVerdictStore(path);
            store.RecordVerified(key);
            Assert.True(File.Exists(path));

            store.Clear();

            Assert.False(store.IsVerified(key));
            Assert.False(File.Exists(path));
            Assert.False(new FileSmokeVerdictStore(path).IsVerified(key));
        }
        finally
        {
            DeleteStore(path);
        }
    }

    [Fact]
    public void Corrupt_store_file_is_treated_as_empty()
    {
        string path = NewStorePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not valid json");
            var store = new FileSmokeVerdictStore(path);
            Assert.False(store.IsVerified(CreateKey(ModelSha, ExecutionProviderKind.TensorRTRtx, "560.35.03")));
        }
        finally
        {
            DeleteStore(path);
        }
    }

    private static SmokeVerdictKey CreateKey(
        string modelSha256,
        ExecutionProviderKind provider,
        string driverVersion) =>
        new(
            modelSha256,
            provider,
            NvidiaGpuArchitectureBucket.Ada,
            driverVersion,
            TrtRtxEpVersion: "0.3.0");

    private static string NewStorePath() =>
        Path.Combine(Path.GetTempPath(), $"trackdub-smoke-verdicts-{Guid.NewGuid():N}", "smoke-verdicts.json");

    private static void DeleteStore(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Cleanup failed for '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Cleanup failed for '{path}': {ex.Message}");
        }
    }
}
