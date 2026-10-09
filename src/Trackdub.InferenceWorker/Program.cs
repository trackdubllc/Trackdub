using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Kokoro;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.Worker;
using Trackdub.Inference.Runtime.Planning;

// ORT 1.30 inference worker (ADR-0017). Standard output carries protocol messages only; all
// diagnostics go to standard error, which the application keeps as the crash tail.
Stream input = Console.OpenStandardInput();
Stream output = Console.OpenStandardOutput();
var writeGate = new SemaphoreSlim(1, 1);
var inFlight = new ConcurrentDictionary<long, CancellationTokenSource>();
var kokoro = new Lazy<KokoroTtsEngine>(() => new KokoroTtsEngine(
    new PlanSuppliedByApplication(),
    BenchmarkModelPathResolver.CreateDefault(),
    new EspeakNgPhonemizer()));

async Task RespondAsync(WorkerMessage message)
{
    await writeGate.WaitAsync().ConfigureAwait(false);
    try
    {
        await InferenceWorkerProtocol.WriteAsync(output, message, CancellationToken.None).ConfigureAwait(false);
    }
    finally
    {
        writeGate.Release();
    }
}

async Task HandleAsync(WorkerMessage request, CancellationToken cancellationToken)
{
    try
    {
        WorkerMessage response = request.Kind switch
        {
            InferenceWorkerProtocol.Hello => new WorkerMessage(request.Id, InferenceWorkerProtocol.Result,
                InferenceWorkerProtocol.ToPayload(DescribeWorker())),
            InferenceWorkerProtocol.TtsSynthesize => new WorkerMessage(request.Id, InferenceWorkerProtocol.Result,
                InferenceWorkerProtocol.ToPayload(await SynthesizeAsync(
                    InferenceWorkerProtocol.FromPayload<WorkerTtsRequest>(request.Payload),
                    cancellationToken).ConfigureAwait(false))),
            _ => throw new NotSupportedException($"Unknown inference worker request '{request.Kind}'."),
        };
        await RespondAsync(response).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[worker] {request.Kind} #{request.Id} failed: {ex}");
        await RespondAsync(new WorkerMessage(request.Id, InferenceWorkerProtocol.Error,
            Error: ex.Message, ErrorType: ex is OperationCanceledException ? nameof(OperationCanceledException) : ex.GetType().Name))
            .ConfigureAwait(false);
    }
    finally
    {
        if (inFlight.TryRemove(request.Id, out CancellationTokenSource? source))
        {
            source.Dispose();
        }
    }
}

async Task<WorkerTtsResult> SynthesizeAsync(WorkerTtsRequest request, CancellationToken cancellationToken)
{
    StageRuntimePlan plan = request.Plan.ToStageRuntimePlan();
    KokoroTtsEngine engine = string.Equals(plan.EngineFamily, KokoroTtsEngine.EngineFamilyName, StringComparison.OrdinalIgnoreCase)
        ? kokoro.Value
        : throw new NotSupportedException($"The inference worker does not host TTS engine family '{plan.EngineFamily}'.");
    TtsSynthesisResult result = await engine.SynthesizeAsync(
        new TtsSynthesisRequest(
            request.Text,
            request.LanguageCode,
            request.Voice,
            request.Speed,
            request.PhonemeOverride,
            TargetDurationSeconds: request.TargetDurationSeconds),
        plan,
        cancellationToken).ConfigureAwait(false);
    return new WorkerTtsResult(
        Convert.ToBase64String(result.WavBytes),
        result.DurationSamples,
        result.SampleRate,
        result.ModelId,
        result.VoiceId,
        result.Provider,
        engine.LastExecutionSummary);
}

static WorkerHelloResult DescribeWorker()
{
    bool cudaListed = CudaOrtProbe.IsCudaProviderListed();
    bool cuDnn = CudaOrtProbe.IsCuDnn9Available();
    string detail = (cudaListed, cuDnn) switch
    {
        (true, true) => "Native ORT CUDA execution provider and cuDNN 9 are available.",
        (false, _) => "This worker's ONNX Runtime build does not list CUDAExecutionProvider.",
        (true, false) => "cuDNN 9 (cudnn64_9.dll) for CUDA 13 was not found on PATH.",
    };
    return new WorkerHelloResult(
        InferenceWorkerProtocol.Version,
        InferenceWorkerProtocol.BuildStamp,
        OrtEnv.Instance().GetVersionString(),
        cudaListed && cuDnn,
        detail);
}

while (await InferenceWorkerProtocol.ReadAsync(input, CancellationToken.None).ConfigureAwait(false) is { } message)
{
    switch (message.Kind)
    {
        case InferenceWorkerProtocol.Shutdown:
            foreach (CancellationTokenSource source in inFlight.Values)
            {
                await source.CancelAsync().ConfigureAwait(false);
            }

            return 0;
        case InferenceWorkerProtocol.Cancel:
            long target = InferenceWorkerProtocol.FromPayload<long>(message.Payload);
            if (inFlight.TryGetValue(target, out CancellationTokenSource? running))
            {
                await running.CancelAsync().ConfigureAwait(false);
            }

            break;
        default:
            var cancellation = new CancellationTokenSource();
            inFlight[message.Id] = cancellation;
            _ = Task.Run(() => HandleAsync(message, cancellation.Token));
            break;
    }
}

return 0;

/// <summary>The worker never plans: the application's planner already chose model and provider.</summary>
internal sealed class PlanSuppliedByApplication : IRuntimePlanner
{
    public Task<StageRuntimePlan> PlanAsync(StageRuntimePlanningRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The inference worker only runs plans supplied by the application.");
}
