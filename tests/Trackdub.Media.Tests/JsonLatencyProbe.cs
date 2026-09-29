using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

// TEMPORARY measurement probe for the PR #305 latency question. Not intended to be committed.
//
// Shape: the win-native-deps manifest read that bootstraps media playback. mode=generated calls the
// real loader (generated metadata); mode=reflection reproduces the pre-change loader body, which
// built its own options and deserialized with reflection metadata. Candidate probing and file I/O
// are identical on both sides, so the delta is the metadata cost the change removes.
//
//   TRACKDUB_JSON_PROBE_MODE=reflection|generated  (default generated)
//   TRACKDUB_JSON_PROBE_OUT=<jsonl path>           (default %TEMP%/trackdub-json-latency.jsonl)
public sealed class JsonLatencyProbe
{
    private const int SteadyIterations = 200;

    // Pre-change options, exactly as WinNativeDepsManifestLoader declared them.
    private static readonly JsonSerializerOptions WinndReflectionOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Probe_winnd() => RunWinnd("winnd");

    // Diagnostic: builds the generated context before the timed region so the number separates
    // "build the metadata" from "deserialize with it".
    [Fact]
    public void Probe_winnd_prewarmed_serializers()
    {
        _ = WinNativeDepsSerializationContext.Default.WinNativeDepsManifestRoot;
        RunWinnd("winnd_prewarmed");
    }

    private static void RunWinnd(string shapeName)
    {
        string outputPath = OutputPath();
        bool generated = IsGenerated();
        WarmUpGenericSerializerPath();

        string fixture = ResolveRepoFile("tests", "Trackdub.Media.Tests", "Fixtures", "WinNativeDeps", "win-native-deps.manifest.json");
        string root = Path.Join(Path.GetTempPath(), "trackdub-winnd-probe-" + Guid.NewGuid().ToString("N"));
        string manifestDirectory = Path.Join(root, "runtime");
        Directory.CreateDirectory(manifestDirectory);
        File.Copy(fixture, Path.Join(manifestDirectory, "win-native-deps.manifest.json"));
        string json = File.ReadAllText(Path.Join(manifestDirectory, "win-native-deps.manifest.json"));

        try
        {
            Func<object?> call = generated
                ? () => WinNativeDepsManifestLoader.TryLoadFromDirectory(root)
                : () => ReflectionLoad(root);

            long started = Stopwatch.GetTimestamp();
            object? cold = call();
            double coldMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            ArgumentNullException.ThrowIfNull(cold);
            string bound = $"schemaVersion={((WinNativeDepsManifestRoot)cold).SchemaVersion}, runtimes={((WinNativeDepsManifestRoot)cold).Runtimes!.Count}";

            double[] samples = new double[SteadyIterations];
            for (int i = 0; i < samples.Length; i++)
            {
                long began = Stopwatch.GetTimestamp();
                object? value = call();
                samples[i] = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
                GC.KeepAlive(value);
            }

            Array.Sort(samples);
            Append(outputPath, shapeName, coldMicroseconds, samples[samples.Length / 2], json.Length, bound);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Pre-change TryLoadFromDirectory body: same candidate list, same guards, reflection metadata.
    private static WinNativeDepsManifestRoot? ReflectionLoad(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        string[] candidates =
        [
            Path.Join(baseDirectory, "runtime", "win-native-deps.manifest.json"),
            Path.Join(baseDirectory, "win-native-deps.manifest.json"),
        ];

        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(path);
                WinNativeDepsManifestRoot? root = JsonSerializer.Deserialize<WinNativeDepsManifestRoot>(json, WinndReflectionOptions);
                return root?.SchemaVersion == 1 ? root : null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }

    // Warms the generic serializer entry points so the timed call measures metadata resolution
    // rather than first-call JIT. The dummy type keeps the measured contract's metadata untouched.
    private static void WarmUpGenericSerializerPath()
    {
        const string json = """{"Value":"warmup"}""";
        for (int i = 0; i < 50; i++)
        {
            _ = JsonSerializer.Deserialize<ProbeDummy>(json, new JsonSerializerOptions());
            _ = JsonSerializer.Deserialize(json, ProbeDummyContext.Default.ProbeDummy);
        }
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
