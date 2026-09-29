using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Domain;
using Trackdub.Infrastructure.Components.NvidiaAfx;
using Trackdub.Infrastructure.Runtime.TrtRtxEp;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

// TEMPORARY measurement probe for the PR #305 latency question. Not intended to be committed.
//
// Each shape reproduces the exact read+deserialize expression the change touched, twice: once with
// the pre-change reflection options (mode=reflection) and once with the generated metadata
// (mode=generated). File I/O and the surrounding guards are identical in both modes, so the delta
// is the metadata cost the change removes. Loader validation/post-processing is excluded from both
// sides because the change did not touch it.
//
// One shape per process is essential: the first touch of InfrastructureSerializationContext builds
// metadata for every type it declares, so a second shape measured in the same process would no
// longer be measuring a first-call cost.
//
//   TRACKDUB_JSON_PROBE_MODE=reflection|generated  (default generated)
//   TRACKDUB_JSON_PROBE_OUT=<jsonl path>           (default %TEMP%/trackdub-json-latency.jsonl)
public sealed class JsonLatencyProbe
{
    private const int SteadyIterations = 200;
    private const int ReadIterations = 50;

    // Pre-change options. The AFX loader constructed its options inline on every call, so its
    // replica does too; the other stores cached a static instance, so theirs is initialized on
    // first call — inside the timed cold region, exactly like the old static field.
    private static readonly JsonSerializerOptions TrtReflectionOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions StorageReflectionOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SmokeReflectionOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions ModelCacheReflectionOptions = new() { WriteIndented = true };

    [Fact]
    public void Probe_afx() => Run(AfxShape());

    [Fact]
    public void Probe_trtrtx() => Run(TrtShape());

    [Fact]
    public void Probe_storageconfig() => Run(StorageShape());

    [Fact]
    public void Probe_smokeverdict() => Run(SmokeShape());

    [Fact]
    public void Probe_modelcache() => Run(ModelCacheShape());

    [Fact]
    public void Probe_startup_all_stores() => RunAllStores();

    [Fact]
    public void Probe_io_read_baseline() => RunIoBaseline();

    // Diagnostic: builds both generated contexts before the timed region, so the number separates
    // "build the metadata" from "deserialize with it" (context init is otherwise inside the timed
    // region, and the context declares four types at once).
    [Fact]
    public void Probe_startup_all_stores_prewarmed_serializers()
    {
        _ = InfrastructureSerializationContext.Default.StorageConfig;
        _ = LocalModelCacheSerializationContext.Default.LocalModelCacheRecordArray;
        RunAllStores(shapeName: "startup.all_prewarmed");
    }

    private sealed record ShapeDef(string Name, Func<object?> Generated, Func<object?> Reflection, Func<object?, string> Bound, string Json, Action? Cleanup = null);

