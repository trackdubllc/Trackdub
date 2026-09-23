// TEMPORARY diagnostic harness - not for commit.
using System.Globalization;
using System.Text.Json;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.ParakeetTdt;
using Trackdub.Inference.Onnx.Qwen3Asr;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Runtime.Planning;
using Xunit;

namespace Trackdub.Inference.Tests;

public sealed class ZzAsrAudioCompareTests
{
    [Fact]
    public async Task Harness()
    {
        string? cases = Environment.GetEnvironmentVariable("CMP_CASES");
        string? outPath = Environment.GetEnvironmentVariable("CMP_OUT");
        if (string.IsNullOrWhiteSpace(cases) || string.IsNullOrWhiteSpace(outPath))
        {
            return;
        }

        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Trackdub\model-cache\tonythethompson");
        var engines = new (string Name, IAudioTranscriptionEngineAdapter Engine)[]
        {
            ("parakeet", new ParakeetTdtOnnxAudioTranscriptionEngine(
                Planner("tonythethompson/parakeet-tdt-0.6b-v3-onnx", "parakeet-tdt-0.6b-v3", ExecutionProviderKind.TensorRTRtx,
                    ParakeetTdtOnnxAudioTranscriptionEngine.EngineFamilyName, Path.Combine(cache, @"parakeet-tdt-0.6b-v3-onnx\encoder-model.onnx")),
                BenchmarkModelPathResolver.CreateDefault())),
            ("qwen", new Qwen3AsrOnnxAudioTranscriptionEngine(
                Planner("tonythethompson/qwen3-asr-0.6b-onnx", "qwen3-asr-0.6b", ExecutionProviderKind.Cpu,
                    "qwen3-asr", Path.Combine(cache, @"qwen3-asr-0.6b-onnx\encoder.onnx")),
                BenchmarkModelPathResolver.CreateDefault())),
        };

        var rows = new List<object>();
        // Each case: clip|condition|wav|start-end;start-end
        foreach (string line in cases.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split('|');
            SpeechRegion[] regions = parts[3]
                .Split(';')
                .Select((span, index) =>
                {
                    string[] se = span.Split('-');
                    return new SpeechRegion(index, double.Parse(se[0], CultureInfo.InvariantCulture), double.Parse(se[1], CultureInfo.InvariantCulture));
                })
                .ToArray();
            foreach ((string name, IAudioTranscriptionEngineAdapter engine) in engines)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                IReadOnlyList<RecognizedTranscriptSegment> segments =
                    await engine.TranscribeAsync(new AudioTranscriptionRequest(parts[2], regions), CancellationToken.None);
                rows.Add(new
                {
                    clip = parts[0],
                    condition = parts[1],
                    engine = name,
                    seconds = sw.Elapsed.TotalSeconds,
                    regions = regions.Length,
                    segments = segments.Select(static s => new { index = s.Index, start = s.StartSeconds, text = s.Text }).ToArray(),
                });
                await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(rows));
            }
        }
    }

    private static StubRuntimePlanner Planner(string modelId, string alias, ExecutionProviderKind provider, string family, string entry) =>
        new(new StageRuntimePlan
        {
            Stage = RuntimeStage.Asr,
            Status = StageRuntimePlanStatus.Ready,
            ModelId = modelId,
            ModelAlias = alias,
            Variant = "default",
            ExecutionProvider = provider,
            EngineFamily = family,
            ModelEntryPath = entry,
        });

    private sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public Task<StageRuntimePlan> PlanAsync(StageRuntimePlanningRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(plan with { Stage = request.Stage });
    }
}