    private static ShapeDef AfxShape()
    {
        string path = ResolveRepoFile("tests", "Trackdub.Infrastructure.Tests", "Fixtures", "NvidiaAfx", "nvidia-afx-runtime.manifest.json");
        return new ShapeDef(
            "afx",
            () => ReadStreaming(path, static stream => JsonSerializer.Deserialize(stream, InfrastructureSerializationContext.Default.NvidiaAfxRuntimeManifest)),
            () => ReadStreaming(path, static stream => JsonSerializer.Deserialize<NvidiaAfxRuntimeManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })),
            static value => Describe((NvidiaAfxRuntimeManifest?)value, static manifest => $"packages={manifest.Packages.Length}"),
            File.ReadAllText(path));
    }

    private static ShapeDef TrtShape()
    {
        string path = ResolveRepoFile("runtime", "trt-rtx-ep.manifest.json");
        return new ShapeDef(
            "trtrtx",
            () => ReadStreaming(path, static stream => JsonSerializer.Deserialize(stream, InfrastructureSerializationContext.Default.TrtRtxEpBundleManifestDto)),
            () => ReadStreaming(path, static stream => JsonSerializer.Deserialize<TrtRtxEpBundleManifestLoader.TrtRtxEpBundleManifestDto>(stream, TrtReflectionOptions)),
            static value => Describe((TrtRtxEpBundleManifestLoader.TrtRtxEpBundleManifestDto?)value, static dto => $"packages={dto.Packages!.Count}, schemaVersion={dto.SchemaVersion}"),
            File.ReadAllText(path));
    }

    private static ShapeDef StorageShape()
    {
        string directory = CreateTempDirectory("trackdub-probe-storage");
        string path = Path.Join(directory, "storage.json");
        const string json = """{"UserDataRoot":"D:\\trackdub","UserCacheRoot":"D:\\trackdub\\cache","SharedAssetRoot":null,"IsPortable":false,"Portable":false}""";
        File.WriteAllText(path, json);
        return new ShapeDef(
            "storageconfig",
            () => ReadStreaming(path, static stream => JsonSerializer.Deserialize(stream, InfrastructureSerializationContext.Default.StorageConfig)),
            () => ReadStreaming(path, static stream => JsonSerializer.Deserialize<TrackdubStoragePathResolver.StorageConfig>(stream, StorageReflectionOptions)),
            static value => Describe((TrackdubStoragePathResolver.StorageConfig?)value, static config => $"hasRoots={config.HasAnyRoot}"),
            json,
            () => DeleteDirectory(directory));
    }

    private static ShapeDef SmokeShape()
    {
        string directory = CreateTempDirectory("trackdub-probe-smoke");
        string path = Path.Join(directory, "smoke-verdicts.json");
        const string json = """{"Version":1,"Verified":{"fp-a":"ok","fp-b":"fail"}}""";
        File.WriteAllText(path, json);
        return new ShapeDef(
            "smokeverdict",
            () => JsonSerializer.Deserialize(File.ReadAllText(path), InfrastructureSerializationContext.Default.SmokeVerdictFilePayload),
            () => JsonSerializer.Deserialize<FileSmokeVerdictStore.SmokeVerdictFilePayload>(File.ReadAllText(path), SmokeReflectionOptions),
            static value => Describe((FileSmokeVerdictStore.SmokeVerdictFilePayload?)value, static payload => $"version={payload.Version}, verified={payload.Verified!.Count}"),
            json,
            () => DeleteDirectory(directory));
    }

    private static ShapeDef ModelCacheShape()
    {
        string directory = CreateTempDirectory("trackdub-probe-modelcache");
        string path = Path.Join(directory, "model-cache-records.json");
        const string json = """
            [
              {
                "ModelId": "model-a",
                "RootPath": "D:\\models\\a",
                "Revision": "rev-1",
                "Sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "CachedAtUtc": "2024-01-01T00:00:00+00:00",
                "IntegrityFailed": false,
                "Variants": [
                  {
                    "Alias": "alias-a",
                    "RootPath": "D:\\models\\a\\variant",
                    "EntryRelativePath": "encoder.onnx",
                    "ComponentRelativePaths": ["encoder.onnx", "decoder.onnx"],
                    "OptimizerId": "ort",
                    "ExecutionProvider": 0,
                    "Precision": "fp32",
                    "CreatedAtUtc": "2024-01-01T00:00:00+00:00",
                    "SourceModelRevision": "rev-1"
                  }
                ]
              },
              {
                "ModelId": "model-b",
                "RootPath": "D:\\models\\b",
                "Revision": "rev-2",
                "Sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "CachedAtUtc": "2024-01-02T00:00:00+00:00",
                "IntegrityFailed": false,
                "Variants": []
              }
            ]
            """;
        File.WriteAllText(path, json);
        return new ShapeDef(
            "modelcache",
            () => LoadModelCacheIndex(path, generated: true),
            () => LoadModelCacheIndex(path, generated: false),
            static value => Describe((LocalModelCacheRecord[]?)value, static records => $"records={records.Length}"),
            json,
            () => DeleteDirectory(directory));
    }

    private static void Run(ShapeDef shape)
    {
        string outputPath = OutputPath();
        bool generated = IsGenerated();
        WarmUpGenericSerializerPath();

        try
        {
            Func<object?> call = generated ? shape.Generated : shape.Reflection;

            long started = Stopwatch.GetTimestamp();
            object? cold = call();
            double coldMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            string bound = shape.Bound(cold);

            double[] samples = new double[SteadyIterations];
            for (int i = 0; i < samples.Length; i++)
            {
                long began = Stopwatch.GetTimestamp();
                object? value = call();
                samples[i] = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
                GC.KeepAlive(value);
            }

            Array.Sort(samples);
            Append(outputPath, shape.Name, coldMicroseconds, samples[samples.Length / 2], shape.Json.Length, bound);
        }
        finally
        {
            shape.Cleanup?.Invoke();
        }
    }

    // Realistic cold start: every startup-path store is read once, in a fixed order, in one process.
    private static void RunAllStores(string shapeName = "startup.all")
    {
        string outputPath = OutputPath();
        bool generated = IsGenerated();
        WarmUpGenericSerializerPath();

        ShapeDef[] shapes = [AfxShape(), TrtShape(), StorageShape(), SmokeShape(), ModelCacheShape()];
        try
        {
            var bound = new List<string>(shapes.Length);
            long started = Stopwatch.GetTimestamp();
            foreach (ShapeDef shape in shapes)
            {
                object? value = (generated ? shape.Generated : shape.Reflection)();
                bound.Add($"{shape.Name}:{shape.Bound(value)}");
            }

            double totalMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            Append(outputPath, shapeName, totalMicroseconds, totalMicroseconds, 0, string.Join("; ", bound));
        }
        finally
        {
            foreach (ShapeDef shape in shapes)
            {
                shape.Cleanup?.Invoke();
            }
        }
    }

    private static void RunIoBaseline()
    {
        string outputPath = OutputPath();
        string path = ResolveRepoFile("tests", "Trackdub.Infrastructure.Tests", "Fixtures", "NvidiaAfx", "nvidia-afx-runtime.manifest.json");

        long started = Stopwatch.GetTimestamp();
        string json = File.ReadAllText(path);
        double coldMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        GC.KeepAlive(json);

        double[] samples = new double[ReadIterations];
        for (int i = 0; i < samples.Length; i++)
        {
            long began = Stopwatch.GetTimestamp();
            string value = File.ReadAllText(path);
            samples[i] = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
            GC.KeepAlive(value);
        }

        Array.Sort(samples);
        Append(outputPath, "io.read", coldMicroseconds, samples[samples.Length / 2], json.Length, $"chars={json.Length}");
    }

    private static object? ReadStreaming(string path, Func<FileStream, object?> deserialize)
    {
        using FileStream stream = File.OpenRead(path);
        return deserialize(stream);
    }

    // Mirrors the pre-change LocalModelCacheRecordStore.LoadAsync / the generated-metadata
    // replacement: existence guard, async stream, same buffer options on both sides.
    private static LocalModelCacheRecord[] LoadModelCacheIndex(string path, bool generated)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        LocalModelCacheRecord[]? records = generated
            ? JsonSerializer.DeserializeAsync(stream, LocalModelCacheSerializationContext.Default.LocalModelCacheRecordArray).AsTask().GetAwaiter().GetResult()
            : JsonSerializer.DeserializeAsync<LocalModelCacheRecord[]>(stream, ModelCacheReflectionOptions).AsTask().GetAwaiter().GetResult();

        return records ?? [];
    }

    // Warms the generic serializer entry points (and the JIT of this probe's own code paths) so the
    // timed call measures metadata resolution rather than first-call JIT. A throwaway dummy type is
    // used so neither metadata path for the measured contracts is touched.
    private static void WarmUpGenericSerializerPath()
    {
        const string json = """{"Value":"warmup"}""";
        for (int i = 0; i < ReadIterations; i++)
        {
            _ = JsonSerializer.Deserialize<ProbeDummy>(json, new JsonSerializerOptions());
            _ = JsonSerializer.Deserialize(json, ProbeDummyContext.Default.ProbeDummy);
        }
    }

    private static string Describe<T>(T? value, Func<T, string> describe)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return describe(value);
    }

    private static bool IsGenerated() =>
        !string.Equals(Environment.GetEnvironmentVariable("TRACKDUB_JSON_PROBE_MODE"), "reflection", StringComparison.OrdinalIgnoreCase);

    private static string OutputPath() =>
        Environment.GetEnvironmentVariable("TRACKDUB_JSON_PROBE_OUT")
        ?? Path.Join(Path.GetTempPath(), "trackdub-json-latency.jsonl");

    private static void Append(string outputPath, string shape, double coldMicroseconds, double steadyMicroseconds, int jsonChars, string bound) =>
        File.AppendAllLines(outputPath,
        [
            JsonSerializer.Serialize(new
            {
                mode = IsGenerated() ? "generated" : "reflection",
                metric = "firstload",
                shape,
                coldUs = Math.Round(coldMicroseconds, 2),
                steadyUs = Math.Round(steadyMicroseconds, 2),
                jsonChars,
                bound,
                pid = Environment.ProcessId,
            }),
        ]);

    private static string CreateTempDirectory(string prefix)
    {
        string path = Path.Join(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string ResolveRepoFile(params string[] segments)
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            string candidate = Path.Join([directory, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate {string.Join('/', segments)} from the test output directory.");
    }
}

internal sealed class ProbeDummy
{
    public string? Value { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ProbeDummy))]
internal sealed partial class ProbeDummyContext : JsonSerializerContext
{
}
